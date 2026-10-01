using System.Buffers.Binary;
using System.Security.Cryptography;
using StorageInventory.Core.Scanning;

namespace StorageInventory.Core.Spool;

/// <summary>Where a <see cref="SpoolWriter"/> is in its life.</summary>
internal enum SpoolWriterState
{
    NotStarted,

    /// <summary>Appending file and error records, then the folder section.</summary>
    Writing,

    /// <summary>Run index, trailer and digest written after <c>OnScanEnded(Finished)</c>: the only state in which the
    /// stream holds a spool that can pass verification.</summary>
    Sealed,

    /// <summary>The scan was cancelled or failed: nothing more is written and no trailer exists.</summary>
    Abandoned,

    /// <summary>The writer itself failed (an I/O error, or an observation stream that broke the contract). It never
    /// seals afterwards, whatever end it is told about.</summary>
    Faulted,
}

/// <summary>
/// Writes the observation spool (format 1, <see cref="SpoolFormat"/>) as an <b>isolated</b> observer of the scan
/// (SINK-04): its failures fault it and never the scan. Appends only, in emission order, through one 1 MiB buffer;
/// every byte goes through an incremental SHA-256 in write order, which, since nothing is ever rewritten, is physical
/// order (SPOOL-07, SPOOL-12). Memory: the buffer, the hash state and the run index (16 bytes per run) plus one bit per
/// folder index; never anything per file (SPOOL-21, INV-13).
/// </summary>
/// <remarks>
/// <para>The writer seals (run index, trailer, digest, flush; no fsync) only on <c>OnScanEnded(Finished)</c>, and only
/// if what it recorded agrees with the scan's totals. A cancelled or failed scan, or a writer fault, leaves no trailer
/// and no digest, so the bytes can never pass <see cref="SpoolReader.Verify"/>.</para>
/// <para>It does not own the stream: it never closes, truncates or deletes it. In C1 the stream is supplied by tests;
/// the spool file, its exclusive delete-on-close handle and its release belong to <c>ReportRun</c> in C5.</para>
/// </remarks>
internal sealed class SpoolWriter : IScanObserver, IDisposable
{
    public const int BufferBytes = 1 << 20;

    private readonly Stream _stream;
    private readonly byte[] _captureToken;
    private readonly TimeProvider _time;
    private IncrementalHash? _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private byte[]? _buffer = new byte[BufferBytes];
    private int _used;
    private long _position;

    // Run index (emission order) and the set of folder indexes that already started a run.
    private readonly List<(int Folder, long Offset, int Count)> _runs = [];
    private ulong[] _runStarted = new ulong[64];
    private int _currentRunFolder = -1;

    // What was written, for the trailer and the self-check against the scan's totals.
    private long _fileRecords, _errorRecords, _folderRecords, _bytes;
    private long _scanErrors, _reparseSkipped, _fileReparse, _locallyIncomplete, _affectedAncestors;
    private long _folderSectionOffset = -1;
    private long _expectedFolders = -1;

    /// <param name="destination">An empty, writable stream. The writer appends to it and never closes it.</param>
    /// <param name="captureToken">16 bytes identifying this capture (SPOOL-06 (2)); the reader requires the same token.</param>
    /// <param name="time">Source of the header's created time; the system clock if null.</param>
    public SpoolWriter(Stream destination, byte[] captureToken, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(captureToken);
        if (!destination.CanWrite) throw new ArgumentException("The spool stream must be writable.", nameof(destination));
        if (destination.CanSeek && (destination.Length != 0 || destination.Position != 0))
        {
            throw new ArgumentException("The spool stream must be empty: a spool is append-only from its first byte.", nameof(destination));
        }
        if (captureToken.Length != SpoolFormat.CaptureTokenLength)
        {
            throw new ArgumentException($"The capture token must be {SpoolFormat.CaptureTokenLength} bytes.", nameof(captureToken));
        }
        _stream = destination;
        _captureToken = (byte[])captureToken.Clone();
        _time = time ?? TimeProvider.System;
    }

    public SpoolWriterState State { get; private set; } = SpoolWriterState.NotStarted;

    /// <summary>Bytes written so far (buffered bytes included).</summary>
    public long Length => _position;

    public void OnScanStarted(in ScanStartInfo start)
    {
        Require(State == SpoolWriterState.NotStarted, "the spool was already started");
        var runId = start.RunId;
        if (runId.Length is 0 or > SpoolFormat.MaxRunIdLength) Fail($"a run ID of {runId.Length} code units cannot be recorded");
        State = SpoolWriterState.Writing;
        try
        {
            var span = Reserve(SpoolFormat.HeaderFixedBytes);
            SpoolFormat.Magic.CopyTo(span);
            BinaryPrimitives.WriteUInt16LittleEndian(span[8..], SpoolFormat.FormatVersion);
            BinaryPrimitives.WriteUInt16LittleEndian(span[10..], SpoolFormat.Flags);
            _captureToken.CopyTo(span[12..]);
            BinaryPrimitives.WriteUInt16LittleEndian(span[28..], (ushort)runId.Length);
            WriteUtf16(runId);
            span = Reserve(SpoolFormat.HeaderTailBytes);
            BinaryPrimitives.WriteUInt16LittleEndian(span, SpoolFormat.ScannerContract);
            BinaryPrimitives.WriteInt64LittleEndian(span[2..], _time.GetUtcNow().UtcDateTime.Ticks);
        }
        catch
        {
            Faulted();
            throw;
        }
    }

    public void OnFile(in FileInventoryRecord file, int folderIndex)
    {
        Require(State == SpoolWriterState.Writing && _folderSectionOffset < 0, "a file record arrived outside enumeration");
        if (folderIndex < 0) Fail($"folder index {folderIndex} is negative");
        if (file.SizeBytes < 0) Fail($"file size {file.SizeBytes} is negative");
        if (file.FileName.Length > ushort.MaxValue) Fail($"a name of {file.FileName.Length} code units cannot be recorded");

        var name = file.FileName;
        try
        {
            TrackRun(folderIndex);
            var span = Reserve(1 + 4 + 2);
            span[0] = SpoolFormat.FileTag;
            BinaryPrimitives.WriteInt32LittleEndian(span[1..], folderIndex);
            BinaryPrimitives.WriteUInt16LittleEndian(span[5..], (ushort)name.Length);
            WriteUtf16(name);
            span = Reserve(8 * 4 + 4);
            BinaryPrimitives.WriteInt64LittleEndian(span, file.SizeBytes);
            BinaryPrimitives.WriteInt64LittleEndian(span[8..], SpoolFormat.Ticks(file.CreatedUtc));
            BinaryPrimitives.WriteInt64LittleEndian(span[16..], SpoolFormat.Ticks(file.ModifiedUtc));
            BinaryPrimitives.WriteInt64LittleEndian(span[24..], SpoolFormat.Ticks(file.LastAccessUtc));
            BinaryPrimitives.WriteInt32LittleEndian(span[32..], (int)file.Attributes);
            _fileRecords++;
            _bytes = checked(_bytes + file.SizeBytes);
        }
        catch
        {
            Faulted();
            throw;
        }
    }

    public void OnError(ScanErrorRecord error)
    {
        Require(State == SpoolWriterState.Writing && _folderSectionOffset < 0, "an error record arrived outside enumeration");
        try
        {
            var code = SpoolFormat.ErrorCode(error.Type);
            var span = Reserve(1 + 1 + 4);
            span[0] = SpoolFormat.ErrorTag;
            span[1] = code;
            BinaryPrimitives.WriteUInt32LittleEndian(span[2..], (uint)error.Path.Length);
            WriteUtf16(error.Path);
            span = Reserve(4);
            BinaryPrimitives.WriteUInt32LittleEndian(span, (uint)error.Message.Length);
            WriteUtf16(error.Message);
            _errorRecords++;
            if (error.Type == ScanErrorType.ReparsePointSkipped) _reparseSkipped++;
            else if (error.Type == ScanErrorType.ReparsePointFile) _fileReparse++;
            else _scanErrors++;
        }
        catch
        {
            Faulted();
            throw;
        }
    }

    public void OnFolderFinalised(int index, int parentIndex, FolderInventoryRecord folder)
    {
        Require(State == SpoolWriterState.Writing, "a folder record arrived outside the scan");
        if (_folderSectionOffset < 0)
        {
            // The first finalised folder opens the folder section: no file or error record may follow it.
            if (index != 0 || parentIndex != -1) Fail("the folder section must start with the root (index 0, parent -1)");
            _folderSectionOffset = _position;
            _currentRunFolder = -1;
            _expectedFolders = checked(folder.TotalSubfolderCount + 1);
        }
        if (index != _folderRecords) Fail($"folder {index} arrived where folder {_folderRecords} was due (ascending, each once)");
        if (index != 0 && (parentIndex < 0 || parentIndex >= index)) Fail($"folder {index} has parent {parentIndex}, which is not below it");
        if (index >= _expectedFolders) Fail($"folder {index} is beyond the {_expectedFolders} folders the root accounts for");
        if (folder.Name.Length > ushort.MaxValue) Fail($"a folder name of {folder.Name.Length} code units cannot be recorded");

        try
        {
            var status = SpoolFormat.StatusCode(folder.Status);
            var reason = folder.StatusReason is { } r ? SpoolFormat.ErrorCode(r) : SpoolFormat.NoStatusReason;
            var span = Reserve(1 + 4 + 4 + 2);
            span[0] = SpoolFormat.FolderTag;
            BinaryPrimitives.WriteInt32LittleEndian(span[1..], index);
            BinaryPrimitives.WriteInt32LittleEndian(span[5..], parentIndex);
            BinaryPrimitives.WriteUInt16LittleEndian(span[9..], (ushort)folder.Name.Length);
            WriteUtf16(folder.Name);
            span = Reserve(3 + 4 + 8 * 2 + 8 * 6 + 8);
            span[0] = status;
            span[1] = reason;
            span[2] = folder.SubtreeComplete ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteInt32LittleEndian(span[3..], (int)folder.Attributes);
            BinaryPrimitives.WriteInt64LittleEndian(span[7..], SpoolFormat.Ticks(folder.CreatedUtc));
            BinaryPrimitives.WriteInt64LittleEndian(span[15..], SpoolFormat.Ticks(folder.ModifiedUtc));
            BinaryPrimitives.WriteInt64LittleEndian(span[23..], folder.DirectSizeBytes);
            BinaryPrimitives.WriteInt64LittleEndian(span[31..], folder.TotalSizeBytes);
            BinaryPrimitives.WriteInt64LittleEndian(span[39..], folder.DirectFileCount);
            BinaryPrimitives.WriteInt64LittleEndian(span[47..], folder.TotalFileCount);
            BinaryPrimitives.WriteInt64LittleEndian(span[55..], folder.DirectSubfolderCount);
            BinaryPrimitives.WriteInt64LittleEndian(span[63..], folder.TotalSubfolderCount);
            BinaryPrimitives.WriteInt64LittleEndian(span[71..], folder.LargestFileBytes ?? SpoolFormat.NullSize);
            _folderRecords++;
            if (folder.Status is FolderScanStatus.Unreadable or FolderScanStatus.Partial) _locallyIncomplete++;
            else if (!folder.SubtreeComplete) _affectedAncestors++;
        }
        catch
        {
            Faulted();
            throw;
        }
    }

    public void OnScanEnded(in ScanEndInfo end)
    {
        if (State is SpoolWriterState.Faulted or SpoolWriterState.Abandoned or SpoolWriterState.Sealed) return;
        if (end.Outcome != ScanEndOutcome.Finished || State != SpoolWriterState.Writing)
        {
            // Cancelled or failed: never sealed, never a candidate for anything (CAN-01a to CAN-01c).
            State = SpoolWriterState.Abandoned;
            Release();
            return;
        }

        var totals = end.Totals!;
        if (_folderSectionOffset < 0) Fail("the scan finished without a folder section");
        if (_folderRecords != _expectedFolders) Fail($"{_folderRecords} folders were finalised, the root accounts for {_expectedFolders}");
        CheckTotal("files", _fileRecords, totals.Files);
        CheckTotal("bytes", _bytes, totals.Bytes);
        CheckTotal("folders", _folderRecords, totals.Folders);
        CheckTotal("scan errors", _scanErrors, totals.ScanErrors);
        CheckTotal("reparse points skipped", _reparseSkipped, totals.ReparsePointsSkipped);
        CheckTotal("file reparse points", _fileReparse, totals.FileReparsePoints);
        CheckTotal("locally incomplete folders", _locallyIncomplete, totals.LocallyIncompleteFolders);
        CheckTotal("affected ancestor folders", _affectedAncestors, totals.AffectedAncestorFolders);

        try
        {
            var runIndexOffset = _position;
            foreach (var (folder, offset, count) in _runs)
            {
                var entry = Reserve(SpoolFormat.RunEntryBytes);
                BinaryPrimitives.WriteInt32LittleEndian(entry, folder);
                BinaryPrimitives.WriteInt64LittleEndian(entry[4..], offset);
                BinaryPrimitives.WriteInt32LittleEndian(entry[12..], count);
            }

            var t = Reserve(SpoolFormat.TrailerBytes);
            t[0] = SpoolFormat.TrailerTag;
            var fields = new[]
            {
                _fileRecords, _errorRecords, _folderRecords, _runs.Count,
                totals.Files, totals.Folders, totals.Bytes, totals.ScanErrors, totals.ReparsePointsSkipped,
                totals.FileReparsePoints, totals.LocallyIncompleteFolders, totals.AffectedAncestorFolders,
                _folderSectionOffset, runIndexOffset,
            };
            for (var i = 0; i < fields.Length; i++) BinaryPrimitives.WriteInt64LittleEndian(t[(1 + 8 * i)..], fields[i]);

            Flush();   // hashes every byte before the digest
            var digest = _hash!.GetHashAndReset();
            _stream.Write(digest);
            _position += digest.Length;
            _stream.Flush();
            State = SpoolWriterState.Sealed;
        }
        catch
        {
            Faulted();
            throw;
        }
        Release();
    }

    public void Dispose() => Release();

    private void TrackRun(int folderIndex)
    {
        if (folderIndex == _currentRunFolder)
        {
            var last = _runs[^1];
            if (last.Count == int.MaxValue) Fail($"folder {folderIndex} has more files than one run can count");
            _runs[^1] = last with { Count = last.Count + 1 };
            return;
        }
        // A new run. SINK-03 #2: a folder's files are contiguous, so a folder can start only one run.
        var word = folderIndex >> 6;
        if (word >= _runStarted.Length) Array.Resize(ref _runStarted, Math.Max(word + 1, _runStarted.Length * 2));
        var bit = 1UL << (folderIndex & 63);
        if ((_runStarted[word] & bit) != 0) Fail($"the files of folder {folderIndex} are not contiguous");
        _runStarted[word] |= bit;
        _runs.Add((folderIndex, _position, 1));
        _currentRunFolder = folderIndex;
    }

    private Span<byte> Reserve(int bytes)
    {
        if (BufferBytes - _used < bytes) Flush();
        var span = _buffer.AsSpan(_used, bytes);
        _used += bytes;
        _position += bytes;
        return span;
    }

    private void WriteUtf16(string text)
    {
        var remaining = text.AsSpan();
        while (remaining.Length > 0)
        {
            if (BufferBytes - _used < 2) Flush();
            var chars = Math.Min(remaining.Length, (BufferBytes - _used) / 2);
            SpoolFormat.CopyUtf16(remaining[..chars], _buffer.AsSpan(_used, chars * 2));
            _used += chars * 2;
            _position += chars * 2;
            remaining = remaining[chars..];
        }
    }

    private void Flush()
    {
        if (_used == 0) return;
        _hash!.AppendData(_buffer!, 0, _used);
        _stream.Write(_buffer!, 0, _used);
        _used = 0;
    }

    private void CheckTotal(string what, long recorded, long expected)
    {
        if (recorded != expected) Fail($"the spool recorded {recorded} {what}, the scan reports {expected}");
    }

    private void Require(bool condition, string violation)
    {
        if (!condition) Fail(violation);
    }

    /// <summary>Faults the writer (see <see cref="Faulted"/>) for an observation stream that breaks the contract.</summary>
    private void Fail(string violation)
    {
        Faulted();
        throw new SpoolFormatException("Spool not written: " + violation + ".");
    }

    /// <summary>Any failure while writing (I/O, an overflow, a value without a code, or class C) faults the writer for
    /// good: it releases its buffer and never seals.</summary>
    private void Faulted()
    {
        State = SpoolWriterState.Faulted;
        Release();
    }

    private void Release()
    {
        _buffer = null;
        _used = 0;
        _hash?.Dispose();
        _hash = null;
    }
}

using System.Buffers.Binary;
using System.Security.Cryptography;

namespace StorageInventory.Core.Spool;

/// <summary>Why a spool failed the verification pass V.</summary>
internal enum SpoolDefect
{
    None,

    /// <summary>Shorter than a header plus a trailer (step 1).</summary>
    TooShort,

    /// <summary>SHA-256 of bytes [0, L - 32) differs from the last 32 bytes: a byte changed, removed or added after the
    /// seal (step 1). Reported in preference to any structural defect.</summary>
    DigestMismatch,

    BadMagic,
    UnsupportedFormat,
    UnsupportedFlags,

    /// <summary>The header's capture token is not the expected one (SPOOL-06 (2)).</summary>
    CaptureTokenMismatch,

    /// <summary>Run ID length, scanner contract or created time out of range.</summary>
    BadHeader,

    /// <summary>A tag in the record region other than 0x01 or 0x02 before the folder section (step 3).</summary>
    BadRecordTag,

    /// <summary>A length, index, size, time or code out of bounds (step 3).</summary>
    RecordOutOfBounds,

    /// <summary>Records per tag differ from the trailer's counts (step 3).</summary>
    RecordCountMismatch,

    /// <summary>A folder index starts two runs: its files are not contiguous (step 4).</summary>
    RunSplit,

    /// <summary>The run index differs, entry by entry, from the runs rebuilt from the records (step 4).</summary>
    RunIndexMismatch,

    /// <summary>Folder indexes not 0..n-1 ascending, a parent not below its child, or a record that is not a folder
    /// where one is due (step 5).</summary>
    FolderSectionInvalid,

    /// <summary>Records and folders do not close: a file run or child folder under a folder that was not listed, a
    /// folder whose direct counts or bytes differ from its records, or completeness flags that contradict the statuses
    /// (step 5, mirroring FolderAggregator.Verify and the specification's §9.5).</summary>
    FolderClosureViolation,

    /// <summary>Totals recomputed from the records and folders differ from the trailer's (step 5).</summary>
    TotalsMismatch,

    /// <summary>The trailer's totals differ from the scan result's totals (step 5, SPOOL-12).</summary>
    ResultTotalsMismatch,

    /// <summary>Trailer tag, run count or offsets inconsistent with the positions found (step 6).</summary>
    TrailerInvalid,
}

/// <summary>The outcome of the verification pass V. <see cref="Spool"/> exists only when it passed.</summary>
internal sealed record SpoolVerification(SpoolDefect Defect, string Detail, VerifiedSpool? Spool)
{
    public bool Passed => Defect == SpoolDefect.None;
}

/// <summary>
/// The verification pass V (SPOOL-23, D-50), and the only way to obtain a <see cref="VerifiedSpool"/>. No record of a
/// spool is returned to any caller before V has passed.
/// </summary>
/// <remarks>
/// <para>V is <b>one sequential read</b> of the stream from offset 0 in physical order. While reading it feeds bytes
/// [0, L - 32) to SHA-256 and parses the structure; if the structure breaks, it stops parsing but keeps reading to the
/// end for the digest. A digest mismatch is reported in preference to any structural defect. The structural steps are
/// SPOOL-23 (2) to (6): header; record region (tags, bounds, counts); runs rebuilt from the records (a maximal sequence
/// of file records with one folder index, errors allowed between them; no folder index may start two runs) compared
/// with the stored run index entry by entry; the folder section (n = the root's total subfolders + 1 records, indexes
/// 0..n-1 ascending, parent below child) and the totals recomputed from it; and the trailer's tag and offsets.
/// V also checks closure between records and folders (direct counts and bytes, statuses, completeness flags).</para>
/// <para><b>What V proves</b> (G0F-M07): integrity relative to the seal, and structural consistency even against a
/// rewrite that recomputes the digest. <b>What it does not prove:</b> semantic order or content against an outside
/// authority. A rewrite that keeps every structural invariant and recomputes the digest is accepted; for example two
/// file records of the same folder exchanged within its run. That order is protected by the writer (append-only,
/// emission order) and, from C5, by the exclusive handle.</para>
/// <para>Memory: O(runs + folders) (a few bytes per folder), never O(files).</para>
/// </remarks>
internal static class SpoolReader
{
    private const int ReadBufferBytes = 1 << 20;
    private const long CancellationCheckBytes = 64L << 20;   // the cadence of CAN-01d, for later gates

    /// <param name="spool">The spool, readable and seekable; read from offset 0, never written.</param>
    /// <param name="expectedCaptureToken">The capture token the header must carry.</param>
    /// <param name="expectedTotals">The scan result's totals, which the trailer must equal; null to skip that check.</param>
    public static SpoolVerification Verify(Stream spool, byte[] expectedCaptureToken, ScanTotals? expectedTotals, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spool);
        ArgumentNullException.ThrowIfNull(expectedCaptureToken);
        if (!spool.CanRead || !spool.CanSeek) throw new ArgumentException("The spool stream must be readable and seekable.", nameof(spool));

        var length = spool.Length;
        if (length < SpoolFormat.MinimumLength)
        {
            return new SpoolVerification(SpoolDefect.TooShort, $"{length} bytes; a spool has at least {SpoolFormat.MinimumLength}", null);
        }

        spool.Seek(0, SeekOrigin.Begin);
        using var pass = new HashingPass(spool, length, cancellationToken);
        var parser = new StructureParser(pass, length, expectedCaptureToken, expectedTotals);
        var structural = parser.Parse();
        pass.DrainToEnd();
        if (!pass.DigestMatches(out var digestDetail)) return new SpoolVerification(SpoolDefect.DigestMismatch, digestDetail, null);
        if (structural.Defect != SpoolDefect.None) return new SpoolVerification(structural.Defect, structural.Detail, null);
        return new SpoolVerification(SpoolDefect.None, "", new VerifiedSpool(spool, length, parser.Header!, parser.RecordRegionOffset, parser.Trailer!, parser.Runs));
    }

    /// <summary>Reads the stream once, sequentially; hashes [0, L - 32) and keeps the last 32 bytes.</summary>
    private sealed class HashingPass(Stream stream, long length, CancellationToken cancellationToken) : IDisposable
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly byte[] _buffer = new byte[ReadBufferBytes];
        private readonly byte[] _stored = new byte[SpoolFormat.DigestBytes];
        private readonly long _hashEnd = length - SpoolFormat.DigestBytes;
        private int _start, _end;          // unread bytes are _buffer[_start.._end)
        private long _read;                // bytes read from the stream so far
        private long _nextCancellationCheck = CancellationCheckBytes;

        /// <summary>Absolute offset of the next unconsumed byte.</summary>
        public long Position => _read - (_end - _start);

        public bool EndedEarly { get; private set; }

        /// <summary>Makes at least <paramref name="count"/> unread bytes available (count ≤ the buffer size).</summary>
        public bool Ensure(int count)
        {
            if (_end - _start >= count) return true;
            if (_start > 0)
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }
            while (_end - _start < count && Fill()) { }
            return _end - _start >= count;
        }

        public ReadOnlySpan<byte> Take(int count)
        {
            if (!Ensure(count)) throw new EndOfStreamException();
            var span = _buffer.AsSpan(_start, count);
            _start += count;
            return span;
        }

        public void Skip(long count)
        {
            while (count > 0)
            {
                if (_end == _start && !Fill()) throw new EndOfStreamException();
                var n = (int)Math.Min(count, _end - _start);
                _start += n;
                count -= n;
            }
        }

        public void DrainToEnd()
        {
            _start = _end;
            while (Fill()) _start = _end;
        }

        public bool DigestMatches(out string detail)
        {
            if (EndedEarly || _read != length)
            {
                detail = $"the stream ended after {_read} of {length} bytes";
                return false;
            }
            var computed = _hash.GetHashAndReset();
            if (CryptographicOperations.FixedTimeEquals(computed, _stored))
            {
                detail = "";
                return true;
            }
            detail = $"SHA-256 of bytes [0, {_hashEnd}) is {Convert.ToHexString(computed)}, the spool stores {Convert.ToHexString(_stored)}";
            return false;
        }

        private bool Fill()
        {
            if (_read >= length) return false;
            if (_end == _buffer.Length)
            {
                if (_start == 0) return false;   // cannot happen: requests never exceed the buffer
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }
            var want = (int)Math.Min(_buffer.Length - _end, length - _read);
            var got = stream.Read(_buffer, _end, want);
            if (got <= 0)
            {
                EndedEarly = true;
                return false;
            }

            // Hash the part below L - 32; keep the part at or above it as the stored digest.
            var chunkStart = _read;
            var hashed = (int)Math.Clamp(_hashEnd - chunkStart, 0, got);
            if (hashed > 0) _hash.AppendData(_buffer, _end, hashed);
            for (var i = hashed; i < got; i++) _stored[chunkStart + i - _hashEnd] = _buffer[_end + i];

            _end += got;
            _read += got;
            if (_read >= _nextCancellationCheck)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _nextCancellationCheck += CancellationCheckBytes;
            }
            return true;
        }

        public void Dispose() => _hash.Dispose();
    }

    private sealed class Defect(SpoolDefect kind, string detail) : Exception(detail)
    {
        public SpoolDefect Kind { get; } = kind;
    }

    /// <summary>SPOOL-23 steps (2) to (6), on the bytes the hashing pass delivers, in order.</summary>
    private sealed class StructureParser(HashingPass pass, long length, byte[] expectedToken, ScanTotals? expectedTotals)
    {
        private readonly long _trailerStart = length - SpoolFormat.DigestBytes - SpoolFormat.TrailerBytes;
        private readonly List<SpoolRun> _runs = [];
        private readonly List<long> _runBytes = [];

        public SpoolHeader? Header { get; private set; }
        public long RecordRegionOffset { get; private set; }
        public SpoolTrailer? Trailer { get; private set; }
        public IReadOnlyList<SpoolRun> Runs => _runs;

        public (SpoolDefect Defect, string Detail) Parse()
        {
            try
            {
                ParseCore();
                return (SpoolDefect.None, "");
            }
            catch (Defect d)
            {
                return (d.Kind, d.Message);
            }
            catch (EndOfStreamException)
            {
                return (SpoolDefect.RecordOutOfBounds, "a record runs past the end of the stream");
            }
        }

        private static Defect Bad(SpoolDefect kind, string detail) => new(kind, detail);

        /// <summary>The i-th 64-bit field of the trailer (after its tag).</summary>
        private static long I64(ReadOnlySpan<byte> trailer, int i) => BinaryPrimitives.ReadInt64LittleEndian(trailer[(1 + 8 * i)..]);

        private void Need(long bytes, SpoolDefect kind, string what)
        {
            if (bytes < 0 || pass.Position + bytes > _trailerStart) throw Bad(kind, $"{what} at offset {pass.Position} runs into the trailer");
        }

        private void ParseCore()
        {
            // (2) Header.
            var fixedHeader = pass.Take(SpoolFormat.HeaderFixedBytes);
            if (!fixedHeader[..8].SequenceEqual(SpoolFormat.Magic)) throw Bad(SpoolDefect.BadMagic, "not a StorageInventory spool");
            var format = BinaryPrimitives.ReadUInt16LittleEndian(fixedHeader[8..]);
            if (format != SpoolFormat.FormatVersion) throw Bad(SpoolDefect.UnsupportedFormat, $"format {format}");
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(fixedHeader[10..]);
            if (flags != SpoolFormat.Flags) throw Bad(SpoolDefect.UnsupportedFlags, $"flags 0x{flags:X4}");
            var token = fixedHeader.Slice(12, SpoolFormat.CaptureTokenLength).ToArray();
            if (!CryptographicOperations.FixedTimeEquals(token, expectedToken)) throw Bad(SpoolDefect.CaptureTokenMismatch, "the capture token is not this capture's");
            var runIdLength = BinaryPrimitives.ReadUInt16LittleEndian(fixedHeader[28..]);
            if (runIdLength is 0 or > SpoolFormat.MaxRunIdLength) throw Bad(SpoolDefect.BadHeader, $"run ID length {runIdLength}");
            Need(2L * runIdLength + SpoolFormat.HeaderTailBytes, SpoolDefect.BadHeader, "the header");
            var runId = SpoolFormat.ReadUtf16(pass.Take(2 * runIdLength));
            var tail = pass.Take(SpoolFormat.HeaderTailBytes);
            var contract = BinaryPrimitives.ReadUInt16LittleEndian(tail);
            if (contract != SpoolFormat.ScannerContract) throw Bad(SpoolDefect.BadHeader, $"scanner contract {contract}");
            var created = BinaryPrimitives.ReadInt64LittleEndian(tail[2..]);
            if (created == SpoolFormat.NullTicks || !SpoolFormat.ValidTicks(created)) throw Bad(SpoolDefect.BadHeader, $"created ticks {created}");
            Header = new SpoolHeader(token, runId, contract, new DateTime(created, DateTimeKind.Utc));
            RecordRegionOffset = pass.Position;

            // (3) Record region, rebuilding the runs (4) as it goes.
            long fileRecords = 0, errorRecords = 0, bytes = 0, scanErrors = 0, reparseSkipped = 0, fileReparse = 0;
            var currentRunFolder = -1;
            long folderSectionOffset;
            while (true)
            {
                if (pass.Position >= _trailerStart) throw Bad(SpoolDefect.FolderSectionInvalid, "the record region ends without a folder section");
                var recordOffset = pass.Position;
                var tag = pass.Take(1)[0];
                if (tag == SpoolFormat.FolderTag)
                {
                    folderSectionOffset = recordOffset;
                    break;
                }
                if (tag == SpoolFormat.FileTag)
                {
                    Need(SpoolFormat.FileRecordFixedBytes - 1, SpoolDefect.RecordOutOfBounds, "a file record");
                    var head = pass.Take(4 + 2);
                    var folderIndex = BinaryPrimitives.ReadInt32LittleEndian(head);
                    var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(head[4..]);
                    if (folderIndex < 0) throw Bad(SpoolDefect.RecordOutOfBounds, $"file record at {recordOffset}: folder index {folderIndex}");
                    Need(2L * nameLength + 8 * 4 + 4, SpoolDefect.RecordOutOfBounds, "a file name");
                    pass.Skip(2L * nameLength);
                    var values = pass.Take(8 * 4 + 4);
                    var size = BinaryPrimitives.ReadInt64LittleEndian(values);
                    if (size < 0) throw Bad(SpoolDefect.RecordOutOfBounds, $"file record at {recordOffset}: size {size}");
                    for (var i = 1; i <= 3; i++)
                    {
                        var ticks = BinaryPrimitives.ReadInt64LittleEndian(values[(8 * i)..]);
                        if (!SpoolFormat.ValidTicks(ticks)) throw Bad(SpoolDefect.RecordOutOfBounds, $"file record at {recordOffset}: ticks {ticks}");
                    }
                    if (folderIndex == currentRunFolder)
                    {
                        var last = _runs[^1];
                        if (last.Count == int.MaxValue) throw Bad(SpoolDefect.RecordOutOfBounds, "a run longer than a run can count");
                        _runs[^1] = last with { Count = last.Count + 1 };
                        _runBytes[^1] += size;
                    }
                    else
                    {
                        _runs.Add(new SpoolRun(folderIndex, recordOffset, 1, fileRecords));
                        _runBytes.Add(size);
                        currentRunFolder = folderIndex;
                    }
                    fileRecords++;
                    bytes += size;
                    if (bytes < 0) throw Bad(SpoolDefect.RecordOutOfBounds, "the file sizes overflow");
                }
                else if (tag == SpoolFormat.ErrorTag)
                {
                    Need(SpoolFormat.ErrorRecordFixedBytes - 1, SpoolDefect.RecordOutOfBounds, "an error record");
                    var head = pass.Take(1 + 4);
                    if (!SpoolFormat.TryErrorType(head[0], out var type)) throw Bad(SpoolDefect.RecordOutOfBounds, $"error record at {recordOffset}: type code {head[0]}");
                    var pathLength = BinaryPrimitives.ReadUInt32LittleEndian(head[1..]);
                    Need(2L * pathLength + 4, SpoolDefect.RecordOutOfBounds, "an error path");
                    pass.Skip(2L * pathLength);
                    var messageLength = BinaryPrimitives.ReadUInt32LittleEndian(pass.Take(4));
                    Need(2L * messageLength, SpoolDefect.RecordOutOfBounds, "an error message");
                    pass.Skip(2L * messageLength);
                    errorRecords++;
                    if (type == ScanErrorType.ReparsePointSkipped) reparseSkipped++;
                    else if (type == ScanErrorType.ReparsePointFile) fileReparse++;
                    else scanErrors++;
                }
                else
                {
                    throw Bad(SpoolDefect.BadRecordTag, $"tag 0x{tag:X2} at offset {recordOffset}");
                }
            }

            // (4) No folder index may start two runs.
            var byFolder = _runs.Select((r, i) => (r.FolderIndex, Run: i)).OrderBy(x => x.FolderIndex).ToArray();
            for (var i = 1; i < byFolder.Length; i++)
            {
                if (byFolder[i].FolderIndex == byFolder[i - 1].FolderIndex)
                {
                    throw Bad(SpoolDefect.RunSplit, $"folder {byFolder[i].FolderIndex} starts two runs");
                }
            }

            // (5) Folder section: the root first; it says how many folders follow.
            var folders = ParseFolders(byFolder, out var folderTotals);
            var runIndexOffset = pass.Position;

            // (4) The stored run index, entry by entry.
            var runIndexBytes = _trailerStart - runIndexOffset;
            if (runIndexBytes != (long)_runs.Count * SpoolFormat.RunEntryBytes)
            {
                throw Bad(SpoolDefect.RunIndexMismatch, $"the run index holds {runIndexBytes} bytes; {_runs.Count} runs were rebuilt from the records");
            }
            for (var i = 0; i < _runs.Count; i++)
            {
                var entry = pass.Take(SpoolFormat.RunEntryBytes);
                var folder = BinaryPrimitives.ReadInt32LittleEndian(entry);
                var offset = BinaryPrimitives.ReadInt64LittleEndian(entry[4..]);
                var count = BinaryPrimitives.ReadInt32LittleEndian(entry[12..]);
                var rebuilt = _runs[i];
                if (folder != rebuilt.FolderIndex || offset != rebuilt.Offset || count != rebuilt.Count)
                {
                    throw Bad(SpoolDefect.RunIndexMismatch,
                        $"run {i}: stored (folder {folder}, offset {offset}, count {count}), rebuilt (folder {rebuilt.FolderIndex}, offset {rebuilt.Offset}, count {rebuilt.Count})");
                }
            }

            // (6) Trailer.
            var t = pass.Take(SpoolFormat.TrailerBytes);
            if (t[0] != SpoolFormat.TrailerTag) throw Bad(SpoolDefect.TrailerInvalid, $"trailer tag 0x{t[0]:X2}");
            var trailer = new SpoolTrailer(I64(t, 0), I64(t, 1), I64(t, 2), I64(t, 3), new ScanTotals
            {
                Files = I64(t, 4), Folders = I64(t, 5), Bytes = I64(t, 6), ScanErrors = I64(t, 7), ReparsePointsSkipped = I64(t, 8),
                FileReparsePoints = I64(t, 9), LocallyIncompleteFolders = I64(t, 10), AffectedAncestorFolders = I64(t, 11),
            }, I64(t, 12), I64(t, 13));

            if (trailer.FileRecords != fileRecords || trailer.ErrorRecords != errorRecords || trailer.FolderRecords != folders)
            {
                throw Bad(SpoolDefect.RecordCountMismatch,
                    $"trailer counts (files {trailer.FileRecords}, errors {trailer.ErrorRecords}, folders {trailer.FolderRecords}) differ from the records (files {fileRecords}, errors {errorRecords}, folders {folders})");
            }
            if (trailer.Runs != _runs.Count) throw Bad(SpoolDefect.TrailerInvalid, $"the trailer counts {trailer.Runs} runs; the run index holds {_runs.Count}");
            if (trailer.FolderSectionOffset != folderSectionOffset || trailer.RunIndexOffset != runIndexOffset)
            {
                throw Bad(SpoolDefect.TrailerInvalid,
                    $"trailer offsets (folders {trailer.FolderSectionOffset}, run index {trailer.RunIndexOffset}) differ from the positions found ({folderSectionOffset}, {runIndexOffset})");
            }

            if (folderTotals.Files != fileRecords || folderTotals.Bytes != bytes)
            {
                throw Bad(SpoolDefect.TotalsMismatch,
                    $"the root totals ({folderTotals.Files} files, {folderTotals.Bytes} bytes) differ from the records ({fileRecords} files, {bytes} bytes)");
            }
            var recomputed = folderTotals with
            {
                Files = fileRecords, Bytes = bytes, ScanErrors = scanErrors, ReparsePointsSkipped = reparseSkipped, FileReparsePoints = fileReparse,
            };
            if (recomputed != trailer.Totals) throw Bad(SpoolDefect.TotalsMismatch, $"recomputed {recomputed}, trailer {trailer.Totals}");
            if (expectedTotals is not null && expectedTotals != trailer.Totals)
            {
                throw Bad(SpoolDefect.ResultTotalsMismatch, $"trailer {trailer.Totals}, scan result {expectedTotals}");
            }
            Trailer = trailer;
        }

        /// <summary>Parses the folder section (its first tag is already consumed) and checks closure with the runs.
        /// Returns the number of folders.</summary>
        private long ParseFolders((int FolderIndex, int Run)[] runsByFolder, out ScanTotals folderTotals)
        {
            const byte Listed = 1, Complete = 2;   // per-folder flags
            byte[]? flags = null;
            long[]? childrenSeen = null;
            long[]? directSubfolders = null;
            long count = 0, locallyIncomplete = 0, affectedAncestors = 0;
            long rootFiles = 0, rootBytes = 0;
            var runCursor = 0;

            for (long k = 0; ; k++)
            {
                if (k > 0)
                {
                    if (k == count) break;
                    Need(1, SpoolDefect.FolderSectionInvalid, "a folder record");
                    var tag = pass.Take(1)[0];
                    if (tag != SpoolFormat.FolderTag) throw Bad(SpoolDefect.FolderSectionInvalid, $"tag 0x{tag:X2} where folder {k} is due");
                }
                var recordOffset = pass.Position - 1;
                Need(SpoolFormat.FolderRecordFixedBytes - 1, SpoolDefect.RecordOutOfBounds, "a folder record");
                var head = pass.Take(4 + 4 + 2);
                var index = BinaryPrimitives.ReadInt32LittleEndian(head);
                var parent = BinaryPrimitives.ReadInt32LittleEndian(head[4..]);
                var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(head[8..]);
                Need(2L * nameLength + SpoolFormat.FolderRecordFixedBytes - 11, SpoolDefect.RecordOutOfBounds, "a folder name");
                pass.Skip(2L * nameLength);
                var v = pass.Take(SpoolFormat.FolderRecordFixedBytes - 11);
                if (!SpoolFormat.TryStatus(v[0], out var status)) throw Bad(SpoolDefect.RecordOutOfBounds, $"folder record at {recordOffset}: status code {v[0]}");
                if (v[1] != SpoolFormat.NoStatusReason && !SpoolFormat.TryErrorType(v[1], out _)) throw Bad(SpoolDefect.RecordOutOfBounds, $"folder record at {recordOffset}: reason code {v[1]}");
                if (v[2] > 1) throw Bad(SpoolDefect.RecordOutOfBounds, $"folder record at {recordOffset}: subtree flag {v[2]}");
                var subtreeComplete = v[2] == 1;
                for (var i = 0; i < 2; i++)
                {
                    var ticks = BinaryPrimitives.ReadInt64LittleEndian(v[(7 + 8 * i)..]);
                    if (!SpoolFormat.ValidTicks(ticks)) throw Bad(SpoolDefect.RecordOutOfBounds, $"folder record at {recordOffset}: ticks {ticks}");
                }
                long directBytes = SpoolFormat.FolderValue(v, 0), totalBytes = SpoolFormat.FolderValue(v, 1), directFiles = SpoolFormat.FolderValue(v, 2), totalFiles = SpoolFormat.FolderValue(v, 3),
                    directSub = SpoolFormat.FolderValue(v, 4), totalSub = SpoolFormat.FolderValue(v, 5), largest = SpoolFormat.FolderValue(v, 6);
                if (directBytes < 0 || directFiles < 0 || directSub < 0 || totalBytes < directBytes || totalFiles < directFiles || totalSub < directSub
                    || (largest != SpoolFormat.NullSize && largest < 0))
                {
                    throw Bad(SpoolDefect.RecordOutOfBounds, $"folder record at {recordOffset}: inconsistent sizes or counts");
                }

                if (k == 0)
                {
                    if (index != 0 || parent != -1) throw Bad(SpoolDefect.FolderSectionInvalid, $"the folder section starts with folder {index} (parent {parent}), not the root");
                    // n = the root's total subfolders + 1, bounded by what the remaining bytes can hold.
                    var maxFolders = 1 + (_trailerStart - pass.Position) / SpoolFormat.FolderRecordFixedBytes;
                    if (totalSub < 0 || totalSub + 1 > maxFolders || totalSub + 1 > int.MaxValue)
                    {
                        throw Bad(SpoolDefect.FolderSectionInvalid, $"the root accounts for {totalSub} subfolders; the spool can hold at most {maxFolders - 1}");
                    }
                    count = totalSub + 1;
                    flags = new byte[count];
                    childrenSeen = new long[count];
                    directSubfolders = new long[count];
                    rootFiles = totalFiles;
                    rootBytes = totalBytes;
                }
                else
                {
                    if (index != k) throw Bad(SpoolDefect.FolderSectionInvalid, $"folder {index} where folder {k} is due (ascending, each once)");
                    if (parent < 0 || parent >= index) throw Bad(SpoolDefect.FolderSectionInvalid, $"folder {index} has parent {parent}, which is not below it");
                    // A child is observed only inside a folder that was listed (Ok or Partial).
                    if ((flags![parent] & Listed) == 0) throw Bad(SpoolDefect.FolderClosureViolation, $"folder {index} lies inside folder {parent}, which was not listed");
                    childrenSeen![parent]++;
                    // Incompleteness propagates to every ancestor.
                    if (!subtreeComplete && (flags[parent] & Complete) != 0) throw Bad(SpoolDefect.FolderClosureViolation, $"folder {index} is incomplete but its parent {parent} is marked complete");
                }

                var listed = status is FolderScanStatus.Ok or FolderScanStatus.Partial;
                if (status is FolderScanStatus.Unreadable or FolderScanStatus.Partial && subtreeComplete)
                {
                    throw Bad(SpoolDefect.FolderClosureViolation, $"folder {index} could not be fully read but is marked complete");
                }
                flags![index] = (byte)((listed ? Listed : 0) | (subtreeComplete ? Complete : 0));
                directSubfolders![index] = directSub;
                if (status is FolderScanStatus.Unreadable or FolderScanStatus.Partial) locallyIncomplete++;
                else if (!subtreeComplete) affectedAncestors++;

                // Direct file counts and bytes equal the folder's run (or zero when it has none).
                long runFiles = 0, runBytes = 0;
                if (runCursor < runsByFolder.Length && runsByFolder[runCursor].FolderIndex == index)
                {
                    var run = runsByFolder[runCursor++].Run;
                    runFiles = _runs[run].Count;
                    runBytes = _runBytes[run];
                    if (!listed) throw Bad(SpoolDefect.FolderClosureViolation, $"folder {index} has files but was not listed");
                }
                if (runFiles != directFiles || runBytes != directBytes)
                {
                    throw Bad(SpoolDefect.FolderClosureViolation, $"folder {index} declares {directFiles} files of {directBytes} bytes; its records hold {runFiles} of {runBytes}");
                }
            }

            if (runCursor < runsByFolder.Length)
            {
                throw Bad(SpoolDefect.FolderClosureViolation, $"a file run names folder {runsByFolder[runCursor].FolderIndex}, which does not exist");
            }
            for (var i = 0; i < count; i++)
            {
                if (childrenSeen![i] != directSubfolders![i])
                {
                    throw Bad(SpoolDefect.FolderClosureViolation, $"folder {i} declares {directSubfolders[i]} subfolders; {childrenSeen[i]} follow it");
                }
            }

            folderTotals = new ScanTotals
            {
                Files = rootFiles, Folders = count, Bytes = rootBytes,
                LocallyIncompleteFolders = locallyIncomplete, AffectedAncestorFolders = affectedAncestors,
            };
            return count;
        }
    }
}

using System.Buffers.Binary;

namespace StorageInventory.Core.Spool;

/// <summary>
/// A spool that passed the verification pass V. Only <see cref="SpoolReader.Verify"/> creates one, so every record a
/// caller can read comes from bytes V has checked. It reads through the same stream V read, by seeking: by run (the
/// key-ordered reads of IMP-03), by section, or in physical order. It never writes.
/// </summary>
/// <remarks>The stream must not change after V; from C5 the exclusive handle guarantees it. As a cheap guard, every read
/// checks that the length is still the verified one, and a record that does not parse as V found it throws
/// <see cref="SpoolFormatException"/>.</remarks>
internal sealed class VerifiedSpool
{
    private const int SectionBufferBytes = 1 << 16;
    private const int RunBufferBytes = 1 << 12;

    private readonly Stream _stream;
    private readonly long _length;
    private readonly long _recordRegionOffset;

    internal VerifiedSpool(Stream stream, long length, SpoolHeader header, long recordRegionOffset, SpoolTrailer trailer, IReadOnlyList<SpoolRun> runs)
    {
        _stream = stream;
        _length = length;
        _recordRegionOffset = recordRegionOffset;
        Header = header;
        Trailer = trailer;
        Runs = runs;
    }

    public SpoolHeader Header { get; }
    public SpoolTrailer Trailer { get; }

    /// <summary>Every file run, in emission order, with each run's first sequence number.</summary>
    public IReadOnlyList<SpoolRun> Runs { get; }

    /// <summary>The files of one run, in spool order, skipping the error records between them.</summary>
    public IEnumerable<SpoolFileRecord> ReadRun(SpoolRun run)
    {
        var cursor = new Cursor(this, run.Offset, RunBufferBytes);
        var sequence = run.FirstSequence;
        for (var read = 0; read < run.Count;)
        {
            var tag = cursor.Byte();
            if (tag == SpoolFormat.ErrorTag)
            {
                cursor.SkipErrorBody();
                continue;
            }
            if (tag != SpoolFormat.FileTag) throw Changed($"tag 0x{tag:X2} inside a run");
            var file = cursor.FileBody(sequence++);
            if (file.FolderIndex != run.FolderIndex) throw Changed("a run's record names another folder");
            read++;
            yield return file;
        }
    }

    /// <summary>The finalised folders, ascending index.</summary>
    public IEnumerable<SpoolFolderRecord> ReadFolders()
    {
        var cursor = new Cursor(this, Trailer.FolderSectionOffset, SectionBufferBytes);
        for (long k = 0; k < Trailer.FolderRecords; k++)
        {
            if (cursor.Byte() != SpoolFormat.FolderTag) throw Changed("a folder record is missing");
            yield return cursor.FolderBody();
        }
    }

    /// <summary>File and error records in physical (emission) order.</summary>
    public IEnumerable<SpoolRecord> ReadRecordRegion()
    {
        var cursor = new Cursor(this, _recordRegionOffset, SectionBufferBytes);
        long fileSequence = 0, errorSequence = 0;
        while (cursor.Position < Trailer.FolderSectionOffset)
        {
            var tag = cursor.Byte();
            if (tag == SpoolFormat.FileTag) yield return cursor.FileBody(fileSequence++);
            else if (tag == SpoolFormat.ErrorTag) yield return cursor.ErrorBody(errorSequence++);
            else throw Changed($"tag 0x{tag:X2} in the record region");
        }
    }

    /// <summary>The error records in emission order (one more pass over the record region, skipping file records).</summary>
    public IEnumerable<SpoolErrorRecord> ReadErrors() => ReadRecordRegion().OfType<SpoolErrorRecord>();

    private static SpoolFormatException Changed(string detail) => new("The verified spool no longer reads as it was verified: " + detail + ".");

    /// <summary>Buffered sequential reading from an offset of the verified stream.</summary>
    private sealed class Cursor
    {
        private readonly VerifiedSpool _spool;
        private readonly byte[] _buffer;
        private int _start, _end;
        private long _bufferOffset;

        /// <param name="bufferBytes">At least the largest fixed part of a record (folder records: 90 bytes).</param>
        public Cursor(VerifiedSpool spool, long offset, int bufferBytes)
        {
            _spool = spool;
            _bufferOffset = offset;
            _buffer = new byte[bufferBytes];
        }

        public long Position => _bufferOffset + _start;

        public byte Byte() => Take(1)[0];

        public SpoolFileRecord FileBody(long sequence)
        {
            var head = Take(4 + 2);
            var folder = BinaryPrimitives.ReadInt32LittleEndian(head);
            var name = Utf16(BinaryPrimitives.ReadUInt16LittleEndian(head[4..]));
            var v = Take(8 * 4 + 4);
            return new SpoolFileRecord(sequence, folder, name, BinaryPrimitives.ReadInt64LittleEndian(v),
                SpoolFormat.FromTicks(BinaryPrimitives.ReadInt64LittleEndian(v[8..])),
                SpoolFormat.FromTicks(BinaryPrimitives.ReadInt64LittleEndian(v[16..])),
                SpoolFormat.FromTicks(BinaryPrimitives.ReadInt64LittleEndian(v[24..])),
                (FileAttributes)BinaryPrimitives.ReadInt32LittleEndian(v[32..]));
        }

        public SpoolErrorRecord ErrorBody(long sequence)
        {
            var code = Byte();
            if (!SpoolFormat.TryErrorType(code, out var type)) throw Changed($"error code {code}");
            var path = Utf16(BinaryPrimitives.ReadUInt32LittleEndian(Take(4)));
            var message = Utf16(BinaryPrimitives.ReadUInt32LittleEndian(Take(4)));
            return new SpoolErrorRecord(sequence, type, path, message);
        }

        public void SkipErrorBody()
        {
            Byte();
            Skip(2L * BinaryPrimitives.ReadUInt32LittleEndian(Take(4)));
            Skip(2L * BinaryPrimitives.ReadUInt32LittleEndian(Take(4)));
        }

        public SpoolFolderRecord FolderBody()
        {
            var head = Take(4 + 4 + 2);
            var index = BinaryPrimitives.ReadInt32LittleEndian(head);
            var parent = BinaryPrimitives.ReadInt32LittleEndian(head[4..]);
            var name = Utf16(BinaryPrimitives.ReadUInt16LittleEndian(head[8..]));
            var v = Take(SpoolFormat.FolderRecordFixedBytes - 11);
            if (!SpoolFormat.TryStatus(v[0], out var status)) throw Changed($"status code {v[0]}");
            ScanErrorType? reason = null;
            if (v[1] != SpoolFormat.NoStatusReason)
            {
                if (!SpoolFormat.TryErrorType(v[1], out var r)) throw Changed($"reason code {v[1]}");
                reason = r;
            }
            var largest = SpoolFormat.FolderValue(v, 6);
            return new SpoolFolderRecord(index, parent, name, status, reason, v[2] == 1,
                (FileAttributes)BinaryPrimitives.ReadInt32LittleEndian(v[3..]),
                SpoolFormat.FromTicks(BinaryPrimitives.ReadInt64LittleEndian(v[7..])),
                SpoolFormat.FromTicks(BinaryPrimitives.ReadInt64LittleEndian(v[15..])),
                SpoolFormat.FolderValue(v, 0), SpoolFormat.FolderValue(v, 1), SpoolFormat.FolderValue(v, 2), SpoolFormat.FolderValue(v, 3),
                SpoolFormat.FolderValue(v, 4), SpoolFormat.FolderValue(v, 5), largest == SpoolFormat.NullSize ? null : largest);
        }

        private string Utf16(long codeUnits)
        {
            var bytes = 2 * codeUnits;
            if (bytes <= _buffer.Length) return SpoolFormat.ReadUtf16(Take((int)bytes));
            var large = new byte[bytes];   // a long error message or path; bounded by the verified file
            for (var done = 0; done < bytes;)
            {
                var n = (int)Math.Min(_buffer.Length, bytes - done);
                Take(n).CopyTo(large.AsSpan(done));
                done += n;
            }
            return SpoolFormat.ReadUtf16(large);
        }

        private void Skip(long bytes)
        {
            while (bytes > 0)
            {
                var n = (int)Math.Min(_buffer.Length, bytes);
                Take(n);
                bytes -= n;
            }
        }

        private ReadOnlySpan<byte> Take(int count)
        {
            if (_end - _start < count) Refill(count);
            var span = _buffer.AsSpan(_start, count);
            _start += count;
            return span;
        }

        private void Refill(int count)
        {
            if (_spool._stream.Length != _spool._length) throw Changed("its length changed");
            _bufferOffset += _start;
            var kept = _end - _start;
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, kept);
            _start = 0;
            _end = kept;
            _spool._stream.Seek(_bufferOffset + _end, SeekOrigin.Begin);
            while (_end < count)
            {
                var want = (int)Math.Min(_buffer.Length - _end, _spool._length - (_bufferOffset + _end));
                if (want <= 0) throw Changed("a record runs past its end");
                var got = _spool._stream.Read(_buffer, _end, want);
                if (got <= 0) throw Changed("it ended early");
                _end += got;
            }
        }
    }
}

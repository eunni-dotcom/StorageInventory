using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace StorageInventory.Core.Spool;

/// <summary>
/// The observation spool, format 1 (SPOOL-20, D-50): a private, versioned, append-only binary record of one scan's
/// observations. It is not a snapshot, not an export and not an interchange format (INV-11's analogue). C1 provides the
/// codec over a <see cref="Stream"/> only; no spool file exists until C5.
/// </summary>
/// <remarks>
/// <para><b>Layout</b> (little-endian throughout; every string is a length in UTF-16 code units followed by the exact
/// code units, never normalised, never case-folded, unpaired surrogates included):</para>
/// <code>
/// Header      magic "SISPOOL1" (8 ASCII bytes) | format u16 = 1 | flags u16 = 0 | capture token (16 bytes)
///             | run ID (u16 length, 1..256) | scanner_contract u16 = 1 | created UTC ticks i64
/// Records     in emission order, file and error records interleaved exactly as the scan emitted them:
///   0x01 file   folder index i32 | name (u16 length) | size i64 | created, modified, accessed ticks i64 | attributes i32
///   0x02 error  type u8 | path (u32 length) | message (u32 length)
/// Folders     every finalised folder, ascending index 0..n-1 (the folder section):
///   0x03 folder index i32 | parent index i32 | name (u16 length) | status u8 | status reason u8 | subtree complete u8
///               | attributes i32 | created, modified ticks i64 | direct bytes, total bytes, direct files, total files,
///               direct subfolders, total subfolders i64 | largest file bytes i64
/// Run index   one 16-byte entry per file run, in emission order: folder index i32 | offset i64 | count i32
/// Trailer     0x7F | file records, error records, folder records, runs i64 | totals: files, folders, bytes,
///             scan errors, reparse points skipped, file reparse points, locally incomplete folders, affected
///             ancestor folders i64 | folder-section offset i64 | run-index offset i64           (113 bytes)
/// Digest      SHA-256 of every byte before it: bytes [0, L - 32) of a file of length L          (32 bytes)
/// </code>
/// <para><b>Frozen details</b> (G0F-O03): nulls are <see cref="long.MinValue"/> for ticks and the largest-file size,
/// and <see cref="NoStatusReason"/> for the status reason; format 1 defines no flags, so any non-zero flags value is
/// rejected; codes are the explicit stable codes of the specification's §9.4, never enum ordinals; a file record's
/// sequence is its position among file records, an error record's its position among error records; a run's offset
/// is the absolute offset of its first file record's tag. A file run is a maximal sequence of file records with one
/// folder index; error records may lie between its records. <b>Record counts map to totals</b> as: file records =
/// <see cref="ScanTotals.Files"/>; error records = <see cref="ScanTotals.ScanErrors"/> +
/// <see cref="ScanTotals.ReparsePointsSkipped"/> + <see cref="ScanTotals.FileReparsePoints"/> (informational reparse
/// rows are error records too); folder records = <see cref="ScanTotals.Folders"/>.</para>
/// </remarks>
internal static class SpoolFormat
{
    public static ReadOnlySpan<byte> Magic => "SISPOOL1"u8;
    public const ushort FormatVersion = 1;
    public const ushort Flags = 0;
    public const ushort ScannerContract = 1;   // v1.0 traversal and metadata semantics (spec §9.4, scanner_contract)
    public const int CaptureTokenLength = 16;
    public const int MaxRunIdLength = 256;

    public const byte FileTag = 0x01;
    public const byte ErrorTag = 0x02;
    public const byte FolderTag = 0x03;
    public const byte TrailerTag = 0x7F;

    public const long NullTicks = long.MinValue;
    public const long NullSize = long.MinValue;
    public const byte NoStatusReason = 0xFF;

    /// <summary>Magic, format, flags, capture token, run-ID length.</summary>
    public const int HeaderFixedBytes = 8 + 2 + 2 + CaptureTokenLength + 2;
    /// <summary>Scanner contract and created ticks, after the run ID.</summary>
    public const int HeaderTailBytes = 2 + 8;
    /// <summary>A file record without its name's code units.</summary>
    public const int FileRecordFixedBytes = 1 + 4 + 2 + 8 + 8 * 3 + 4;
    /// <summary>An error record without its strings' code units.</summary>
    public const int ErrorRecordFixedBytes = 1 + 1 + 4 + 4;
    /// <summary>A folder record without its name's code units.</summary>
    public const int FolderRecordFixedBytes = 1 + 4 + 4 + 2 + 3 + 4 + 8 * 2 + 8 * 6 + 8;
    public const int RunEntryBytes = 4 + 8 + 4;
    public const int TrailerBytes = 1 + 8 * 4 + 8 * 8 + 8 * 2;
    public const int DigestBytes = 32;

    /// <summary>The shortest byte sequence that can hold a header and a trailer.</summary>
    public const int MinimumLength = HeaderFixedBytes + HeaderTailBytes + TrailerBytes + DigestBytes;

    // Stable codes (spec §9.4, SCH-07): explicit integers, append-only, never enum ordinals.
    public static byte ErrorCode(ScanErrorType type) => type switch
    {
        ScanErrorType.AccessDenied => 1,
        ScanErrorType.NotFound => 2,
        ScanErrorType.PathTooLong => 3,
        ScanErrorType.IOError => 4,
        ScanErrorType.InvalidPath => 5,
        ScanErrorType.InvalidTimestamp => 6,
        ScanErrorType.UnexpectedError => 7,
        ScanErrorType.ReparsePointSkipped => 8,
        ScanErrorType.ReparsePointFile => 9,
        _ => throw new SpoolFormatException($"No spool code for error type {type}."),
    };

    public static bool TryErrorType(byte code, out ScanErrorType type)
    {
        type = code switch
        {
            1 => ScanErrorType.AccessDenied,
            2 => ScanErrorType.NotFound,
            3 => ScanErrorType.PathTooLong,
            4 => ScanErrorType.IOError,
            5 => ScanErrorType.InvalidPath,
            6 => ScanErrorType.InvalidTimestamp,
            7 => ScanErrorType.UnexpectedError,
            8 => ScanErrorType.ReparsePointSkipped,
            9 => ScanErrorType.ReparsePointFile,
            _ => (ScanErrorType)(-1),
        };
        return code is >= 1 and <= 9;
    }

    public static byte StatusCode(FolderScanStatus status) => status switch
    {
        FolderScanStatus.Ok => 0,
        FolderScanStatus.Unreadable => 1,
        FolderScanStatus.Partial => 2,
        FolderScanStatus.ReparsePointSkipped => 3,
        _ => throw new SpoolFormatException($"No spool code for folder status {status}."),
    };

    public static bool TryStatus(byte code, out FolderScanStatus status)
    {
        status = code switch
        {
            0 => FolderScanStatus.Ok,
            1 => FolderScanStatus.Unreadable,
            2 => FolderScanStatus.Partial,
            3 => FolderScanStatus.ReparsePointSkipped,
            _ => (FolderScanStatus)(-1),
        };
        return code <= 3;
    }

    /// <summary>The i-th of a folder record's seven 64-bit counts (direct bytes .. largest file), read from the record's
    /// fixed part after its name.</summary>
    public static long FolderValue(ReadOnlySpan<byte> fixedTail, int i) => BinaryPrimitives.ReadInt64LittleEndian(fixedTail[(23 + 8 * i)..]);

    public static long Ticks(DateTime? value) => value is { } v ? v.Ticks : NullTicks;

    public static bool ValidTicks(long ticks) => ticks == NullTicks || ticks is >= 0 and <= 3155378975999999999;   // DateTime.MaxValue.Ticks

    public static DateTime? FromTicks(long ticks) => ticks == NullTicks ? null : new DateTime(ticks, DateTimeKind.Utc);

    /// <summary>The exact UTF-16LE code units of a string (no encoder, so unpaired surrogates survive).</summary>
    public static void CopyUtf16(ReadOnlySpan<char> text, Span<byte> destination)
    {
        if (BitConverter.IsLittleEndian)
        {
            MemoryMarshal.AsBytes(text).CopyTo(destination);
            return;
        }
        for (var i = 0; i < text.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(destination[(2 * i)..], text[i]);
    }

    /// <summary>Rebuilds a string from exact UTF-16LE code units.</summary>
    public static string ReadUtf16(ReadOnlySpan<byte> source)
    {
        if (BitConverter.IsLittleEndian) return new string(MemoryMarshal.Cast<byte, char>(source));
        var chars = new char[source.Length / 2];
        for (var i = 0; i < chars.Length; i++) chars[i] = (char)BinaryPrimitives.ReadUInt16LittleEndian(source[(2 * i)..]);
        return new string(chars);
    }
}

/// <summary>A spool could not be written or read consistently. Class A (CAT-01): for the writer this faults the
/// isolated observer; it never reaches the scan's outcome.</summary>
internal sealed class SpoolFormatException(string message) : Exception(message);

/// <summary>One file run as stored in (and rebuilt from) a spool.</summary>
/// <param name="Offset">Absolute offset of the run's first file record.</param>
/// <param name="FirstSequence">The v1 sequence of the run's first file (its position among all file records).</param>
internal readonly record struct SpoolRun(int FolderIndex, long Offset, int Count, long FirstSequence);

/// <summary>A record of the spool's record region.</summary>
internal abstract record SpoolRecord;

/// <summary>A file observation read back from a verified spool.</summary>
/// <param name="Sequence">Position among the file records: v1's emission sequence.</param>
internal sealed record SpoolFileRecord(long Sequence, int FolderIndex, string Name, long SizeBytes, DateTime? CreatedUtc,
    DateTime? ModifiedUtc, DateTime? LastAccessUtc, FileAttributes Attributes) : SpoolRecord;

/// <summary>A ScanErrors row read back from a verified spool.</summary>
/// <param name="Sequence">Position among the error records.</param>
internal sealed record SpoolErrorRecord(long Sequence, ScanErrorType Type, string Path, string Message) : SpoolRecord;

/// <summary>A finalised folder read back from a verified spool.</summary>
internal sealed record SpoolFolderRecord(int Index, int ParentIndex, string Name, FolderScanStatus Status, ScanErrorType? StatusReason,
    bool SubtreeComplete, FileAttributes Attributes, DateTime? CreatedUtc, DateTime? ModifiedUtc,
    long DirectSizeBytes, long TotalSizeBytes, long DirectFileCount, long TotalFileCount, long DirectSubfolderCount,
    long TotalSubfolderCount, long? LargestFileBytes);

/// <summary>The header of a verified spool.</summary>
internal sealed record SpoolHeader(byte[] CaptureToken, string RunId, ushort ScannerContract, DateTime CreatedUtc);

/// <summary>The trailer of a verified spool.</summary>
internal sealed record SpoolTrailer(long FileRecords, long ErrorRecords, long FolderRecords, long Runs, ScanTotals Totals,
    long FolderSectionOffset, long RunIndexOffset);

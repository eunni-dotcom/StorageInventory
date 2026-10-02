using StorageInventory.Core;
using StorageInventory.Core.Identity;
using StorageInventory.History.Identity;
using StorageInventory.History.Library;

namespace StorageInventory.Library;

/// <summary>One snapshot row as the Library hands it to the rest of the product: every stored code decoded through its explicit
/// table and every count range-checked (SEC-15, SEC-16).</summary>
internal sealed record SnapshotSummary(long SnapshotId, long SourceId, SnapshotState State, ScanCompletionState Completion, long Files, long Folders, long Bytes,
    IdentityConfidence Confidence, IdentityBasis Basis, LibraryInsideSource LibraryInsideSource, long SchemaVersion);

/// <summary>
/// The read boundary of the Library (SEC-15): its contents are untrusted, so a value read from them is decoded and checked before
/// anything else sees it. A stable code that this version does not know, a negative count, or a value of the wrong type is a
/// <see cref="LibraryDataException"/>, a data error that the caller shows as such, never a crash and never a surprise. Nothing read
/// here is, or becomes, a path that the product opens, launches or writes (A-17, SEC-15): the catalogue deals in numbers and codes,
/// and names and paths are exact UTF-16 bytes that only the display layer decodes.
/// </summary>
internal static class LibraryCatalog
{
    /// <summary>The upper bound on rows one call returns, whatever the caller asks for (a hostile or huge Library never makes it page
    /// without bound).</summary>
    internal const int MaximumRows = 10_000;

    /// <summary>The snapshots, oldest first (capture order is the snapshot id, SCH-11), at most <paramref name="limit"/> of them.</summary>
    internal static IReadOnlyList<SnapshotSummary> ListSnapshots(ReaderConnection reader, int limit = MaximumRows)
    {
        var rows = reader.Query(ReadSql.ListSnapshotSummaries, ("$limit", Math.Clamp(limit, 1, MaximumRows)));
        var result = new List<SnapshotSummary>(rows.Count);
        foreach (var row in rows)
        {
            result.Add(new SnapshotSummary(
                SnapshotId: Integer(row[0], "snapshot_id", min: 1),
                SourceId: Integer(row[1], "source_id", min: 1),
                State: StableCodes.SnapshotStateFromCode(Integer(row[2], "state")),
                Completion: StableCodes.CompletionFromStored(Integer(row[3], "completeness")),
                Files: Integer(row[4], "files", min: 0),
                Folders: Integer(row[5], "folders", min: 1),
                Bytes: Integer(row[6], "bytes", min: 0),
                Confidence: StableCodes.IdentityConfidenceFromCode(Integer(row[7], "identity_confidence")),
                Basis: StableCodes.IdentityBasisFromCode(Integer(row[8], "identity_basis")),
                LibraryInsideSource: StableCodes.LibraryInsideSourceFromCode(Integer(row[9], "library_inside_source")),
                SchemaVersion: Integer(row[10], "schema_version", min: 1, max: LibraryNames.SchemaVersion)));
        }
        return result;
    }

    /// <summary>An integer column, checked to be an integer in range.</summary>
    internal static long Integer(object? value, string column, long min = long.MinValue, long max = long.MaxValue)
    {
        if (value is not long number) throw new LibraryDataException($"The Library holds a {value?.GetType().Name ?? "NULL"} in the integer column {column}.");
        if (number < min || number > max) throw new LibraryDataException($"The Library holds {number} in {column}, outside {min}..{max}.");
        return number;
    }

    /// <summary>A name or path column: exact UTF-16LE bytes, never longer than a path can be (32,767 code units) and never an odd
    /// number of bytes. Returned as bytes: decoding for display is the display layer's job and the result is never used as a path.</summary>
    internal static byte[] Utf16Bytes(object? value, string column)
    {
        if (value is not byte[] bytes) throw new LibraryDataException($"The Library holds a {value?.GetType().Name ?? "NULL"} in the BLOB column {column}.");
        if (bytes.Length % 2 != 0 || bytes.Length > 2 * 32_767) throw new LibraryDataException($"The Library holds a malformed name or path in {column} ({bytes.Length} bytes).");
        return bytes;
    }
}

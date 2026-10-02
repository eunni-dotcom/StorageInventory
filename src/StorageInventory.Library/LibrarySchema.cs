using System.Security.Cryptography;
using System.Text;

namespace StorageInventory.Library;

/// <summary>One row of <c>sqlite_schema</c>.</summary>
internal readonly record struct SchemaRow(string Type, string Name, string TableName, string? Sql);

/// <summary>
/// The frozen schema-1 fingerprint (LIB-08 step 6, SEC-17, SCH-09 rule 7). At every open the rows of <c>sqlite_schema</c> must
/// exactly match the expected set for the version: no extra table, view, trigger or index, and no altered definition (the stored
/// <c>CREATE</c> text carries every column). A mismatch is "Not a Library (unexpected schema)" and the schema is never repaired.
/// TEST-S1 pins <see cref="Schema1Fingerprint"/> and the object list; changing either is a schema change that needs review.
/// </summary>
internal static class LibrarySchema
{
    /// <summary>SHA-256 (lower-case hex) of <see cref="Canonical"/> over the rows of a freshly created schema-1 Library.</summary>
    internal const string Schema1Fingerprint = "8f96f561b6f811559bf56fe26322ae43e6835ea4f61c1c15df5200078b773457";

    /// <summary>The objects schema 1 contains, as "type name": 10 tables-with-auto-indexes and the explicit indexes. SQLite also
    /// lists the automatic indexes it creates for PRIMARY KEY and UNIQUE constraints; they have no SQL text.</summary>
    internal static readonly string[] Schema1Objects =
    [
        "index folder_path_child", "index folder_path_root", "index scan_attempt_by_source", "index snapshot_by_source",
        "index source_local_key", "index source_network_key", "index sqlite_autoindex_name_1", "index sqlite_autoindex_snapshot_1",
        "index volume_by_serial",
        "table file_obs", "table folder_obs", "table folder_path", "table library_info", "table name", "table scan_attempt",
        "table scan_error", "table snapshot", "table snapshot_extension_total", "table source", "table volume",
    ];

    /// <summary>The canonical text the fingerprint is computed over: one line per row, ordered by type then name.</summary>
    internal static string Canonical(IEnumerable<SchemaRow> rows)
    {
        var builder = new StringBuilder();
        foreach (var row in rows.OrderBy(r => r.Type, StringComparer.Ordinal).ThenBy(r => r.Name, StringComparer.Ordinal))
        {
            builder.Append(row.Type).Append('|').Append(row.Name).Append('|').Append(row.TableName).Append('|').Append(row.Sql is null ? "<null>" : row.Sql.Replace("\r\n", "\n", StringComparison.Ordinal)).Append('\n');
        }
        return builder.ToString();
    }

    internal static string Compute(IEnumerable<SchemaRow> rows) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(rows)))).ToLowerInvariant();

    /// <summary>What differs from schema 1: objects that are not expected, expected ones that are missing, and (when the object set
    /// matches) whether a definition changed. Empty when the fingerprint matches.</summary>
    internal static IReadOnlyList<string> Differences(IReadOnlyCollection<SchemaRow> rows)
    {
        var found = rows.Select(r => $"{r.Type} {r.Name}").ToHashSet(StringComparer.Ordinal);
        var differences = new List<string>();
        differences.AddRange(found.Except(Schema1Objects).Order(StringComparer.Ordinal).Select(o => "unexpected " + o));
        differences.AddRange(Schema1Objects.Except(found).Select(o => "missing " + o));
        if (differences.Count == 0 && Compute(rows) != Schema1Fingerprint) differences.Add("a definition differs from schema 1");
        return differences;
    }

    internal static bool Matches(IReadOnlyCollection<SchemaRow> rows) => Differences(rows).Count == 0;
}

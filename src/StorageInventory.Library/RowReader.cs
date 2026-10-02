using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace StorageInventory.Library;

/// <summary>One result row, read by position: just what the Library's queries need, so that the verification of a snapshot can stream
/// millions of rows through the engine's own API on the writer (no ADO.NET reader object per query, no per-row layering) and a
/// read-only connection can serve the same queries through ADO.NET.</summary>
internal interface IRowReader
{
    int FieldCount { get; }

    bool IsNull(int index);

    long GetInt64(int index);

    string GetString(int index);

    /// <summary>The value as <c>long</c>, <c>double</c>, <c>string</c>, <c>byte[]</c> or null.</summary>
    object? GetValue(int index);
}

/// <summary>A row of a <see cref="SqliteDataReader"/>.</summary>
internal sealed class AdoRowReader(SqliteDataReader reader) : IRowReader
{
    public int FieldCount => reader.FieldCount;

    public bool IsNull(int index) => reader.IsDBNull(index);

    public long GetInt64(int index) => reader.GetInt64(index);

    public string GetString(int index) => reader.GetString(index);

    public object? GetValue(int index) => reader.IsDBNull(index) ? null : reader.GetValue(index);
}

/// <summary>A row of a statement stepped through SQLitePCLRaw (the writer's own handle).</summary>
internal sealed class RawRowReader(sqlite3_stmt statement) : IRowReader
{
    public int FieldCount => raw.sqlite3_column_count(statement);

    public bool IsNull(int index) => raw.sqlite3_column_type(statement, index) == raw.SQLITE_NULL;

    public long GetInt64(int index) => raw.sqlite3_column_int64(statement, index);

    public string GetString(int index) => raw.sqlite3_column_text(statement, index).utf8_to_string();

    public object? GetValue(int index) => raw.sqlite3_column_type(statement, index) switch
    {
        raw.SQLITE_INTEGER => raw.sqlite3_column_int64(statement, index),
        raw.SQLITE_FLOAT => raw.sqlite3_column_double(statement, index),
        raw.SQLITE_TEXT => raw.sqlite3_column_text(statement, index).utf8_to_string(),
        raw.SQLITE_BLOB => raw.sqlite3_column_blob(statement, index).ToArray(),
        _ => null,
    };
}

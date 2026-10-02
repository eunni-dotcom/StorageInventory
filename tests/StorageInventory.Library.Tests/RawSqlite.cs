using System.Data.Common;

namespace StorageInventory.Library.Tests;

/// <summary>
/// A test-only raw SQLite connection, reached by reflection. The test projects have no compile-time access to SQLite
/// (<c>PrivateAssets="compile"</c>, A-11, A-12), so a test that has to edit a Library behind the product's back (a trigger, a
/// committed state-1 snapshot, a changed schema) loads <c>Microsoft.Data.Sqlite</c> by name and drives it through the
/// <see cref="DbConnection"/> abstractions. This is test code only; nothing in <c>src/</c> does this (A-18).
/// </summary>
internal static class RawSqlite
{
    private static DbConnection Open(string path, string mode)
    {
        LibraryDatabase.EnsureProvider();   // the same provider the product sets, once
        var type = Type.GetType("Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite", throwOnError: true)!;
        var connection = (DbConnection)Activator.CreateInstance(type, $"Data Source={path};Mode={mode};Pooling=False")!;
        connection.Open();
        return connection;
    }

    /// <summary>Runs statements read-write on an existing database, outside the product (the file is never created).</summary>
    internal static void Execute(string path, params string[] statements)
    {
        using var connection = Open(path, "ReadWrite");
        foreach (var sql in statements)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }

    internal static object? Scalar(string path, string sql)
    {
        using var connection = Open(path, "ReadOnly");
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return value is DBNull ? null : value;
    }

    /// <summary>Opens a read-write connection the caller keeps (to hold a write transaction open from a test).</summary>
    internal static DbConnection OpenReadWrite(string path) => Open(path, "ReadWrite");

    internal static DbConnection OpenReadOnly(string path) => Open(path, "ReadOnly");
}

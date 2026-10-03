using System.Diagnostics;

namespace StorageInventory.Library.Tests;

/// <summary>A diagnostic, not a test: what the shipped engine can do on this machine, statement by statement, through the same
/// <c>WriterStatement</c> the importer uses. It explains the import's profile (TEST-P1) and is run with <c>--benchmark micro</c>.</summary>
internal static class EngineMicroBenchmark
{
    internal static int Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "SI-Library-Micro", Guid.NewGuid().ToString("N")[..8]);
        var appData = Path.Combine(root, "AppData");
        Directory.CreateDirectory(appData);
        var session = new LibrarySession(Path.Combine(appData, "Library"), appData);
        session.RunStartupOpen();
        using (var create = Lease(session, MutationKind.Create)) session.CreateLibrary(create);
        var main = Path.Combine(appData, "Library", LibraryNames.MainFile);
        using var prepare = Lease(session, MutationKind.Prepare);
        using var writer = LibraryDatabase.OpenWriter(prepare, session.Interlock, main, "micro", [MutationKind.Prepare], true, null);
        writer.Begin(prepare);
        void Exec(string sql) { using var s = writer.Prepare(prepare, sql); s.ExecuteNonQuery(prepare); }
        var cacheKiB = Environment.GetEnvironmentVariable("SI_MICRO_CACHE_KIB");
        if (cacheKiB is not null) { Exec("PRAGMA cache_size = -" + cacheKiB); Console.WriteLine("cache_size set to " + cacheKiB + " KiB"); }
        Exec("CREATE TABLE t_plain (a INTEGER NOT NULL, b INTEGER NOT NULL, c INTEGER NOT NULL, d INTEGER, e INTEGER, f INTEGER, g INTEGER, h INTEGER, i INTEGER NOT NULL, PRIMARY KEY (a, b, c)) STRICT, WITHOUT ROWID");
        Exec("CREATE TABLE t_names (name_id INTEGER PRIMARY KEY, utf16 BLOB NOT NULL UNIQUE) STRICT");
        Exec("CREATE TABLE t_names_nounique (name_id INTEGER PRIMARY KEY, utf16 BLOB NOT NULL) STRICT");

        const int n = 500_000;
        foreach (var rows in new[] { 500_000, 1_000_000, 2_000_000 })
        {
            Exec("DELETE FROM t_plain");
            Time($"insert 9 integer columns, key order, WITHOUT ROWID, {rows:N0} rows in one transaction", rows, () =>
            {
                using var s = writer.Prepare(prepare, "INSERT INTO t_plain (a, b, c, d, e, f, g, h, i) VALUES ($a, $b, $c, $d, $e, $f, $g, $h, $i)", "$a", "$b", "$c", "$d", "$e", "$f", "$g", "$h", "$i");
                for (var k = 0; k < rows; k++)
                {
                    s.Set(0, 1L).Set(1, (long)(k / 4)).Set(2, (long)k).Set(3, 5L).Set(4, 6L).Set(5, 7L).Set(6, 8L).Set(7, 9L).Set(8, 10L).ExecuteNonQuery(prepare);
                }
            });
            Console.WriteLine($"  journal now {(File.Exists(Path.Combine(appData, "Library", LibraryNames.JournalFile)) ? new FileInfo(Path.Combine(appData, "Library", LibraryNames.JournalFile)).Length : 0) / 1048576.0:0.0} MB, database {new FileInfo(main).Length / 1048576.0:0} MB");
        }

        foreach (var perStatement in new[] { 1, 4, 16 })
        {
            Exec("DELETE FROM t_plain");
            var rows = 500_000;
            Time($"multi-row insert, {perStatement} rows per statement", rows, () =>
            {
                var values = string.Join(",", Enumerable.Range(0, perStatement).Select(r => $"(${"a"}{r}, ${"b"}{r}, ${"c"}{r}, ${"d"}{r}, ${"e"}{r}, ${"f"}{r}, ${"g"}{r}, ${"h"}{r}, ${"i"}{r})"));
                var names = Enumerable.Range(0, perStatement).SelectMany(r => new[] { "a", "b", "c", "d", "e", "f", "g", "h", "i" }.Select(c => "$" + c + r)).ToArray();
                using var st = writer.Prepare(prepare, "INSERT INTO t_plain (a, b, c, d, e, f, g, h, i) VALUES " + values, names);
                for (var k = 0; k < rows; k += perStatement)
                {
                    for (var r = 0; r < perStatement; r++)
                    {
                        var o = r * 9;
                        st.Set(o, 1L).Set(o + 1, (long)((k + r) / 4)).Set(o + 2, (long)(k + r)).Set(o + 3, 5L).Set(o + 4, 6L).Set(o + 5, 7L).Set(o + 6, 8L).Set(o + 7, 9L).Set(o + 8, 10L);
                    }
                    st.ExecuteNonQuery(prepare);
                }
            });
        }

        var random = new Random(7);
        var keys = Enumerable.Range(0, n).Select(i => System.Text.Encoding.Unicode.GetBytes($"file_x_{random.Next()}_{i}.ext")).ToArray();
        Time("insert blob into rowid table + UNIQUE index, random order, RETURNING", n, () =>
        {
            using var s = writer.Prepare(prepare, "INSERT INTO t_names (utf16) VALUES ($u) RETURNING name_id", "$u");
            foreach (var key in keys) s.Set(0, key).ExecuteScalar(prepare);
        });
        Time("insert blob into rowid table (no index), RETURNING", n, () =>
        {
            using var s = writer.Prepare(prepare, "INSERT INTO t_names_nounique (utf16) VALUES ($u) RETURNING name_id", "$u");
            foreach (var key in keys) s.Set(0, key).ExecuteScalar(prepare);
        });
        Time("insert blob into rowid table + UNIQUE index, no RETURNING (ExecuteNonQuery)", n, () =>
        {
            Exec("DELETE FROM t_names");
            using var s = writer.Prepare(prepare, "INSERT INTO t_names (utf16) VALUES ($u)", "$u");
            foreach (var key in keys) s.Set(0, key).ExecuteNonQuery(prepare);
        });
        Time("SELECT by blob key through the UNIQUE index (all present)", n, () =>
        {
            using var s = writer.Prepare(prepare, "SELECT name_id FROM t_names WHERE utf16 = $u", "$u");
            foreach (var key in keys) s.Set(0, key).ExecuteScalar(prepare);
        });
        Time("SELECT by blob key (all absent)", n, () =>
        {
            using var s = writer.Prepare(prepare, "SELECT name_id FROM t_names WHERE utf16 = $u", "$u");
            foreach (var key in keys) s.Set(0, key.Concat(new byte[] { 1, 0 }).ToArray()).ExecuteScalar(prepare);
        });
        Time("INSERT ... ON CONFLICT DO NOTHING RETURNING (new names)", n, () =>
        {
            Exec("DELETE FROM t_names");
            using var s = writer.Prepare(prepare, "INSERT INTO t_names (utf16) VALUES ($u) ON CONFLICT (utf16) DO NOTHING RETURNING name_id", "$u");
            foreach (var key in keys) s.Set(0, key).ExecuteScalar(prepare);
        });
        writer.Rollback(prepare);
        session.TestOnlyShutdown();
        return 0;
    }

    private static void Time(string what, int rows, Action action)
    {
        var watch = Stopwatch.StartNew();
        action();
        var seconds = watch.Elapsed.TotalSeconds;
        Console.WriteLine($"{what}: {rows / seconds:N0} statements/s ({seconds / rows * 1e6:0.0} us each)");
    }

    private static MutationLease Lease(LibrarySession session, MutationKind kind)
    {
        if (!session.Interlock.TryBeginMutation(kind, 0, out var lease, out var refusal)) throw new InvalidOperationException(refusal);
        return lease;
    }
}

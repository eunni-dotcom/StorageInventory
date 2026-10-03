using System.Reflection;
using System.Text;
using StorageInventory.Testing;

namespace StorageInventory.Library.Tests;

/// <summary>A-24 (bounded query plans) and the run-time half of A-22 (the pragmas read back as configured).</summary>
public static class QueryPlanTests
{
    /// <summary>Statements whose plan may use a temporary B-tree although they have no LIMIT: a reviewed exception list. Empty in C4.</summary>
    internal static readonly string[] ReviewedTempBTreeExceptions = [];

    private static readonly string[] ObservationTables = ["file_obs", "folder_obs", "scan_error", "snapshot_extension_total"];

    private static IEnumerable<(string Name, string Sql)> Statements() => typeof(OpenSql).Assembly.GetTypes()
        .Where(t => t.Name.EndsWith("Sql", StringComparison.Ordinal) && t.IsAbstract && t.IsSealed && t.Namespace == "StorageInventory.Library")
        .SelectMany(t => t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => ($"{t.Name}.{f.Name}", ((string)f.GetRawConstantValue()!).Trim())))
        .Where(x => x.Item2.StartsWith("SELECT", StringComparison.Ordinal) || x.Item2.StartsWith("INSERT", StringComparison.Ordinal)
                    || x.Item2.StartsWith("UPDATE", StringComparison.Ordinal) || x.Item2.StartsWith("DELETE", StringComparison.Ordinal));

    [Test]
    public static void A_24_no_SQL_constant_plans_an_unbounded_temporary_B_tree_or_a_scan_of_an_observation_table()
    {
        var world = World.Create();
        var session = world.CreatedSession();
        LibraryStateTests.ImportOne(session, new SyntheticSnapshot(1500, seed: 1));
        LibraryStateTests.ImportOne(session, new SyntheticSnapshot(1500, seed: 2, label: "b"), "run-2", new ImportSourceSpec.Existing(1));

        var report = new StringBuilder();
        var problems = new List<string>();
        var statements = Statements().OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
        Assert.True(statements.Count >= 50, $"{statements.Count} DML statements found");
        foreach (var (name, sql) in statements)
        {
            var plan = session.Read(r => r.Query("EXPLAIN QUERY PLAN " + sql).Select(row => (string)row[3]!).ToList());
            report.AppendLine($"{name}: {string.Join(" | ", plan)}");
            var hasLimit = sql.Contains(" LIMIT ", StringComparison.Ordinal);
            if (plan.Any(p => p.Contains("USE TEMP B-TREE", StringComparison.Ordinal)) && !hasLimit && !ReviewedTempBTreeExceptions.Contains(name))
            {
                problems.Add($"{name} plans a temporary B-tree without a LIMIT: {string.Join(" | ", plan)}");
            }
            foreach (var table in ObservationTables)
            {
                if (plan.Any(p => p.StartsWith("SCAN " + table, StringComparison.Ordinal) && !p.Contains("USING", StringComparison.Ordinal)))
                {
                    problems.Add($"{name} scans the whole of {table}: {string.Join(" | ", plan)}");
                }
            }
        }
        Console.WriteLine("A-24 query plans (fixture Library, 2 snapshots of 1,500 folders):");
        Console.WriteLine(report.ToString());
        Assert.Equal(0, problems.Count, string.Join(Environment.NewLine, problems));
        session.TestOnlyShutdown();
    }

    [Test]
    public static void A_24_the_bounded_top_N_is_the_one_statement_that_sorts_and_it_has_a_LIMIT()
    {
        var world = World.Create();
        var session = world.CreatedSession();
        LibraryStateTests.ImportOne(session, new SyntheticSnapshot(800));
        var plan = session.Read(r => r.Query("EXPLAIN QUERY PLAN " + ReadSql.TopFilesBySize).Select(row => (string)row[3]!).ToList());
        Assert.True(plan.Any(p => p.Contains("USE TEMP B-TREE", StringComparison.Ordinal)), "the top-N does sort: " + string.Join(" | ", plan));
        Assert.Contains(" LIMIT ", ReadSql.TopFilesBySize);

        // negative self-test: an unbounded sort over the observation table is detected by the same inspection
        var unbounded = session.Read(r => r.Query("EXPLAIN QUERY PLAN SELECT size_bytes FROM file_obs ORDER BY size_bytes").Select(row => (string)row[3]!).ToList());
        Assert.True(unbounded.Any(p => p.Contains("USE TEMP B-TREE", StringComparison.Ordinal)) && unbounded.Any(p => p.StartsWith("SCAN file_obs", StringComparison.Ordinal)),
            "the inspection sees an unbounded sort and a full scan: " + string.Join(" | ", unbounded));
        session.TestOnlyShutdown();
    }

    [Test]
    public static void A_22_every_pragma_reads_back_as_configured_on_the_writer_and_the_reader()
    {
        var world = World.Create();
        var session = world.CreatedSession();
        using (var lease = World.Lease(session, MutationKind.Prepare, 1))
        {
            using var writer = LibraryDatabase.OpenWriter(lease, session.Interlock, world.Main, "pragmas", [MutationKind.Prepare], false, null);
            var q = WriterQueries.Of(writer, lease);
            Assert.Equal("truncate", Convert.ToString(q.Scalar(OpenSql.GetJournalMode)), "journal_mode");
            Assert.Equal(2L, Convert.ToInt64(q.Scalar(OpenSql.GetSynchronous)), "synchronous = FULL");
            Assert.Equal("normal", Convert.ToString(q.Scalar(OpenSql.GetLockingMode)), "locking_mode");
            Assert.Equal(5000L, Convert.ToInt64(q.Scalar(OpenSql.GetBusyTimeout)), "busy_timeout");
            Assert.Equal(2L, Convert.ToInt64(q.Scalar(OpenSql.GetTempStore)), "temp_store = MEMORY");
            Assert.Equal(1L, Convert.ToInt64(q.Scalar(OpenSql.GetForeignKeys)), "foreign_keys");
            Assert.Equal(0L, Convert.ToInt64(q.Scalar(OpenSql.GetTrustedSchema)), "trusted_schema");
            Assert.True(Convert.ToInt64(q.Scalar(OpenSql.GetCacheSize)) != -65536L, "the default cache is kept for a writer that is not the import");
        }
        var captureId = session.NewCaptureId();
        var prepare = World.Lease(session, MutationKind.Prepare, captureId);
        var save = prepare.HandOffToObservation().HandOffToSave(out _);
        using (var writer = LibraryDatabase.OpenWriter(save, session.Interlock, world.Main, "import pragmas", [MutationKind.Save], true, null))
        {
            var q = WriterQueries.Of(writer, save);
            Assert.Equal(-65536L, Convert.ToInt64(q.Scalar(OpenSql.GetCacheSize)), "the import writer's cache is 64 MiB");
            Assert.Equal("truncate", Convert.ToString(q.Scalar(OpenSql.GetJournalMode)));
        }
        save.Dispose();
        session.Read(r =>
        {
            Assert.Equal(2L, Convert.ToInt64(r.Scalar(OpenSql.GetTempStore)), "reader temp_store");
            Assert.Equal(0L, Convert.ToInt64(r.Scalar(OpenSql.GetTrustedSchema)), "reader trusted_schema");
            Assert.Equal(-2000L, Convert.ToInt64(r.Scalar(OpenSql.GetCacheSize)), "readers keep the build default (2 MiB)");
            return 0;
        });
        session.TestOnlyShutdown();
    }
}

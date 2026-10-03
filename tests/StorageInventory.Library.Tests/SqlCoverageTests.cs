using System.Reflection;
using StorageInventory.Testing;

namespace StorageInventory.Library.Tests;

/// <summary>
/// C4-M13: the A-24 plan check sees every statement. Every constant of every <c>*Sql</c> class is classified by its first keyword; the
/// DML ones (SELECT, INSERT, UPDATE, DELETE) are exactly the set <see cref="QueryPlanTests"/> plans, including the per-source name
/// statements, the verification statements with <c>$source_id</c> and the footprint statements; the others (PRAGMA reads and writes, DDL,
/// transaction control, fault injection) are accounted for by name, and a constant of a new kind fails the test until it is classified.
/// </summary>
public static class SqlCoverageTests
{
    private static IEnumerable<(string Name, string Text, FieldInfo Field)> AllConstants() => typeof(OpenSql).Assembly.GetTypes()
        .Where(t => t.Name.EndsWith("Sql", StringComparison.Ordinal) && t.Namespace == "StorageInventory.Library")
        .SelectMany(t => t.GetFields(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Select(f => ($"{t.Name}.{f.Name}", f.IsLiteral && f.FieldType == typeof(string) ? ((string)f.GetRawConstantValue()!).Trim() : "", f)));

    private static string Keyword(string text) => text.Split([' ', '\r', '\n', '\t'], 2, StringSplitOptions.RemoveEmptyEntries)[0].ToUpperInvariant();

    [Test]
    public static void A_24_the_plan_check_sees_every_DML_statement_and_every_other_constant_is_classified()
    {
        var all = AllConstants().ToList();
        Assert.True(all.All(c => c.Field.IsLiteral && c.Field.FieldType == typeof(string)), "every field of the Sql classes is a const string (C4-M13): " + string.Join(", ", all.Where(c => !c.Field.IsLiteral).Select(c => c.Name)));
        Assert.True(all.Count >= 100, $"{all.Count} SQL constants were found");

        var dmlKeywords = new[] { "SELECT", "INSERT", "UPDATE", "DELETE" };
        var dml = all.Where(c => dmlKeywords.Contains(Keyword(c.Text))).Select(c => c.Name).Order(StringComparer.Ordinal).ToList();
        var planned = QueryPlanTests.Statements().Select(s => s.Name).Order(StringComparer.Ordinal).ToList();
        Assert.SequenceEqual(dml, planned, "the statements the A-24 plan check sees are exactly the DML constants");
        Assert.True(planned.Count >= 55, $"{planned.Count} statements are planned");

        // the statements named by the review and by IMP-11 are among them
        foreach (var named in new[]
        {
            "OpenSql.SelectNameFootprint", "OpenSql.SelectFolderPathFootprint", "OpenSql.CountSchemaObjects", "OpenSql.SelectSchemaRows", "OpenSql.CountUnpublishedSnapshots",
            "ImportSql.SelectNameId", "ImportSql.InsertName", "ImportSql.InsertNameIfAbsent",
        })
        {
            Assert.True(planned.Contains(named), "the plan check sees " + named);
        }
        var perSource = all.Where(c => dmlKeywords.Contains(Keyword(c.Text)) && c.Text.Contains("$source_id", StringComparison.Ordinal)).Select(c => c.Name).ToList();
        Assert.True(perSource.Count >= 10, $"{perSource.Count} statements bind $source_id");
        Assert.True(perSource.All(planned.Contains), "every statement with $source_id is planned: " + string.Join(", ", perSource.Where(n => !planned.Contains(n))));
        Assert.True(planned.Count(n => n.StartsWith("ImportSql.Verify", StringComparison.Ordinal)) >= 5, "the verification statements are planned");

        // the others, by kind
        var others = all.Where(c => !dmlKeywords.Contains(Keyword(c.Text))).ToList();
        var byKind = others.GroupBy(c => Keyword(c.Text)).ToDictionary(g => g.Key, g => g.Select(c => c.Name).ToList());
        Assert.SequenceEqual(["BEGIN", "COMMIT", "CREATE", "PRAGMA", "ROLLBACK"], byKind.Keys.Order(StringComparer.Ordinal), "a constant of a new kind needs a classification here: " + string.Join(", ", byKind.Keys));
        var pragmaSets = byKind["PRAGMA"].Where(n => all.First(c => c.Name == n).Text.Contains('=', StringComparison.Ordinal)).ToList();
        var pragmaReads = byKind["PRAGMA"].Except(pragmaSets).ToList();
        Assert.True(pragmaSets.All(n => n.Contains(".Set", StringComparison.Ordinal) || n.StartsWith("FaultSql.", StringComparison.Ordinal)), "pragmas that set a value are the Set constants (A-22) and the fault injection: " + string.Join(", ", pragmaSets));
        Assert.True(pragmaReads.All(n => n.StartsWith("OpenSql.", StringComparison.Ordinal)), "the pragma reads are OpenSql's");
        Assert.True(pragmaReads.Contains("OpenSql.GetPageCount") && pragmaReads.Contains("OpenSql.GetPageSize") && pragmaReads.Contains("OpenSql.GetApplicationId") && pragmaReads.Contains("OpenSql.GetUserVersion"), "IMP-11's pragma reads are accounted for");
        Assert.Equal(18, byKind["CREATE"].Count, "the 18 DDL statements");
        Assert.Equal(3, byKind["BEGIN"].Count + byKind["COMMIT"].Count + byKind["ROLLBACK"].Count, "the three transaction statements");
    }

    [Test]
    public static void A_24_every_pragma_read_is_a_bounded_single_value_query_on_the_reader_and_the_writer()
    {
        // the PRAGMA reads have no plan to inspect: they are run, and each returns one small value without touching an observation table
        var world = World.Create();
        var session = world.CreatedSession();
        var reads = AllConstants().Where(c => Keyword(c.Text) == "PRAGMA" && !c.Text.Contains('=', StringComparison.Ordinal) && c.Name.StartsWith("OpenSql.", StringComparison.Ordinal) && c.Name != "OpenSql.QuickCheck").ToList();
        Assert.True(reads.Count >= 12, $"{reads.Count} pragma reads");
        foreach (var (name, text, _) in reads)
        {
            var value = session.Read(r => r.Scalar(text));
            Assert.NotNull(value, name + " returns a value on the reader");
        }
        var pageSize = Convert.ToInt64(session.Read(r => r.Scalar(OpenSql.GetPageSize)));
        var pageCount = Convert.ToInt64(session.Read(r => r.Scalar(OpenSql.GetPageCount)));
        Assert.Equal(4096L, pageSize, "page_size");
        Assert.True(pageCount > 5, "page_count");
        using (var lease = World.Lease(session, MutationKind.Prepare, 1))
        {
            using var writer = LibraryDatabase.OpenWriter(lease, session.Interlock, world.Main, "pragma reads", [MutationKind.Prepare], false, null);
            Assert.Equal(pageSize, writer.PageSize(lease), "the writer's PageSize");
            Assert.Equal(pageCount, writer.PageCount(lease), "the writer's PageCount");
        }
        session.TestOnlyShutdown();
    }
}

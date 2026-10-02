using StorageInventory.Testing;

namespace StorageInventory.Library.Tests;

public static class SmokeTests
{
    [Test]
    public static void A_session_creates_a_library_and_the_pinned_engine_is_loaded()
    {
        var world = World.Create();
        var session = world.OpenedSession();
        Assert.Equal(LibraryState.NotCreated, session.Status.State, "a fresh directory has no Library");
        Assert.False(Directory.Exists(world.LibraryDirectory), "an open creates no directory");

        using (var lease = World.Lease(session, MutationKind.Create))
        {
            var status = session.CreateLibrary(lease);
            Assert.Equal(LibraryState.Available, status.State, status.Message);
        }
        Console.WriteLine("members after creation: " + string.Join(", ", world.Names().Select(n => $"{n}={World.Length(world.Member(n))}")));
        var header = session.Store.ReadHeader();
        Assert.Equal(HeaderOutcome.Valid, header.Outcome);
        var version = session.Read(r => r.Scalar("SELECT sqlite_version()"));
        Assert.Equal("3.53.3", version?.ToString());
        var rows = session.Read(r =>
        {
            return r.Query(OpenSql.SelectSchemaRows).Select(row => new SchemaRow((string)row[0]!, (string)row[1]!, (string)row[2]!, (string?)row[3])).ToList();
        });
        Console.WriteLine("objects: " + string.Join("; ", rows.Select(r => r.Type + " " + r.Name)));
        Console.WriteLine("fingerprint: " + LibrarySchema.Compute(rows));
        session.TestOnlyShutdown();
    }
}

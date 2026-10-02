using StorageInventory.Library;

namespace StorageInventory.IntegrationTests.LeaseAuditFixtures;

// Fixtures for the negative self-tests of A-25 (A-21): methods that break the rule, and compliant ones that must be accepted. They are
// compiled into this test assembly, which the IL inspection reads (src is never touched), and are never called. Each is internal
// because it names the Library's internal lease type.

/// <summary>Compliant: takes a lease and passes it on.</summary>
internal static class Compliant
{
    internal static bool Create(LibraryStore store, MutationLease lease) => store.CreateEmptyDatabase(lease);
}

/// <summary>Compliant: the helper is private and called only by a method that takes a lease.</summary>
internal static class PrivateHelperFromLeasedMethod
{
    internal static void Public(LibraryStore store, MutationLease lease) => Helper(store, lease);

    private static void Helper(LibraryStore store, MutationLease lease) => store.EnsureLibraryFolder(lease);
}

/// <summary>Compliant: an instance method of a lease-bound type (its constructor took the lease).</summary>
internal sealed class LeaseBound
{
    private readonly MutationLease _lease;

    internal LeaseBound(MutationLease lease) => _lease = lease;

    internal bool Create(LibraryStore store) => store.CreateEmptyDatabase(_lease);
}

/// <summary>Compliant: obtains its own lease from the interlock.</summary>
internal static class ObtainsItsOwnLease
{
    internal static bool Run(LibrarySession session)
    {
        if (!session.Interlock.TryBeginMutation(MutationKind.Create, 0, out var lease, out _)) return false;
        using (lease) return session.Store.CreateEmptyDatabase(lease);
    }
}

/// <summary>Compliant: an async method that takes a lease.</summary>
internal static class AsyncWithALease
{
    internal static async Task Delete(LibrarySession session, MutationLease lease) => await session.DeleteSnapshotAsync(lease, 1);
}

// ---- violations ----

/// <summary>Violation: writes without a lease (a default value is not one).</summary>
internal static class WritesWithoutALease
{
    internal static bool Create(LibraryStore store) => store.CreateEmptyDatabase(default);
}

/// <summary>Violation: a private helper reachable from a public method that takes no lease.</summary>
internal static class PrivateHelperFromLeaselessPublic
{
    internal static void Public(LibraryStore store) => Helper(store);

    private static void Helper(LibraryStore store) => store.EnsureLibraryFolder(default);
}

/// <summary>Violation: creates a directory itself.</summary>
internal static class DirectFileSystemMutation
{
    internal static void Make(string path) => Directory.CreateDirectory(path);

    internal static void Rename(string from, string to) => File.Move(from, to);

    internal static Stream Open(string path) => new FileStream(path, FileMode.Open, FileAccess.Read);
}

/// <summary>Violation: an async method (its work is in a compiler-generated state machine) with no lease.</summary>
internal static class AsyncWithoutALease
{
    internal static async Task Delete(LibrarySession session) => await session.DeleteSnapshotAsync(default, 1);
}

/// <summary>Violation: the mutation hides in a lambda.</summary>
internal static class LambdaWithoutALease
{
    internal static Func<bool> Make(LibraryStore store) => () => store.CreateEmptyDatabase(default);
}

/// <summary>Violation: forges a lease (A-25 part c), both ways: a constructor call and a default value.</summary>
internal static class ForgesALease
{
    internal static MutationLease Forge() => new(null!, 1, MutationKind.Create);

    internal static MutationLease Zero() => default;

    internal static ObservationLease ForgeObservation() => new(null!, 1, 1);
}

/// <summary>A public surrogate for a lease type, so the "no public member takes or returns a lease" rule has something public to find
/// (the real lease types are internal, so a real public exposure cannot even be compiled).</summary>
public sealed class LeaseSurrogate;

/// <summary>Violation of the public-exposure rule, judged with <see cref="LeaseSurrogate"/> as the lease type.</summary>
public static class ExposesALease
{
    public static LeaseSurrogate Get() => new();

    public static void Take(LeaseSurrogate lease) => _ = lease;
}

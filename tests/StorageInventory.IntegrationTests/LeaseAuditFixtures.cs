using StorageInventory.Library;

namespace StorageInventory.IntegrationTests.LeaseAuditFixtures;

// Fixtures for the negative self-tests of A-25 (A-21): methods that break the rule, and compliant ones that must be accepted. They are
// compiled into this test assembly, which the IL inspection reads (src is never touched), and are never called. Each is internal
// because it names the Library's internal types. One class per case; LeaseAuditTests lists, for every violating method, the rule that
// must reject it and a word of the reason.

// ================================================================ compliant: must be accepted

/// <summary>A lease parameter, passed on.</summary>
internal static class CompliantLeaseParameter
{
    internal static bool Create(LibraryStore store, MutationLease lease) => store.CreateEmptyDatabase(lease);
}

/// <summary>A private helper called only by a method of its own type that takes a lease (one hop).</summary>
internal static class CompliantPrivateHelper
{
    internal static void Public(string path, MutationLease lease)
    {
        _ = lease;
        Make(path);
    }

    private static void Make(string path) => Directory.CreateDirectory(path);
}

/// <summary>A lambda that takes a lease of its own: it has authority for its own calls; the host that builds it reaches nothing.</summary>
internal static class CompliantLambdaWithItsOwnLease
{
    internal static Func<MutationLease, bool> Build(LibraryStore store) => lease => store.CreateEmptyDatabase(lease);

    internal static Func<MutationLease, Task<bool>> BuildAsync(LibraryStore store) => async lease =>
    {
        await Task.Yield();
        return store.CreateEmptyDatabase(lease);
    };
}

/// <summary>A lambda that captures a lease inside a host that takes the lease.</summary>
internal static class CompliantLambdaCapturingAHostLease
{
    internal static Func<bool> Build(LibraryStore store, MutationLease lease) => () => store.CreateEmptyDatabase(lease);
}

/// <summary>A local function with a lease parameter of its own: a unit of its own, accepted for its own calls.</summary>
internal static class CompliantLocalFunction
{
    internal static bool Run(LibraryStore store, MutationLease lease)
    {
        return Local(lease);

        bool Local(MutationLease own) => store.CreateEmptyDatabase(own);
    }
}

/// <summary>An async method that takes a lease.</summary>
internal static class CompliantAsync
{
    internal static async Task Delete(LibrarySession session, MutationLease lease) => await session.DeleteSnapshotAsync(lease, 1);
}

/// <summary>An interface method that takes the lease, implemented and called with it.</summary>
internal interface IFixtureLeasedOp
{
    void Run(string path, MutationLease lease);
}

internal sealed class CompliantLeasedImplementation : IFixtureLeasedOp
{
    public void Run(string path, MutationLease lease) => Directory.CreateDirectory(path);
}

internal static class CompliantLeasedDispatch
{
    internal static void Use(IFixtureLeasedOp op, MutationLease lease) => op.Run("x", lease);
}

/// <summary>Overloads: the lease-less overload of a leased operation is not itself a leased operation (a call to it is not a
/// mutation), so a harmless caller is not flagged for the lease-taking overload next to it.</summary>
internal static class OverloadedCallee
{
    internal static void Do(string path, MutationLease lease) => Directory.CreateDirectory(path);

    internal static int Do(string path) => path.Length;
}

internal static class CompliantHarmlessOverloadCaller
{
    internal static int Harmless(string path) => OverloadedCallee.Do(path);
}

/// <summary>A read-only operation needs no lease (A-25 d): the header read.</summary>
internal static class CompliantReadOnly
{
    internal static HeaderRead Header(LibraryStore store) => store.ReadHeader();
}

// ================================================================ violations: each must be rejected, for a named rule

/// <summary>Violation (A25a/not-private): a public wrapper without a lease.</summary>
public static class LeaselessPublicWrapper
{
    public static void Make(string path) => Directory.CreateDirectory(path);
}

/// <summary>Violation (A25a/not-private): an internal wrapper without a lease (a default value is not one).</summary>
internal static class LeaselessInternalWrapper
{
    internal static bool Create(LibraryStore store) => store.CreateEmptyDatabase(default);
}

/// <summary>Violation (A25a/caller-without-lease): a private helper reachable from a method that takes no lease.</summary>
internal static class PrivateHelperFromLeaselessMethod
{
    internal static void Public(string path) => Make(path);

    private static void Make(string path) => Directory.CreateDirectory(path);
}

/// <summary>Violation (A25a/caller-of-another-type): a private helper is called by a method that takes a lease, but of ANOTHER type (a
/// nested class calling its outer type's private helper): "called only from methods of its own type" is not met.</summary>
internal static class PrivateHelperFromANestedType
{
    private static void Make(string path) => Directory.CreateDirectory(path);

    internal static class Inner
    {
        internal static void Call(string path, MutationLease lease)
        {
            _ = lease;
            Make(path);
        }
    }
}

/// <summary>Violation (A25a/private-and-uncalled): a private helper that nobody calls has no authority.</summary>
internal static class PrivateHelperNobodyCalls
{
    private static void Orphan(string path) => Directory.CreateDirectory(path);
}

/// <summary>Violation (A25a/caller-without-lease): the helper is reached from a lease-taking method THROUGH another private helper
/// (two hops): authority is not transitive.</summary>
internal static class PrivateHelperTwoHops
{
    internal static void Top(string path, MutationLease lease)
    {
        _ = lease;
        Middle(path);
    }

    private static void Middle(string path) => Bottom(path);

    private static void Bottom(string path) => Directory.CreateDirectory(path);
}

/// <summary>An interface, an implementation behind it, and callers that never see the implementation.</summary>
internal interface IFixtureOp
{
    void Run(string path);
}

internal sealed class DispatchImplementation : IFixtureOp
{
    public void Run(string path) => Directory.CreateDirectory(path);
}

/// <summary>Violation (A25a/not-private): an explicit interface implementation is private by name but reachable through the interface.</summary>
internal sealed class DispatchExplicitImplementation : IFixtureOp
{
    void IFixtureOp.Run(string path) => Directory.CreateDirectory(path);
}

/// <summary>Violation (A25a/not-private, through dispatch): the caller sees only the interface; the implementation mutates.</summary>
internal static class DispatchThroughInterface
{
    internal static void Use(IFixtureOp op) => op.Run("x");
}

internal abstract class FixtureBase
{
    internal abstract void Do(string path);
}

internal sealed class FixtureDerived : FixtureBase
{
    internal override void Do(string path) => Directory.CreateDirectory(path);
}

/// <summary>Violation (through dispatch): a call, and a method-group conversion (<c>ldvirtftn</c>), through a base-class virtual.</summary>
internal static class DispatchThroughBaseClass
{
    internal static void Use(FixtureBase b) => b.Do("x");

    internal static Action<string> Delegate(FixtureBase b) => b.Do;
}

/// <summary>Violation: the same, through a generic helper constrained to the interface (<c>constrained. callvirt</c>).</summary>
internal static class DispatchThroughGenericHelper
{
    internal static void Run<T>(T op) where T : IFixtureOp => op.Run("x");
}

/// <summary>Violation: an implementation of the Library's own <c>IQueryRunner</c> that keeps a writer and a lease and runs a statement
/// on the writer with the retained lease, without any lease parameter (the shape the earlier audit exempted).</summary>
internal sealed class LeaseRetainingQueryRunner(WriterConnection writer, MutationLease lease) : IQueryRunner
{
    public object? Scalar(string sql, params (string Name, object? Value)[] parameters) => writer.Scalar(lease, sql, parameters);

    public void Rows(string sql, Action<IRowReader> each, params (string Name, object? Value)[] parameters) => writer.Rows(lease, sql, each, parameters);
}

/// <summary>Violation (through dispatch): a consumer of <c>IQueryRunner</c> reaches the retaining implementation.</summary>
internal static class UsesQueryRunner
{
    internal static object? Count(IQueryRunner runner) => runner.Scalar("x");
}

/// <summary>Violation (A25a/not-private): a lambda that captures a lease inside a host that takes none belongs to the host.</summary>
internal static class LambdaCapturingALeaseInALeaselessHost
{
    internal static Func<bool> Make(LibraryStore store)
    {
        MutationLease lease = default;
        return () => store.CreateEmptyDatabase(lease);
    }
}

/// <summary>Violation: the mutation hides in a lambda.</summary>
internal static class LambdaWithoutALease
{
    internal static Func<bool> Make(LibraryStore store) => () => store.CreateEmptyDatabase(default);
}

/// <summary>Violation: a method group of a leased operation made in a method that takes no lease (<c>ldftn</c> counts as a call).</summary>
internal static class MethodGroupOfALeasedOperation
{
    internal static Func<MutationLease, bool> Bind(LibraryStore store) => store.CreateEmptyDatabase;
}

/// <summary>Violation (R-19 equivalent): a lease kept in a field, used by an instance method without a lease parameter. The earlier
/// audit called this a "lease-bound type" and exempted it.</summary>
internal sealed class RetainedLease(LibraryStore store, MutationLease lease)
{
    internal bool Create() => store.CreateEmptyDatabase(lease);
}

/// <summary>Violation (R-20 equivalent): the same type runs a primitive (a directory creation, standing in for a command on the
/// writer, which a test assembly cannot name) with no lease parameter.</summary>
internal sealed class RetainedLeasePrimitive(MutationLease lease)
{
    private readonly MutationLease _lease = lease;

    internal long Id => _lease.Id;

    internal void Poke(string path) => Directory.CreateDirectory(path);
}

/// <summary>Violation (R-20 equivalent): a writer and its lease kept in fields; an instance method runs a statement on the writer.</summary>
internal sealed class RetainedWriter(WriterConnection writer, MutationLease lease)
{
    internal object? Peek(string sql) => writer.Scalar(lease, sql);
}

/// <summary>Violation: an object created under a lease and used after it: the lease-taking constructor is fine, the later method is not.</summary>
internal sealed class UsedAfterItsLease
{
    private readonly WriterConnection _writer;
    private readonly MutationLease _lease;

    internal UsedAfterItsLease(MutationLease lease, WriterConnection writer)
    {
        _lease = lease;
        _writer = writer;
    }

    internal void Late() => _writer.Commit(_lease);
}

/// <summary>Violation (the earlier "lease producer" exemption): a method that asks the interlock for a lease and uses it, without a
/// lease parameter of its own.</summary>
internal static class ProducerWithoutAParameter
{
    internal static bool Run(LibrarySession session)
    {
        if (!session.Interlock.TryBeginMutation(MutationKind.Create, 0, out var lease, out _)) return false;
        using (lease) return session.Store.CreateEmptyDatabase(lease);
    }
}

/// <summary>Violation: a writer handed in, a lease that is not.</summary>
internal static class WriterWithoutALease
{
    internal static void Poke(WriterConnection writer) => writer.Begin(default);
}

/// <summary>Violation: an overload hides nothing. The lease-taking overload is compliant; the lease-less one beside it is rejected.</summary>
internal static class OverloadHiding
{
    internal static void Make(string path, MutationLease lease)
    {
        _ = lease;
        Directory.CreateDirectory(path);
    }

    internal static void Make(string path) => Directory.CreateDirectory(path);
}

/// <summary>Violation: an async method (its work is in a compiler-generated state machine) with no lease.</summary>
internal static class AsyncWithoutALease
{
    internal static async Task Delete(LibrarySession session) => await session.DeleteSnapshotAsync(default, 1);
}

/// <summary>Violation: creates a directory, renames and opens a file itself.</summary>
internal static class DirectFileSystemMutation
{
    internal static void Make(string path) => Directory.CreateDirectory(path);

    internal static void Rename(string from, string to) => File.Move(from, to);

    internal static Stream Open(string path) => new FileStream(path, FileMode.Open, FileAccess.Read);

    internal static void Write(string path) => File.WriteAllText(path, "x");

    internal static void Info(string path) => new FileInfo(path).Delete();
}

// ================================================================ part (c)

/// <summary>Violation (A25c): forges a lease, both ways: a constructor call and a default value.</summary>
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

// ================================================================ C4-M17

/// <summary>Violation (C4-M17 file system): a file-system API outside LibraryStore.</summary>
internal static class FileSystemOutsideTheStore
{
    internal static bool Exists(string path) => File.Exists(path);

    internal static bool DirectoryExists(string path) => Directory.Exists(path);

    internal static long Length(string path) => new FileInfo(path).Length;

    internal static bool PathExists(string path) => Path.Exists(path);
}

/// <summary>Violation (C4-M17 lock first): inspects a member BEFORE the writer lock. The member calls sit lexically (and in IL) before
/// <c>AcquireWriterLock</c>.</summary>
internal static class InspectsBeforeTheLock
{
    internal static void Derive(LibraryStore store, MutationLease lease)
    {
        _ = store.ReadHeader();
        _ = store.InspectMembersUnderLock();
        _ = store.AcquireWriterLock(lease);
    }

    internal static void ReadHeaderFirst(LibraryStore store, MutationLease lease)
    {
        _ = store.ReadHeaderUnderLock();
        _ = store.AcquireWriterLock(lease);
    }

    internal static void CreateFirst(LibraryStore store, MutationLease lease)
    {
        _ = store.CreateEmptyDatabase(lease);
        _ = store.AcquireWriterLock(lease);
    }

    internal static async Task SetAside(LibraryStore store, MutationLease lease)
    {
        _ = store.QuarantineSet(lease);
        await Task.Yield();
        _ = store.AcquireWriterLock(lease);
    }

    internal static string Path(LibraryStore store, MutationLease lease)
    {
        var path = store.MainPathUnderLock();
        _ = store.AcquireWriterLock(lease);
        return path;
    }
}

/// <summary>Compliant: the lock first, then every member access.</summary>
internal static class InspectsAfterTheLock
{
    internal static void Derive(LibraryStore store, MutationLease lease)
    {
        _ = store.AcquireWriterLock(lease);
        _ = store.InspectMembersUnderLock();
        _ = store.ReadHeaderUnderLock();
        _ = store.ReadHeader();
        _ = store.MainPathUnderLock();
    }
}

/// <summary>Violation (C4-M17 lock first): a lock-requiring method (it inspects a member and never takes the lock) that is not private
/// and is not a declared entry; and a caller that reaches it before its own lock call.</summary>
internal static class InspectsWithoutTheLock
{
    internal static bool Exists(LibraryStore store) => store.InspectMembersUnderLock().Main.Exists;
}

internal static class CallsALockRequiringMethodBeforeTheLock
{
    private static bool Check(LibraryStore store) => store.InspectMembersUnderLock().Main.Exists;

    internal static void Derive(LibraryStore store, MutationLease lease)
    {
        _ = Check(store);
        _ = store.AcquireWriterLock(lease);
    }
}

// ================================================================ C4-M13

/// <summary>Violation (C4-M13): a statement that is a <c>static readonly</c> built string, not a constant.</summary>
internal static class FixtureBuiltSql
{
    internal static readonly string Table = "file_obs";
    internal static readonly string Select = "SELECT count(*) FROM " + Table;
}

/// <summary>Compliant: all constants.</summary>
internal static class FixtureConstantSql
{
    internal const string Select = "SELECT count(*) FROM file_obs";
}

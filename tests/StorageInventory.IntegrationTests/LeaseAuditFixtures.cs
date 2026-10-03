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

/// <summary>A lambda that captures a lease inside a host that takes the lease, and runs inside the host's call (it is built and
/// invoked there; it is never stored, returned or handed to code that keeps it).</summary>
internal static class CompliantLambdaCapturingAHostLease
{
    internal static bool Build(LibraryStore store, MutationLease lease)
    {
        Func<bool> create = () => store.CreateEmptyDatabase(lease);
        return create();
    }
}

/// <summary>The accepted shape of the Library's verification: the host that takes the lease builds a query runner out of delegates that
/// capture it and hands the runner to code that only queries through it (<c>SnapshotVerifier.Verify</c>), which neither stores nor returns
/// it. The runner is also used by the host itself.</summary>
internal static class CompliantRunnerForTheVerifier
{
    internal static string? Run(WriterConnection writer, MutationLease lease)
    {
        var runner = new DelegateQueryRunner(
            (sql, parameters) => writer.Scalar(lease, sql, parameters),
            (sql, each, parameters) => writer.Rows(lease, sql, each, parameters));
        _ = runner.Scalar(OpenSql.GetApplicationId);
        return SnapshotVerifier.Verify(runner, 1, 1, 0, 0, 0, 0, true);
    }
}

/// <summary>A carrier that keeps a delegate in one field and a name in another. Only the field the delegate was stored in carries it:
/// handing the carrier to code that reads the name is not an escape of the delegate (the analysis is field-sensitive).</summary>
internal sealed class FixtureHolder(Func<bool> action, string name)
{
    internal string Name => name;

    internal Func<bool> Action => action;

    internal bool Run() => action();
}

internal static class CompliantCarrierFieldSensitivity
{
    internal static string Run(LibraryStore store, MutationLease lease)
    {
        var holder = new FixtureHolder(() => store.CreateEmptyDatabase(lease), "n");
        _ = holder.Run();
        return Describe(holder);
    }

    private static string Describe(FixtureHolder holder) => holder.Name;
}

internal delegate bool FixtureApply(Func<bool> action);

/// <summary>A closure handed to a delegate of a declared type, whose only target runs what it is given: the arguments of a delegate
/// invocation are judged against every method ever bound to that delegate type.</summary>
internal static class CompliantClosurePassedThroughADelegate
{
    internal static bool Run(LibraryStore store, MutationLease lease)
    {
        FixtureApply apply = action => action();
        return apply(() => store.CreateEmptyDatabase(lease));
    }
}

/// <summary>A closure handed to a first-party helper that only runs it: proven by the helper's own body (the delegate is the receiver of
/// <c>Invoke</c> and nothing else), so the closure stays inside the host's call.</summary>
internal static class CompliantSynchronousHelper
{
    internal static bool Run(LibraryStore store, MutationLease lease) => Apply(() => store.CreateEmptyDatabase(lease));

    private static bool Apply(Func<bool> action) => action();
}

/// <summary>A lambda that takes a lease parameter is a unit of its own and borrows nothing, so it may be stored: whoever runs it must
/// hold a lease to give it. The invocation, in a method that takes a lease, is accepted.</summary>
internal static class CompliantLeaseTakingDelegate
{
    private static readonly Func<LibraryStore, MutationLease, bool> Create = static (store, lease) => store.CreateEmptyDatabase(lease);

    internal static bool Run(LibraryStore store, MutationLease lease) => Create(store, lease);
}

/// <summary>Overloads that differ only by generic arity, all private and called only from the one method that takes a lease: accepted
/// (each is a method of its own).</summary>
internal static class CompliantGenericOverloads
{
    internal static void Run(string path, MutationLease lease)
    {
        _ = lease;
        Make(path);
        Make<int>(path);
        Make<int, string>(path);
    }

    private static void Make(string path) => Directory.CreateDirectory(path);

    private static void Make<T>(string path) => Directory.CreateDirectory(path + typeof(T).Name);

    private static void Make<T, U>(string path) => Directory.CreateDirectory(path + typeof(T).Name + typeof(U).Name);
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

/// <summary>The one place C# lets overloads share a name AND parameters: conversion operators that differ by their return type. Their
/// identity carries the return type, so they are two methods, not one (and not ambiguous).</summary>
internal sealed class ConversionSource
{
    public static implicit operator int(ConversionSource source) => source is null ? 0 : 1;

    public static implicit operator long(ConversionSource source) => source is null ? 0L : 2L;
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

// ================================================================ method identity: overloads that differ in generic arity or in shape (C4R-M05)

/// <summary>Violation (A25a/not-private): the reviewer's evasion. A private generic helper is called from a method that takes a lease; an
/// internal NON-generic overload with the same name and parameters, declared after it, runs the mutation without one. The earlier keys had
/// no generic arity, so both shared one unit, which took its privacy and its callers from the first.</summary>
internal static class ArityAliasNonGenericAfterGeneric
{
    internal static void Run(string path, MutationLease lease)
    {
        _ = lease;
        Pick<int>(path);
    }

    private static void Pick<T>(string path) => _ = path.Length + typeof(T).Name.Length;

    internal static void Pick(string path) => Directory.CreateDirectory(path);
}

/// <summary>Violation (A25a/not-private): the same with the order reversed: the private non-generic helper first, the lease-less generic
/// overload after it.</summary>
internal static class ArityAliasGenericAfterNonGeneric
{
    internal static void Run(string path, MutationLease lease)
    {
        _ = lease;
        Pick(path);
    }

    private static void Pick(string path) => _ = path.Length;

    internal static void Pick<T>(string path) => Directory.CreateDirectory(path + typeof(T).Name);
}

/// <summary>Violation (A25a/not-private): overloads that differ only by HOW MANY type parameters they have. The private one is
/// called under a lease; the one with two type parameters is internal, lease-less and mutates.</summary>
internal static class ArityDiffers
{
    internal static void Run(string path, MutationLease lease)
    {
        _ = lease;
        Make<int>(path);
    }

    private static void Make<T>(string path) => _ = path.Length + typeof(T).Name.Length;

    internal static void Make<T, U>(string path) => Directory.CreateDirectory(path + typeof(T).Name + typeof(U).Name);
}

/// <summary>Violation (A25a/caller-without-lease): a mutation primitive that is reachable ONLY through the generic overload. A lease-less
/// method calls the private generic helper that creates the directory; the non-generic private helper next to it, called under a lease,
/// does nothing. The two must not be judged as one.</summary>
internal static class GenericPathOnly
{
    internal static void Run(string path, MutationLease lease)
    {
        _ = lease;
        Helper(path);
    }

    private static void Helper(string path) => _ = path.Length;

    internal static void Poke(string path) => Helper<int>(path);

    private static void Helper<T>(string path) => Directory.CreateDirectory(path + typeof(T).Name);
}

/// <summary>Violation (A25a/not-private): same parameter COUNT, different types. The private overload takes a string, the lease-less
/// internal one an int; the instantiations of one generic type differ only by their arguments; arrays differ only by their rank.</summary>
internal static class ParameterTypesDiffer
{
    internal static void Run(string path, MutationLease lease)
    {
        _ = lease;
        Do(path);
        Do(new List<int>());
        Do(new int[1, 1]);
    }

    private static void Do(string path) => _ = path.Length;

    internal static void Do(int number) => Directory.CreateDirectory("x" + number);

    private static void Do(List<int> numbers) => _ = numbers.Count;

    internal static void Do(List<string> names) => Directory.CreateDirectory("x" + names.Count);

    private static void Do(int[,] grid) => _ = grid.Length;

    internal static void Do(int[,,] cube) => Directory.CreateDirectory("x" + cube.Length);
}

/// <summary>Violation (A25a/not-private): overloads that differ only by a by-reference parameter.</summary>
internal static class ByReferenceDiffers
{
    internal static void Run(int number, MutationLease lease)
    {
        _ = lease;
        Do(number);
    }

    private static void Do(int number) => _ = number;

    internal static void Do(ref int number) => Directory.CreateDirectory("x" + number);
}

// ================================================================ delegates and closures: authority borrowed by capture must stay in the host (C4R-M05)

/// <summary>Violation (A25a/closure-escapes): a closure that captures the lease is stored in an instance field by a method that takes the
/// lease, and run later by a method that takes none (the reviewer's evasion, with the lease-taking host and the lease-less invoker).</summary>
internal sealed class ClosureStoredInAField
{
    private Func<bool>? _retained;

    internal void Arm(LibraryStore store, MutationLease lease) => _retained = () => store.CreateEmptyDatabase(lease);

    internal bool Poke() => _retained!();
}

/// <summary>Violation (A25a/closure-escapes): the same through a static field.</summary>
internal static class ClosureStoredInAStaticField
{
    private static Func<bool>? _retained;

    internal static void Arm(LibraryStore store, MutationLease lease) => _retained = () => store.CreateEmptyDatabase(lease);

    internal static bool Poke() => _retained!();
}

/// <summary>Violation (A25a/closure-escapes): the closure is put in an array.</summary>
internal static class ClosureStoredInAnArray
{
    internal static Func<bool>[] Arm(LibraryStore store, MutationLease lease) => [() => store.CreateEmptyDatabase(lease)];
}

/// <summary>Violation (A25a/closure-escapes): a lease-taking method returns the closure it built.</summary>
internal static class ClosureReturned
{
    internal static Func<bool> Build(LibraryStore store, MutationLease lease) => () => store.CreateEmptyDatabase(lease);
}

/// <summary>Violation (A25a/not-private and A25a/closure-escapes): a lease-less method returns a closure that runs a mutation with a lease
/// it kept in a field.</summary>
internal sealed class ClosureReturnedByALeaselessMethod(LibraryStore store, MutationLease lease)
{
    internal Func<bool> Build() => () => store.CreateEmptyDatabase(lease);
}

/// <summary>Violation (A25a/closure-escapes): the closure is handed to a lease-less helper that keeps it in a field.</summary>
internal sealed class ClosurePassedToAMethodThatKeepsIt
{
    private Func<bool>? _kept;

    internal void Run(LibraryStore store, MutationLease lease) => Keep(() => store.CreateEmptyDatabase(lease));

    private void Keep(Func<bool> action) => _kept = action;

    internal bool Later() => _kept!();
}

/// <summary>Violation (A25a/closure-escapes): the closure is handed to a helper that returns it, so its caller could keep it.</summary>
internal static class ClosurePassedToAMethodThatReturnsIt
{
    internal static bool Run(LibraryStore store, MutationLease lease) => Echo(() => store.CreateEmptyDatabase(lease)) is not null;

    private static Func<bool> Echo(Func<bool> action) => action;
}

/// <summary>Violation (A25a/closure-escapes): the closure is handed to code the audit cannot read (the thread pool), which runs it when it
/// likes.</summary>
internal static class ClosurePassedToTheThreadPool
{
    internal static Task<bool> Run(LibraryStore store, MutationLease lease) => Task.Run(() => store.CreateEmptyDatabase(lease));
}

/// <summary>Violation (A25a/closure-escapes): a local function converted to a delegate, which is stored.</summary>
internal sealed class LocalFunctionStoredAsADelegate
{
    private Func<bool>? _retained;

    internal void Arm(LibraryStore store, MutationLease lease)
    {
        _retained = Create;

        bool Create() => store.CreateEmptyDatabase(lease);
    }

    internal bool Poke() => _retained!();
}

/// <summary>Violation (A25a/closure-escapes): the carrier is handed to code that returns the field the delegate was stored in.</summary>
internal static class CarrierDelegateReturned
{
    internal static bool Run(LibraryStore store, MutationLease lease) => Leak(new FixtureHolder(() => store.CreateEmptyDatabase(lease), "n")) is not null;

    private static Func<bool> Leak(FixtureHolder holder) => holder.Action;
}

internal delegate void FixtureKeep(Func<bool> action);

/// <summary>Violation (A25a/closure-escapes): the closure is handed to a delegate whose target keeps what it is given.</summary>
internal sealed class ClosurePassedThroughADelegateThatKeepsIt
{
    private Func<bool>? _kept;

    internal void Run(LibraryStore store, MutationLease lease)
    {
        FixtureKeep keep = action => _kept = action;
        keep(() => store.CreateEmptyDatabase(lease));
    }

    internal bool Later() => _kept!();
}

/// <summary>Violation (A25a/closure-escapes): the closure is stored through a property setter, whose body keeps it in the backing field.</summary>
internal sealed class ClosureStoredThroughAProperty
{
    internal Func<bool>? Retained { get; set; }

    internal void Arm(LibraryStore store, MutationLease lease) => Retained = () => store.CreateEmptyDatabase(lease);

    internal bool Poke() => Retained!();
}

/// <summary>Violation (A25a/closure-escapes, failing closed): the closure is run through <c>DynamicInvoke</c>, a call into code the audit
/// cannot read. Running it there would be harmless; the audit cannot tell, so it refuses (use <c>Invoke</c>).</summary>
internal static class ClosureRunByDynamicInvoke
{
    internal static object? Run(LibraryStore store, MutationLease lease)
    {
        Func<bool> create = () => store.CreateEmptyDatabase(lease);
        return create.DynamicInvoke();
    }
}

/// <summary>Violation (A25a/closure-escapes): the closure is subscribed to an event, which keeps it.</summary>
internal sealed class ClosureSubscribedToAnEvent
{
    internal event Func<bool>? Fired;

    internal void Arm(LibraryStore store, MutationLease lease) => Fired += () => store.CreateEmptyDatabase(lease);

    internal bool Raise() => Fired?.Invoke() ?? false;
}

/// <summary>Violation (A25a/closure-escapes): the closure is captured by a second closure, which is stored.</summary>
internal sealed class ClosureCapturedByAnotherClosure
{
    private Func<bool>? _outer;

    internal void Arm(LibraryStore store, MutationLease lease)
    {
        Func<bool> inner = () => store.CreateEmptyDatabase(lease);
        _outer = () => inner();
    }

    internal bool Poke() => _outer!();
}

/// <summary>Violation (A25a/closure-escapes): an ASYNC lambda that captures the lease, stored. Its work is in the <c>MoveNext</c> of a
/// state machine the compiler made out of it, so the method bound to the delegate is only the stub that starts it.</summary>
internal sealed class AsyncClosureStoredInAField
{
    private Func<Task<bool>>? _retained;

    internal void Arm(LibraryStore store, MutationLease lease) => _retained = async () =>
    {
        await Task.Yield();
        return store.CreateEmptyDatabase(lease);
    };

    internal Task<bool> Poke() => _retained!();
}

/// <summary>Violation (A25a/closure-escapes): an iterator that holds the lease and creates the database when somebody enumerates it, which
/// can be after the method that took the lease has returned.</summary>
internal static class IteratorHoldingALease
{
    internal static IEnumerable<bool> Run(LibraryStore store, MutationLease lease)
    {
        yield return store.CreateEmptyDatabase(lease);
    }
}

/// <summary>Violation (A25a/closure-escapes): the closure is kept across an <c>await</c>, so it lives in the state machine's field.</summary>
internal static class ClosureHeldAcrossAnAwait
{
    internal static async Task<bool> Run(LibraryStore store, MutationLease lease)
    {
        Func<bool> create = () => store.CreateEmptyDatabase(lease);
        await Task.Yield();
        return create();
    }
}

/// <summary>Violation (A25a/closure-escapes): a private lease-less host (called only by a method that takes the lease, so the frozen
/// rule accepts it) builds a closure that uses a lease kept in a field, and stores it.</summary>
internal sealed class ClosureStoredByAPrivateHelper(LibraryStore store, MutationLease lease)
{
    private Func<bool>? _retained;

    internal void Run(MutationLease own)
    {
        _ = own;
        Arm();
    }

    private void Arm() => _retained = () => store.CreateEmptyDatabase(lease);

    internal bool Poke() => _retained!();
}

/// <summary>Violation (A25a/closure-escapes): a method group over a PRIVATE helper that reaches a mutation (the helper's authority comes
/// from the lease-taking method that calls it, and building the delegate counts as that call; binding it to a delegate that is stored
/// lets any caller run it).</summary>
internal sealed class MethodGroupOfAPrivateHelperStored
{
    private Action? _retained;

    internal void Run(MutationLease lease)
    {
        _ = lease;
        Create();
        _retained = Create;
    }

    private void Create() => Directory.CreateDirectory("armed");

    internal void Poke() => _retained!();
}

/// <summary>Violation (A25a/closure-escapes): the Library's own runner, built from closures that capture the lease, is kept in a field
/// instead of being used inside the host and handed to the verifier.</summary>
internal sealed class RunnerRetainedInAField
{
    private DelegateQueryRunner? _runner;

    internal void Arm(WriterConnection writer, MutationLease lease) => _runner = new DelegateQueryRunner(
        (sql, parameters) => writer.Scalar(lease, sql, parameters),
        (sql, each, parameters) => writer.Rows(lease, sql, each, parameters));

    internal object? Poke() => _runner!.Scalar(OpenSql.GetApplicationId);
}

/// <summary>Violation (A25a/closure-escapes): the runner is returned.</summary>
internal static class RunnerReturned
{
    internal static IQueryRunner Build(WriterConnection writer, MutationLease lease) => new DelegateQueryRunner(
        (sql, parameters) => writer.Scalar(lease, sql, parameters),
        (sql, each, parameters) => writer.Rows(lease, sql, each, parameters));
}

/// <summary>Violation (A25a/closure-escapes): the runner is handed to a first-party method that stores it (a verifier that keeps its
/// query runner), so the closures outlive the call.</summary>
internal sealed class RunnerHandedToAKeeper
{
    private IQueryRunner? _kept;

    internal string? Run(WriterConnection writer, MutationLease lease)
    {
        var runner = new DelegateQueryRunner(
            (sql, parameters) => writer.Scalar(lease, sql, parameters),
            (sql, each, parameters) => writer.Rows(lease, sql, each, parameters));
        return Verify(runner);
    }

    private string? Verify(IQueryRunner runner)
    {
        _kept = runner;
        return SnapshotVerifier.Verify(runner, 1, 1, 0, 0, 0, 0, true);
    }
}

/// <summary>Violation (A25a/not-private): a delegate that takes a lease, invoked by a method that has none, with a lease it kept in a
/// field. (The invocation of a delegate is not a call the audit can resolve, so it counts as a call to a method that takes a lease.)</summary>
internal sealed class RetainedLeaseTakingDelegate(Func<LibraryStore, MutationLease, bool> operation, LibraryStore store, MutationLease lease)
{
    internal bool Poke() => operation(store, lease);
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

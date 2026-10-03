using System.Reflection;
using StorageInventory.Testing;

namespace StorageInventory.Library.Tests;

/// <summary>
/// The run-time half of A-25 and OBS-15, from the abuser's side (C4-H01, TEST-W6): hold a real <see cref="WriterConnection"/> and offer it
/// every kind of lease that must not authorise work on it. Every operation of the writer's types is tried with every abuse, and each must
/// refuse BEFORE any I/O (no statement runs, no transaction starts or ends, no file changes) and leave the interlock in terminal Faulted.
/// "Before any I/O" is shown, where the operation has an observable effect, by a side channel: a statement that would raise a SQLite error
/// if it ran (a primary-key conflict, an integer overflow) raises the lease refusal instead; <c>BEGIN IMMEDIATE</c> would call the
/// AfterBegin hook; <c>COMMIT</c> and <c>ROLLBACK</c> would clear <see cref="WriterConnection.InTransaction"/>; a cancellation scope would
/// install the token. Each case uses a fresh session, because a failed OBS-15 check faults the interlock that was asked.
/// </summary>
public static class LeaseRuntimeAbuseTests
{
    /// <summary>A statement that fails with a primary-key conflict if it is executed.</summary>
    private const string DuplicateInsert = "INSERT INTO library_info (singleton, library_id, created_utc, created_app_version, last_opened_app_version, next_snapshot_id) VALUES (1, 'x', 1, 'a', 'a', 1)";

    /// <summary>A query that fails with an integer overflow when it is stepped (and prepares fine).</summary>
    private const string Overflow = "SELECT abs(-9223372036854775808)";

    private sealed class Rig : IDisposable
    {
        internal required World World { get; init; }
        internal required LibrarySession Session { get; init; }
        internal required MutationLease Opener { get; init; }
        internal required WriterConnection Writer { get; init; }
        internal required LibraryFaultInjection Faults { get; init; }
        internal required int[] AfterBeginCallsBox { get; init; }
        internal MutationLease StaleLease { get; init; }
        internal WriterStatement? Insert;
        internal WriterStatement? Query;
        internal LibrarySession? OtherSession;
        internal ObservationLease? Observation;
        internal MutationLease? SaveLease;
        internal MutationLease? ForeignLease;

        public void Dispose()
        {
            try { Insert?.Dispose(); Query?.Dispose(); } catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { /* already gone */ }
            try { Writer.Dispose(); } catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or LeaseViolationException) { /* best effort */ }
            try { SaveLease?.Dispose(); Observation?.Dispose(); ForeignLease?.Dispose(); } catch (Exception ex) when (ex is InvalidOperationException or LeaseViolationException) { /* best effort */ }
            Session.TestOnlyShutdown();
            OtherSession?.TestOnlyShutdown();
        }
    }

    private sealed record Abuse(string Name, MutationKind[] Allowed, bool StaleFirst, Func<Rig, MutationLease> Make, string Reason, bool OwnLease);

    private static MutationLease HandOffToSave(Rig rig)
    {
        // the writer stays open; only the interlock's bookkeeping of the opener's resource is released so that the hand-off is not refused
        rig.Opener.ResourceReleased();
        rig.Observation = rig.Opener.HandOffToObservation();
        rig.SaveLease = rig.Observation.Value.HandOffToSave(out _);
        return rig.SaveLease.Value;
    }

    private static MutationLease ForeignLeaseOf(Rig rig, bool equalId)
    {
        var world = World.Create("foreign");
        rig.OtherSession = world.CreatedSession();
        if (!equalId) World.Lease(rig.OtherSession, MutationKind.Create).Dispose();   // one more grant: the ids no longer coincide
        rig.ForeignLease = World.Lease(rig.OtherSession, MutationKind.Prepare, 1);
        Assert.Equal(equalId, rig.ForeignLease.Value.Id == rig.Opener.Id, "the premise about the lease ids");
        Assert.False(ReferenceEquals(rig.ForeignLease.Value.Owner, rig.Opener.Owner), "the lease belongs to another interlock");
        return rig.ForeignLease.Value;
    }

    private static readonly Abuse[] Abuses =
    [
        new("a default lease", [MutationKind.Prepare], false, _ => default, "no lease", false),
        new("a lease of ANOTHER LibrarySession with an EQUAL lease id", [MutationKind.Prepare], false, r => ForeignLeaseOf(r, equalId: true), "a lease of another LibrarySession", false),
        new("a lease of another LibrarySession with a different id", [MutationKind.Prepare], false, r => ForeignLeaseOf(r, equalId: false), "a lease of another LibrarySession", false),
        new("a stale lease of an earlier grant", [MutationKind.Prepare], true, r => r.StaleLease, "stale, disposed or was handed off", false),
        new("a disposed lease (the opener's own, ended cleanly)", [MutationKind.Prepare], false, r =>
        {
            r.Opener.ResourceReleased();   // bookkeeping only: the connection stays open so that the operations can be tried on it
            r.Opener.Dispose();
            return r.Opener;
        }, "stale, disposed or was handed off", true),
        new("a handed-off lease (the Prepare lease after HandOffToObservation)", [MutationKind.Prepare], false, r =>
        {
            r.Opener.ResourceReleased();
            r.Observation = r.Opener.HandOffToObservation();
            return r.Opener;
        }, "stale, disposed or was handed off", true),
        new("a current lease of the wrong kind (a Save lease; the writer allows Prepare only)", [MutationKind.Prepare], false, HandOffToSave, "a Save lease cannot authorise this", false),
        new("a CURRENT lease of this interlock that did not open the writer (the Save successor of the opener; kind allowed)", [MutationKind.Prepare, MutationKind.Save], false, HandOffToSave, "the lease is not the one that opened this connection", false),
    ];

    private static Rig Open(Abuse abuse, bool beginTransaction, bool prepareStatements)
    {
        var world = World.Create();
        var session = world.CreatedSession();
        MutationLease stale = default;
        if (abuse.StaleFirst)
        {
            stale = World.Lease(session, MutationKind.Prepare, 1);
            stale.Dispose();
        }
        var opener = World.Lease(session, MutationKind.Prepare, 1);
        var calls = new int[1];
        var faults = new LibraryFaultInjection { AfterBegin = () => calls[0]++ };
        var writer = LibraryDatabase.OpenWriter(opener, session.Interlock, world.Main, "abuse", abuse.Allowed, false, faults);
        var rig = new Rig { World = world, Session = session, Opener = opener, Writer = writer, Faults = faults, StaleLease = stale, AfterBeginCallsBox = calls };
        if (beginTransaction) writer.Begin(opener);
        if (prepareStatements)
        {
            rig.Insert = writer.Prepare(opener, DuplicateInsert);
            rig.Query = writer.Prepare(opener, Overflow);
        }
        return rig;
    }

    private sealed record Operation(string Name, string Message, bool NeedsTransaction, bool NeedsStatements, Action<Rig, MutationLease> Run, Func<Rig, string?> Effect);

    private static string? NoEffect(Rig rig) => null;

    private static readonly Operation[] Operations =
    [
        new("WriterConnection.Configure", "configure the connection", false, false, (r, l) => r.Writer.Configure(l, importCache: true), NoEffect),
        new("WriterConnection.AssertEngine", "assert the engine", false, false, (r, l) => r.Writer.AssertEngine(l), NoEffect),
        new("WriterConnection.CheckCurrent", "check the lease", false, false, (r, l) => r.Writer.CheckCurrent(l), NoEffect),
        new("WriterConnection.Guard", "probe", false, false, (r, l) => r.Writer.Guard(l, "probe"), NoEffect),
        new("WriterConnection.Begin", "begin the transaction", false, false, (r, l) => r.Writer.Begin(l),
            r => r.AfterBeginCallsBox[0] != 0 ? "BEGIN IMMEDIATE ran (the AfterBegin hook was called)" : r.Writer.InTransaction ? "a transaction was started" : null),
        new("WriterConnection.Commit", "commit the transaction", true, false, (r, l) => r.Writer.Commit(l), r => r.Writer.InTransaction ? null : "COMMIT ran (the transaction ended)"),
        new("WriterConnection.Rollback", "roll back", true, false, (r, l) => r.Writer.Rollback(l), r => r.Writer.InTransaction ? null : "ROLLBACK ran (the transaction ended)"),
        new("WriterConnection.Prepare", "prepare a statement", false, false, (r, l) => r.Writer.Prepare(l, Overflow).Dispose(), NoEffect),
        new("WriterConnection.Scalar", "query", false, false, (r, l) => r.Writer.Scalar(l, Overflow), NoEffect),
        new("WriterConnection.Rows", "prepare a statement", false, false, (r, l) => r.Writer.Rows(l, Overflow, _ => { }), NoEffect),
        new("WriterConnection.PageCount", "query", false, false, (r, l) => r.Writer.PageCount(l), NoEffect),
        new("WriterConnection.PageSize", "query", false, false, (r, l) => r.Writer.PageSize(l), NoEffect),
        new("WriterConnection.BeginCancellationScope", "start a cancellation scope", false, false,
            (r, l) => r.Writer.BeginCancellationScope(l, new CancellationToken(true), new TokenChecker(new CancellationToken(true))).Dispose(),
            r => r.Writer.ScopeToken.IsCancellationRequested ? "the scope's token was installed" : null),
        new("WriterStatement.ExecuteNonQuery(lease)", "execute", false, true, (r, l) => r.Insert!.ExecuteNonQuery(l), NoEffect),
        new("WriterStatement.ExecuteNonQuery(lease, token)", "execute", false, true, (r, l) => r.Insert!.ExecuteNonQuery(l, CancellationToken.None), NoEffect),
        new("WriterStatement.ExecuteRows", "execute", false, true, (r, l) => r.Query!.ExecuteRows(l, _ => { }), NoEffect),
        new("WriterStatement.ExecuteInsertIfAbsent", "execute", false, true, (r, l) => r.Insert!.ExecuteInsertIfAbsent(l, out _), NoEffect),
        new("WriterStatement.ExecuteInsert", "execute", false, true, (r, l) => r.Insert!.ExecuteInsert(l), NoEffect),
        new("WriterStatement.ExecuteScalar", "execute", false, true, (r, l) => r.Query!.ExecuteScalar(l), NoEffect),
    ];

    private static void MakeEngineBogus(Rig rig) =>
        typeof(LibraryFaultInjection).GetProperty(nameof(LibraryFaultInjection.ExpectedEngine), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(rig.Faults, ("0.0.0", "bogus"));

    private static void RunAbuse(Abuse abuse)
    {
        var failures = new List<string>();
        foreach (var operation in Operations)
        {
            // a Rollback by the opener's own, ended lease is a best-effort release and is allowed: it is not refused
            var allowedRollback = operation.Name == "WriterConnection.Rollback" && abuse.OwnLease;
            using var rig = Open(abuse, operation.NeedsTransaction, operation.NeedsStatements);
            if (operation.Name == "WriterConnection.AssertEngine") MakeEngineBogus(rig);   // if it ran, it would throw UnexpectedEngineException
            var lease = abuse.Make(rig);
            var hashesBefore = rig.World.ContentHashes();
            var label = $"{abuse.Name}: {operation.Name}";
            if (allowedRollback)
            {
                try { operation.Run(rig, lease); }
                catch (Exception ex) { failures.Add($"{label}: the best-effort rollback with the opener's own lease was refused: {ex.GetType().Name}: {ex.Message}"); continue; }
                if (rig.Writer.InTransaction) failures.Add($"{label}: the rollback did not end the transaction");
                if (rig.Session.Interlock.IsFaulted) failures.Add($"{label}: an allowed rollback faulted the interlock");
                continue;
            }
            Exception? thrown = null;
            try { operation.Run(rig, lease); }
            catch (Exception ex) { thrown = ex; }
            if (thrown is not LeaseViolationException refusal) { failures.Add($"{label}: expected a LeaseViolationException (refused before any I/O), got {thrown?.GetType().Name ?? "nothing"}: {thrown?.Message}"); continue; }
            if (!refusal.Message.StartsWith("Refused before any I/O", StringComparison.Ordinal)) failures.Add($"{label}: the refusal does not say it was before any I/O: {refusal.Message}");
            if (!refusal.Message.Contains(operation.Message, StringComparison.Ordinal)) failures.Add($"{label}: the refusal does not name the operation '{operation.Message}': {refusal.Message}");
            var reason = operation.Name == "WriterConnection.Rollback" ? "the lease is not the one that opened this connection" : abuse.Reason;
            if (!refusal.Message.Contains(reason, StringComparison.Ordinal)) failures.Add($"{label}: the refusal is not for the intended reason '{reason}': {refusal.Message}");
            if (!rig.Session.Interlock.IsFaulted) failures.Add($"{label}: the interlock did not enter Faulted");
            if (operation.Effect(rig) is { } effect) failures.Add($"{label}: I/O happened before the refusal: {effect}");
            var hashesAfter = rig.World.ContentHashes();
            if (!hashesBefore.OrderBy(h => h.Key).SequenceEqual(hashesAfter.OrderBy(h => h.Key))) failures.Add($"{label}: a Library file changed");
        }
        Assert.Equal(0, failures.Count, Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    [Test]
    public static void A_default_lease_is_refused_by_every_writer_operation_before_any_io() => RunAbuse(Abuses[0]);

    [Test]
    public static void A_lease_of_another_LibrarySession_with_an_equal_id_is_refused_by_every_writer_operation_before_any_io() => RunAbuse(Abuses[1]);

    [Test]
    public static void A_lease_of_another_LibrarySession_with_a_different_id_is_refused_by_every_writer_operation_before_any_io() => RunAbuse(Abuses[2]);

    [Test]
    public static void A_stale_lease_of_an_earlier_grant_is_refused_by_every_writer_operation_before_any_io() => RunAbuse(Abuses[3]);

    [Test]
    public static void A_disposed_lease_is_refused_by_every_writer_operation_before_any_io_except_the_best_effort_rollback() => RunAbuse(Abuses[4]);

    [Test]
    public static void A_handed_off_lease_is_refused_by_every_writer_operation_before_any_io_except_the_best_effort_rollback() => RunAbuse(Abuses[5]);

    [Test]
    public static void A_current_lease_of_the_wrong_kind_is_refused_by_every_writer_operation_before_any_io() => RunAbuse(Abuses[6]);

    [Test]
    public static void A_current_lease_of_the_same_interlock_that_did_not_open_the_writer_is_refused_by_every_writer_operation_before_any_io() => RunAbuse(Abuses[7]);

    [Test]
    public static void Every_lease_taking_member_of_the_writer_types_has_an_abuse_case()
    {
        // so that an operation added to WriterConnection or WriterStatement cannot escape these tests
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var taking = new[] { typeof(WriterConnection), typeof(WriterStatement) }
            .SelectMany(t => t.GetMethods(all).Where(m => m.GetParameters().Any(p => p.ParameterType == typeof(MutationLease))).Select(m => $"{t.Name}.{m.Name}"))
            .Distinct().Order(StringComparer.Ordinal).ToList();
        var covered = Operations.Select(o => o.Name.Contains('(', StringComparison.Ordinal) ? o.Name[..o.Name.IndexOf('(', StringComparison.Ordinal)] : o.Name).Distinct().ToHashSet();
        var missing = taking.Where(t => !covered.Contains(t)).ToList();
        Assert.Equal(0, missing.Count, "lease-taking members without an abuse case: " + string.Join(", ", missing));
        Assert.True(taking.Count >= 17, $"{taking.Count} lease-taking members were found: " + string.Join(", ", taking));
        Assert.True(Abuses.Length == 8, "eight kinds of misused lease");
    }

    [Test]
    public static void Rollback_after_the_openers_own_lease_went_stale_is_allowed_but_a_default_or_foreign_lease_is_refused()
    {
        // allowed: the opener's own lease, after it was handed off (stale): a rollback only removes uncommitted work (OBS-13 best effort)
        using (var rig = Open(Abuses[5], beginTransaction: true, prepareStatements: false))
        {
            var lease = Abuses[5].Make(rig);
            Assert.False(rig.Session.Interlock.IsCurrent(lease), "the opener's lease is no longer current");
            rig.Writer.Rollback(lease);
            Assert.False(rig.Writer.InTransaction, "the transaction was rolled back");
            Assert.False(rig.Session.Interlock.IsFaulted, "a best-effort rollback does not fault the interlock");
        }
        // and when the interlock is Faulted already (the writer closing after a failure): still allowed for the opener's own lease
        using (var rig = Open(Abuses[3], beginTransaction: true, prepareStatements: false))
        {
            rig.Session.Interlock.ReportCatastrophic("test: a class C failure outside any lease");
            Assert.True(rig.Session.Interlock.IsFaulted);
            rig.Writer.Rollback(rig.Opener);
            Assert.False(rig.Writer.InTransaction, "the rollback ran although the interlock is Faulted");
        }
        // refused: default, a foreign lease (equal id), another current lease of the interlock
        foreach (var index in new[] { 0, 1, 2, 3, 6, 7 })
        {
            using var rig = Open(Abuses[index], beginTransaction: true, prepareStatements: false);
            var lease = Abuses[index].Make(rig);
            var refusal = Assert.Throws<LeaseViolationException>(() => rig.Writer.Rollback(lease));
            Assert.Contains("the lease is not the one that opened this connection", refusal.Message);
            Assert.True(rig.Writer.InTransaction, Abuses[index].Name + ": the rollback did not run");
            Assert.True(rig.Session.Interlock.IsFaulted, Abuses[index].Name + ": Faulted");
        }
    }

    [Test]
    public static void The_openers_own_current_lease_is_accepted_by_every_writer_operation()
    {
        // the control: the same operations with the right lease are not refused (so the refusals above are about the lease, not the rig)
        foreach (var operation in Operations.Where(o => o.Name != "WriterConnection.Commit" && o.Name != "WriterConnection.Rollback" && o.Name != "WriterConnection.AssertEngine"))
        {
            using var rig = Open(Abuses[0], beginTransaction: false, prepareStatements: operation.NeedsStatements);
            Exception? thrown = null;
            try { operation.Run(rig, rig.Opener); }
            catch (Exception ex) { thrown = ex; }
            Assert.False(thrown is LeaseViolationException, $"{operation.Name}: the opener's own lease was refused: {thrown?.Message}");
            Assert.False(rig.Session.Interlock.IsFaulted, $"{operation.Name}: the interlock is Faulted");
        }
        using (var rig = Open(Abuses[0], beginTransaction: false, prepareStatements: false))
        {
            rig.Writer.AssertEngine(rig.Opener);
            rig.Writer.Begin(rig.Opener);
            rig.Writer.Commit(rig.Opener);
            rig.Writer.Begin(rig.Opener);
            rig.Writer.Rollback(rig.Opener);
            Assert.False(rig.Session.Interlock.IsFaulted);
        }
    }
}

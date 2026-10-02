using StorageInventory.Testing;

namespace StorageInventory.Library.Tests;

/// <summary>
/// TEST-W6: a lease authorises work only while it is current (OBS-15, A-25; G0F-M03). Each case asserts that the operation is
/// refused BEFORE any I/O (no Library member created, changed or deleted; no connection opened), and that the interlock enters
/// Faulted where OBS-15 says a non-current lease can only be a lifecycle defect. The static half is the audit's A-25 (a) and (c).
/// </summary>
public static class LeaseValidityTests
{
    private static void RefusedBeforeIo(World world, LibrarySession session, Action operation, bool expectFaulted, string label)
    {
        var namesBefore = world.Names();
        var hashesBefore = Directory.Exists(world.LibraryDirectory) ? world.ContentHashes() : [];
        var directoryExisted = Directory.Exists(world.LibraryDirectory);
        Assert.Throws<LeaseViolationException>(operation);
        Assert.Equal(directoryExisted, Directory.Exists(world.LibraryDirectory), label + ": no directory was created");
        Assert.SequenceEqual(namesBefore, world.Names(), label + ": no member was created or deleted");
        if (directoryExisted) Assert.SequenceEqual(hashesBefore.OrderBy(h => h.Key).Select(h => h.Key + "=" + h.Value), world.ContentHashes().OrderBy(h => h.Key).Select(h => h.Key + "=" + h.Value), label + ": no member's contents changed");
        Assert.Equal(expectFaulted, session.Interlock.IsFaulted, label + ": Faulted");
    }

    [Test]
    public static void A_default_lease_authorises_nothing()
    {
        // Each case runs on a fresh session: a failed OBS-15 check faults the interlock that was asked, so the first would otherwise
        // fault the rest. LibraryDatabase has no interlock of its own to fault when it is offered a default lease.
        var cases = new (string Name, Action<World, LibrarySession> Operation, bool Faults)[]
        {
            ("EnsureLibraryFolder", (w, s) => s.Store.EnsureLibraryFolder(default), true),
            ("AcquireWriterLock", (w, s) => s.Store.AcquireWriterLock(default), true),
            ("CreateEmptyDatabase", (w, s) => s.Store.CreateEmptyDatabase(default), true),
            ("QuarantineSet", (w, s) => s.Store.QuarantineSet(default), true),
            ("CreateLibrary", (w, s) => s.CreateLibrary(default), true),
            ("DeleteSnapshot", (w, s) => s.DeleteSnapshotAsync(default, 1).GetAwaiter().GetResult(), true),
            ("OpenWriter", (w, s) => LibraryDatabase.OpenWriter(default, w.Main, "x", [MutationKind.Create], false, null), false),
        };
        foreach (var (name, operation, faults) in cases)
        {
            var world = World.Create();
            var session = world.OpenedSession();
            Assert.Equal(LibraryState.NotCreated, session.Status.State);
            RefusedBeforeIo(world, session, () => operation(world, session), faults, name);
            session.TestOnlyShutdown();
        }
    }

    [Test]
    public static void A_stale_disposed_lease_authorises_nothing_and_faults_the_interlock()
    {
        foreach (var kind in new[] { MutationKind.Open, MutationKind.Create, MutationKind.Prepare, MutationKind.Delete, MutationKind.SetAside })
        {
            var world = World.Create();
            var session = world.OpenedSession();
            var lease = World.Lease(session, kind);
            lease.Dispose();
            Assert.Equal(InterlockStateKind.Idle, session.Interlock.Snapshot.Kind);
            RefusedBeforeIo(world, session, () => session.Store.EnsureLibraryFolder(lease), true, $"a disposed {kind} lease offered to EnsureLibraryFolder");
            session.TestOnlyShutdown();
        }
    }

    [Test]
    public static void A_Delete_lease_used_after_it_was_disposed_cannot_delete()
    {
        var world = World.Create();
        var session = world.CreatedSession();
        var lease = World.Lease(session, MutationKind.Delete);
        lease.Dispose();
        RefusedBeforeIo(world, session, () => session.DeleteSnapshotAsync(lease, 1).GetAwaiter().GetResult(), true, "T-DELETE with a disposed Delete lease");
        session.TestOnlyShutdown();
    }

    [Test]
    public static void A_best_effort_T_OUTCOME_continuation_after_its_Save_lease_ended_is_refused()
    {
        var world = World.Create();
        var session = world.CreatedSession();
        using (var prepare = World.Lease(session, MutationKind.Prepare, owner: 1))
        {
            var attempt = session.RecordAttemptStartAsync(prepare, NewAttempt()).GetAwaiter().GetResult();
            var observation = prepare.HandOffToObservation();
            var save = observation.HandOffToSave(out _);
            save.Dispose();          // the Save lease has ended
            RefusedBeforeIo(world, session, () => session.RecordAttemptOutcomeAsync(save, attempt, AttemptOutcome.Cancelled, null, "late").GetAwaiter().GetResult(), true, "a late T-OUTCOME");
        }
        session.TestOnlyShutdown();
    }

    [Test]
    public static void An_observation_lease_used_after_its_hand_off_to_Save_is_refused_and_faults()
    {
        var interlock = new LibraryInterlock();
        interlock.TakeStartupLease().Dispose();
        interlock.TryBeginObservation(1, out var observation, out _);
        var save = observation.HandOffToSave(out _);
        Assert.Throws<LeaseViolationException>(() => interlock.RequireObservation(observation, "enumerate"));
        Assert.Equal(InterlockStateKind.Faulted, interlock.Snapshot.Kind);
        save.Dispose();
    }

    [Test]
    public static void A_lease_of_another_session_is_refused_by_every_Library_operation()
    {
        var worldA = World.Create("a");
        var worldB = World.Create("b");
        var a = worldA.CreatedSession();
        var b = worldB.CreatedSession();
        var foreign = World.Lease(a, MutationKind.Delete);

        RefusedBeforeIo(worldB, b, () => b.DeleteSnapshotAsync(foreign, 1).GetAwaiter().GetResult(), true, "B's T-DELETE with A's lease");
        var c = worldB.NewSession();   // a third session on B's directory; its own start-up lease is the only one it knows
        Assert.Throws<LeaseViolationException>(() => c.Store.AcquireWriterLock(foreign));
        Assert.True(c.Interlock.IsFaulted, "the session that was offered a foreign lease is Faulted");
        Assert.False(a.Interlock.IsFaulted, "the session that owns the lease is unaffected by the misuse");
        Assert.True(a.Interlock.IsCurrent(foreign), "and the lease is still current at its own interlock");
        foreign.Dispose();
        a.TestOnlyShutdown();
        b.TestOnlyShutdown();
    }

    [Test]
    public static void A_lease_of_the_wrong_kind_authorises_nothing()
    {
        var world = World.Create();
        var session = world.CreatedSession();
        using (var delete = World.Lease(session, MutationKind.Delete))
        {
            var header = new SyntheticSnapshot(3).Header();
            Assert.Throws<LeaseViolationException>(() => session.ImportSnapshotAsync(delete, new AttemptRef(1, new byte[16]), SyntheticSnapshot.NewSource(), header, new SyntheticSnapshot(3)).GetAwaiter().GetResult());
            Assert.True(session.Interlock.IsFaulted, "a Delete lease offered to T-IMPORT faults the interlock");
        }
        session.TestOnlyShutdown();

        var world2 = World.Create();
        var second = world2.CreatedSession();
        using (var prepare = World.Lease(second, MutationKind.Prepare))
        {
            var before = world2.Names();
            Assert.Throws<LeaseViolationException>(() => second.Store.QuarantineSet(prepare));
            Assert.SequenceEqual(before, world2.Names(), "a Prepare lease offered to a rename moved nothing");
            Assert.True(second.Interlock.IsFaulted);
        }
        second.TestOnlyShutdown();
    }

    [Test]
    public static void Disposing_a_lease_twice_changes_nothing_and_does_not_end_a_later_lease()
    {
        var interlock = new LibraryInterlock();
        interlock.TakeStartupLease().Dispose();
        interlock.TryBeginMutation(MutationKind.Create, 0, out var first, out _);
        first.Dispose();
        first.Dispose();
        Assert.Equal(InterlockStateKind.Idle, interlock.Snapshot.Kind);
        interlock.TryBeginMutation(MutationKind.Delete, 0, out var second, out _);
        first.Dispose();   // a stale copy must not end the new lease
        Assert.Equal(InterlockStateKind.Mutating, interlock.Snapshot.Kind, "a stale Dispose does not end another lease (ids are never reused)");
        Assert.True(interlock.IsCurrent(second));
        second.Dispose();
        Assert.False(interlock.IsFaulted, "double disposal is not a defect");
    }

    [Test]
    public static void A_lease_that_becomes_non_current_between_BEGIN_and_COMMIT_prevents_the_commit()
    {
        var world = World.Create();
        MutationLease captured = default;
        var faults = new LibraryFaultInjection { BeforeCommit = () => captured.Dispose() };   // made non-current just before the guard
        var session = world.CreatedSession(new LibrarySessionOptions { Faults = faults });
        using (var prepare = World.Lease(session, MutationKind.Prepare, owner: 1))
        {
            captured = prepare;
            Assert.Throws<LeaseViolationException>(() => session.RecordAttemptStartAsync(prepare, NewAttempt()).GetAwaiter().GetResult());
            Assert.True(session.Interlock.IsFaulted, "a non-current lease reaching COMMIT faults the interlock");
        }
        session.TestOnlyShutdown();

        // nothing was committed: a fresh session sees no attempt
        var verify = world.OpenedSession();
        Assert.Equal(LibraryState.Available, verify.Status.State);
        var attempts = verify.Read(r => r.Long("SELECT count(*) FROM scan_attempt"));
        Assert.Equal(0L, attempts, "the INSERT was rolled back, never committed");
        verify.TestOnlyShutdown();
    }

    [Test]
    public static void A_lease_checked_at_BEGIN_IMMEDIATE_is_refused_before_the_transaction_starts()
    {
        var world = World.Create();
        var session = world.CreatedSession();
        var lease = World.Lease(session, MutationKind.Prepare, owner: 1);
        // open the writer legitimately, then make the lease non-current before BEGIN
        var writer = LibraryDatabase.OpenWriter(lease, world.Main, "t", [MutationKind.Prepare], false, null);
        lease.Dispose();   // with the writer still open, this ends the lease in Faulted (a resource is open), so BEGIN must be refused
        Assert.Throws<LeaseViolationException>(() => writer.Begin(lease));
        Assert.False(writer.InTransaction, "no transaction was started");
        writer.Dispose();
        session.TestOnlyShutdown();
    }

    private static AttemptStart NewAttempt() => new(new byte[16], null, Utf16.ToBytes(@"D:\Media"), null, "run-1", DateTime.UtcNow.Ticks);
}

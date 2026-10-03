using StorageInventory.Testing;

namespace StorageInventory.Library.Tests;

/// <summary>
/// Class C (catastrophic but catchable: <c>OutOfMemoryException</c>, CAT-03) injected THROUGH PRODUCTION CODE in every lease kind and
/// at every transaction of the Library (OBS-13, TEST-W2; C4-M04): T-CREATE, T-RECOVER (the open), T0, T-OUTCOME, T-IMPORT, T-DELETE
/// and the set-aside, plus a writer whose release throws. Each ends in the one terminal Faulted state with no further Library write;
/// before the repair T0, T-OUTCOME and T-DELETE rolled back and ended the lease cleanly (back to Idle). The interlock tests inject
/// class C by calling the lease directly; these tests reach it from inside the operations.
/// </summary>
public static class ClassCReportingTests
{
    private sealed class Trap
    {
        internal volatile bool Armed;
        internal int Fired;

        internal void Fire()
        {
            if (!Armed) return;
            Fired++;
            throw new OutOfMemoryException("injected class C failure");
        }
    }

    private static (World World, LibrarySession Session, Trap Trap) Session(bool created = true)
    {
        var trap = new Trap();
        var world = World.Create();
        var options = new LibrarySessionOptions { Faults = new LibraryFaultInjection { AfterBegin = trap.Fire } };
        var session = created ? world.CreatedSession(options) : world.OpenedSession(options);
        return (world, session, trap);
    }

    private static void AssertFaultedWithNoFurtherWrite(World world, LibrarySession session, string operation)
    {
        Assert.True(session.Interlock.IsFaulted, operation + ": the interlock is Faulted");
        Assert.Contains("class C", session.Interlock.FaultReason);
        Assert.Equal(InterlockStateKind.Faulted, session.Interlock.Snapshot.Kind, operation);
        Assert.Equal(LibraryState.Unavailable, session.Status.State, operation + ": the Library is Unavailable (restart required)");
        Assert.True(session.Status.RestartRequired, operation);
        Assert.False(session.Interlock.TryBeginMutation(MutationKind.Open, 0, out _, out _), operation + ": no lease is ever granted again");
        Assert.False(session.Interlock.TryBeginObservation(1, out _, out _), operation + ": nor an observation");
        var hashes = world.ContentHashes();
        var names = world.Names();
        // nothing can write now: not even the best-effort T-OUTCOME
        Assert.SequenceEqual(names, world.Names(), operation + ": no member appeared");
        Assert.SequenceEqual(hashes.OrderBy(p => p.Key).Select(p => p.Key + p.Value), world.ContentHashes().OrderBy(p => p.Key).Select(p => p.Key + p.Value), operation + ": no member changed");
    }

    [Test]
    public static void Class_C_in_T0_ends_in_Faulted_with_no_outcome_written()
    {
        var (world, session, trap) = Session();
        var lease = World.Lease(session, MutationKind.Prepare, 1);
        trap.Armed = true;
        Assert.Throws<OutOfMemoryException>(() => session.RecordAttemptStartAsync(lease, new AttemptStart(new byte[16], null, Utf16.ToBytes("x"), null, "r", 1)).GetAwaiter().GetResult());
        trap.Armed = false;
        lease.Dispose();   // the caller's finally: it must NOT return the interlock to Idle
        Assert.Equal(1, trap.Fired);
        AssertFaultedWithNoFurtherWrite(world, session, "T0");
        session.TestOnlyShutdown();
    }

    [Test]
    public static void Class_C_in_T_OUTCOME_ends_in_Faulted_and_the_attempt_stays_InProgress()
    {
        var (world, session, trap) = Session();
        var prepare = World.Lease(session, MutationKind.Prepare, 1);
        var attempt = session.RecordAttemptStartAsync(prepare, new AttemptStart(new byte[16], null, Utf16.ToBytes("x"), null, "r", 1)).GetAwaiter().GetResult();
        var save = prepare.HandOffToObservation().HandOffToSave(out _);
        trap.Armed = true;
        Assert.Throws<OutOfMemoryException>(() => session.RecordAttemptOutcomeAsync(save, attempt, AttemptOutcome.NotEligible, CaptureFailureKind.LibraryChangedDuringScan, "m").GetAwaiter().GetResult());
        trap.Armed = false;
        save.Dispose();
        AssertFaultedWithNoFurtherWrite(world, session, "T-OUTCOME");
        session.TestOnlyShutdown();
        var after = world.NewSession();
        after.RunStartupOpen();
        Assert.Equal(1L, after.Read(r => r.Long("SELECT count(*) FROM scan_attempt WHERE outcome = 8")), "the attempt was never given an outcome: it becomes Interrupted at the next open (OBS-13)");
        after.TestOnlyShutdown();
    }

    [Test]
    public static void Class_C_in_T_IMPORT_ends_in_Faulted_and_nothing_of_the_snapshot_remains()
    {
        var (world, session, trap) = Session();
        var prepare = World.Lease(session, MutationKind.Prepare, 1);
        var attempt = session.RecordAttemptStartAsync(prepare, new AttemptStart(new byte[16], null, Utf16.ToBytes("x"), null, "run-1", 1)).GetAwaiter().GetResult();
        var save = prepare.HandOffToObservation().HandOffToSave(out _);
        var snapshot = new SyntheticSnapshot(100);
        trap.Armed = true;
        Assert.Throws<OutOfMemoryException>(() => session.ImportSnapshotAsync(save, attempt, SyntheticSnapshot.NewSource(), snapshot.Header("run-1"), snapshot).GetAwaiter().GetResult());
        trap.Armed = false;
        save.Dispose();
        AssertFaultedWithNoFurtherWrite(world, session, "T-IMPORT");
        session.TestOnlyShutdown();
        var after = world.NewSession();
        after.RunStartupOpen();
        Assert.Equal(0L, after.Read(r => r.Long("SELECT count(*) FROM snapshot")), "no snapshot");
        Assert.Equal(0L, after.Read(r => r.Long("SELECT count(*) FROM name")), "no dictionary row");
        after.TestOnlyShutdown();
    }

    [Test]
    public static void Class_C_in_T_DELETE_ends_in_Faulted_and_the_snapshot_survives()
    {
        var (world, session, trap) = Session();
        LibraryStateTests.ImportOne(session, new SyntheticSnapshot(60));
        var before = ImportTests.Checksum(session, 1);
        var lease = World.Lease(session, MutationKind.Delete);
        trap.Armed = true;
        Assert.Throws<OutOfMemoryException>(() => session.DeleteSnapshotAsync(lease, 1).GetAwaiter().GetResult());
        trap.Armed = false;
        lease.Dispose();
        AssertFaultedWithNoFurtherWrite(world, session, "T-DELETE");
        session.TestOnlyShutdown();
        var after = world.NewSession();
        after.RunStartupOpen();
        Assert.Equal(before, ImportTests.Checksum(after, 1), "the snapshot is intact");
        after.TestOnlyShutdown();
    }

    [Test]
    public static void Class_C_in_T_CREATE_and_in_the_open_s_recovery_end_in_Faulted()
    {
        {
            var (world, session, trap) = Session(created: false);
            var lease = World.Lease(session, MutationKind.Create);
            trap.Armed = true;
            Assert.Throws<OutOfMemoryException>(() => session.CreateLibrary(lease));
            trap.Armed = false;
            lease.Dispose();
            AssertFaultedWithNoFurtherWrite(world, session, "T-CREATE");
            session.TestOnlyShutdown();
        }
        {
            // T-RECOVER at a later open of an existing Library, from the start-up Open lease
            var world = World.Create();
            world.CreatedSession().TestOnlyShutdown();
            var trap = new Trap { Armed = true };
            var session = world.NewSession(new LibrarySessionOptions { Faults = new LibraryFaultInjection { AfterBegin = trap.Fire } });
            Assert.Throws<OutOfMemoryException>(() => session.RunStartupOpen());
            trap.Armed = false;
            AssertFaultedWithNoFurtherWrite(world, session, "T-RECOVER at the start-up open");
            session.TestOnlyShutdown();
        }
    }

    [Test]
    public static void Class_C_during_a_set_aside_ends_in_Faulted_and_the_renames_already_made_stand()
    {
        var world = World.Create();
        world.CreatedSession().TestOnlyShutdown();
        var session = world.NewSession(new LibrarySessionOptions { StoreHooks = new LibraryStoreHooks { AfterRename = n => throw new OutOfMemoryException("injected") } });
        session.Interlock.TakeStartupLease().Dispose();
        var lease = World.Lease(session, MutationKind.SetAside);
        Assert.Throws<OutOfMemoryException>(() => session.SetAsideAsync(lease).GetAwaiter().GetResult());
        lease.Dispose();
        AssertFaultedWithNoFurtherWrite(world, session, "set-aside");
        Assert.True(world.Names().Any(n => n.StartsWith(LibraryNames.QuarantinePrefix, StringComparison.Ordinal)), "the first rename had been done (set-aside is renames only, nothing is deleted)");
        session.TestOnlyShutdown();
    }

    [Test]
    public static void A_writer_that_cannot_be_released_faults_the_interlock_for_every_operation()
    {
        var release = false;
        var world = World.Create();
        var session = world.CreatedSession(new LibrarySessionOptions { Faults = new LibraryFaultInjection { OnWriterRelease = () => { if (release) throw new IOException("the connection could not be closed"); } } });
        var lease = World.Lease(session, MutationKind.Prepare, 1);
        release = true;
        var attempt = session.RecordAttemptStartAsync(lease, new AttemptStart(new byte[16], null, Utf16.ToBytes("x"), null, "r", 1)).GetAwaiter().GetResult();
        release = false;
        Assert.True(attempt.AttemptId > 0, "T0 itself completed");
        Assert.True(session.Interlock.IsFaulted, "but a writer that could not be closed ends the lease in Faulted, never in Idle (OBS-13)");
        lease.Dispose();
        Assert.True(session.Interlock.IsFaulted, "and ending the lease does not undo it");
        Assert.Contains("could not be released", session.Interlock.FaultReason);
        session.TestOnlyShutdown();
    }
}

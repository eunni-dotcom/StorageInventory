using StorageInventory.Testing;

namespace StorageInventory.Library.Tests;

/// <summary>TEST-L4 (the owned file set after lifecycle scenarios, A-23), the lifecycle audit of A-26, TEST-L7 (a recovery that
/// cannot complete) and the "pause inside every mutation kind" half of TEST-W2.</summary>
public static class LifecycleTests
{
    [Test]
    public static void Every_file_event_of_a_full_lifecycle_happens_inside_a_mutation_lease_and_none_while_Idle_or_Observing()
    {
        var world = World.Create();
        Directory.CreateDirectory(world.AppData);
        var session = world.NewSession();
        using var audit = new SideFileAudit(world, session);

        Assert.Equal(LibraryState.NotCreated, session.RunStartupOpen().State);

        using (var create = World.Lease(session, MutationKind.Create))
        {
            Assert.Equal(LibraryState.Available, session.CreateLibrary(create).State);
        }

        // a capture: Prepare (T0) → window (reads only) → Save (import, outcome) → Idle
        var captureId = session.NewCaptureId();
        var prepare = World.Lease(session, MutationKind.Prepare, captureId);
        var attempt = session.RecordAttemptStartAsync(prepare, new AttemptStart(new byte[16], null, Utf16.ToBytes(@"D:\Media"), null, "audit", 1)).GetAwaiter().GetResult();
        var observation = prepare.HandOffToObservation();
        for (var i = 0; i < 5; i++) session.Read(r => r.Long("SELECT count(*) FROM scan_attempt"));   // judged like every other event: no margin is ignored
        var save = observation.HandOffToSave(out _);
        var snapshot = new SyntheticSnapshot(400);
        session.ImportSnapshotAsync(save, attempt, SyntheticSnapshot.NewSource(), snapshot.Header("audit"), snapshot).GetAwaiter().GetResult();
        save.Dispose();

        // a rolled-back import: no event may leave the owned set
        var failing = new SyntheticSnapshot(100, mutation: SyntheticSnapshot.Mutation.WrongSealedFileCount);
        Assert.Throws<ImportException>(() => LibraryStateTests.ImportOne(session, failing, "audit-2", new ImportSourceSpec.Existing(1)));

        using (var delete = World.Lease(session, MutationKind.Delete))
        {
            session.DeleteSnapshotAsync(delete, 1).GetAwaiter().GetResult();
        }

        using (var aside = World.Lease(session, MutationKind.SetAside))
        {
            Assert.True(session.SetAsideAsync(aside).GetAwaiter().GetResult().Quarantine!.Complete);
        }

        using (var create = World.Lease(session, MutationKind.Create))
        {
            Assert.Equal(LibraryState.Available, session.CreateLibrary(create).State, "a new Library beside the set-aside files");
        }

        var events = audit.Events();
        Console.WriteLine("A-26 events: " + string.Join("; ", events));
        Assert.True(events.Any(e => e == "Created library.lock"), "the watcher works: the lock file's creation was seen");
        Assert.True(events.Any(e => e == "Created library.sqlite3"), "main file creation by LibraryStore");
        Assert.True(events.Any(e => e == "Created library.sqlite3-journal"), "the journal is created natively by SQLite at T-CREATE");
        Assert.True(events.Any(e => e.StartsWith("Renamed library.sqlite3 -> library.damaged-", StringComparison.Ordinal)), "the set-aside rename");
        Assert.True(audit.Transitions.Any(t => t.State.Kind == InterlockStateKind.Observing), "the audit saw the observation window");
        var problems = audit.Violations();
        Assert.Equal(0, problems.Count, string.Join(Environment.NewLine, problems));
        session.TestOnlyShutdown();
    }

    [Test]
    public static void The_journal_is_zero_bytes_at_rest_after_commits_and_rollbacks_and_a_zero_byte_journal_is_inactive()
    {
        var world = World.Create();
        var session = world.CreatedSession();
        Assert.True(File.Exists(world.Journal), "T-CREATE created the journal natively");
        Assert.Equal(0L, World.Length(world.Journal), "0 bytes at rest after T-CREATE's commit");
        LibraryStateTests.ImportOne(session, new SyntheticSnapshot(300));
        Assert.Equal(0L, World.Length(world.Journal), "0 bytes after a commit");
        Assert.Throws<ImportException>(() => LibraryStateTests.ImportOne(session, new SyntheticSnapshot(100, mutation: SyntheticSnapshot.Mutation.WrongFileSize), "bad", new ImportSourceSpec.Existing(1)));
        Assert.Equal(0L, World.Length(world.Journal), "0 bytes after a rollback");
        session.TestOnlyShutdown();

        // an open that finds a 0-byte journal beside a valid Library derives Available and never treats it as an interrupted transaction
        var reopened = world.NewSession();
        var status = reopened.RunStartupOpen();
        Assert.Equal(LibraryState.Available, status.State, status.Message);
        Assert.False(status.RecoveryPending);
        Assert.Equal(0L, World.Length(world.Journal));
        reopened.TestOnlyShutdown();
    }

    [Test]
    public static void A_zero_byte_journal_beside_a_zero_byte_main_file_is_not_deleted_and_creation_proceeds_and_reuses_it()
    {
        var world = World.Create();
        Directory.CreateDirectory(world.LibraryDirectory);
        File.WriteAllBytes(world.Main, []);
        File.WriteAllBytes(world.Journal, []);
        var session = world.NewSession();
        using var watcher = new SideFileWatcher(world.LibraryDirectory);
        Assert.Equal(LibraryState.NotCreated, session.RunStartupOpen().State, "an empty journal is an owned, inactive side file: not Leftover files");
        using (var prepare = World.Lease(session, MutationKind.Prepare, 1))
        {
            var status = session.PrepareForSave(prepare);
            Assert.Equal(LibraryState.Available, status.State, status.Message);
        }
        var events = watcher.Settle();
        Console.WriteLine("L8 0-byte journal beside a 0-byte main file: events " + string.Join("; ", events));
        Assert.False(events.Contains("Deleted " + LibraryNames.JournalFile), "SQLite never deletes a 0-byte journal (the VFS reports it as absent to hasHotJournal)");
        Assert.True(File.Exists(world.Journal), "still there");
        Assert.Equal(0L, World.Length(world.Journal), "and still 0 bytes after T-CREATE");
        session.TestOnlyShutdown();
        var reopened = world.NewSession();
        Assert.Equal(LibraryState.Available, reopened.RunStartupOpen().State);
        reopened.TestOnlyShutdown();
    }

    [Test]
    public static void The_side_file_audit_catches_a_planted_violation_in_every_state_with_no_margin()
    {
        // negative self-test (A-21) of the A-26 helper, against the real watcher: an event nobody may cause, and an event whose NAME is
        // allowed but whose STATE is not
        foreach (var (name, plant, expected) in new (string, Action<World>, string)[]
        {
            ("a -wal file while Idle", w => File.WriteAllBytes(w.Member("library.sqlite3-wal"), [1]), "library.sqlite3-wal"),
            ("a journal file created while Idle (an allowed name in a state that allows nothing)", w => File.WriteAllBytes(w.Journal, []), "library.sqlite3-journal"),
        })
        {
            var world = World.Create();
            Directory.CreateDirectory(world.LibraryDirectory);
            var session = world.NewSession();
            using var audit = new SideFileAudit(world, session);
            session.RunStartupOpen();
            plant(world);
            var problems = audit.Violations();
            Assert.True(problems.Any(p => p.Contains(expected, StringComparison.Ordinal)), $"{name}: caught: {string.Join("; ", problems)}");
            session.TestOnlyShutdown();
        }

        // and during an Observing window, immediately: the five reads of a window are not in an ignored margin
        {
            var world = World.Create();
            var session = world.CreatedSession();
            using var audit = new SideFileAudit(world, session);
            var captureId = session.NewCaptureId();
            var prepare = World.Lease(session, MutationKind.Prepare, captureId);
            var observation = prepare.HandOffToObservation();
            File.WriteAllBytes(world.Member("stray.tmp"), [1]);   // inside the window, at once
            observation.Dispose();
            var problems = audit.Violations();
            Assert.True(problems.Any(p => p.Contains("stray.tmp", StringComparison.Ordinal) && p.Contains("Observing", StringComparison.Ordinal)), "caught in the Observing segment: " + string.Join("; ", problems));
            session.TestOnlyShutdown();
        }
    }

    [Test]
    public static void The_side_file_audit_judges_events_by_their_lease_kind()
    {
        // the rule as a pure function: violations the product cannot be made to commit are fed to it directly
        InterlockSnapshot State(InterlockStateKind kind, MutationKind? mutation = null) => new(kind, mutation, 0, 1, "", 1);
        List<string> Judge(MutationKind kind, params string[] events) =>
            SideFileAudit.Evaluate(events, [new SideFileAudit.Transition(0, State(InterlockStateKind.Mutating, kind), [])], [], []);

        Assert.Equal(0, Judge(MutationKind.Open, "Deleted library.sqlite3-journal").Count, "a hot journal rolled back while opening");
        Assert.Equal(0, Judge(MutationKind.Prepare, "Deleted library.sqlite3-journal", "Created library.sqlite3-journal").Count, "LIB-07 step 4 opens an existing Library under the Prepare lease");
        foreach (var kind in new[] { MutationKind.Save, MutationKind.Delete, MutationKind.SetAside })
        {
            Assert.Equal(1, Judge(kind, "Deleted library.sqlite3-journal").Count, $"a journal deleted inside a {kind} lease is a violation: SQLite deletes it only while opening a Library");
        }
        foreach (var kind in new[] { MutationKind.Open, MutationKind.Create, MutationKind.Prepare, MutationKind.Save, MutationKind.Delete })
        {
            Assert.Equal(1, Judge(kind, "Renamed library.sqlite3 -> library.damaged-20260101_000000-abcdef.sqlite3").Count, $"a rename inside a {kind} lease is a violation: only SetAside renames");
        }
        Assert.Equal(0, Judge(MutationKind.SetAside, "Renamed library.sqlite3 -> library.damaged-20260101_000000-abcdef.sqlite3", "Renamed library.sqlite3-journal -> library.damaged-20260101_000000-abcdef.sqlite3-journal",
            "Renamed library.sqlite3-wal -> library.damaged-20260101_000000-abcdef.sqlite3-wal", "Renamed library.sqlite3-shm -> library.damaged-20260101_000000-abcdef.sqlite3-shm").Count, "set-aside renames all four members, -wal and -shm included");
        Assert.Equal(1, Judge(MutationKind.SetAside, "Renamed library.sqlite3 -> somewhere-else.sqlite3").Count, "a rename to anything but library.damaged-*");
        Assert.Equal(1, Judge(MutationKind.Save, "Created library.sqlite3").Count, "the main file is created only by creation");
        Assert.Equal(1, Judge(MutationKind.Open, "Created Library").Count, "an open creates no directory");
        Assert.Equal(1, Judge(MutationKind.SetAside, "Created library.sqlite3-journal").Count, "a set-aside opens no database");
        Assert.Equal(1, Judge(MutationKind.Delete, "Deleted library.lock").Count, "the lock file is never deleted");
        Assert.True(SideFileAudit.Evaluate([], [new SideFileAudit.Transition(0, State(InterlockStateKind.Idle), [])], ["the watcher lost events: buffer overflow"], []).Count == 1, "a watcher error is a violation: an audit that may have missed events proves nothing");
        Assert.Equal(1, SideFileAudit.Evaluate(["Created library.lock"], [new SideFileAudit.Transition(0, State(InterlockStateKind.Faulted), [])], [], []).Count, "no event while Faulted");
    }

    [Test]
    public static void A_set_aside_of_a_Library_with_wal_and_shm_files_renames_all_four_members_and_deletes_nothing()
    {
        var world = World.Create();
        world.CreatedSession().TestOnlyShutdown();
        File.WriteAllBytes(world.Member(LibraryNames.WalFile), [1, 2, 3]);   // another program opened the file in WAL mode
        File.WriteAllBytes(world.Member(LibraryNames.ShmFile), [4, 5, 6]);
        var session = world.NewSession();
        using var audit = new SideFileAudit(world, session);
        var status = session.RunStartupOpen();
        Assert.Equal(LibraryState.NotALibrary, status.State, status.Message);
        Assert.Equal(LibraryReason.WalOrShmPresent, status.Reason);
        QuarantineResult? quarantine;
        using (var aside = World.Lease(session, MutationKind.SetAside)) quarantine = session.SetAsideAsync(aside).GetAwaiter().GetResult().Quarantine;
        Assert.True(quarantine!.Complete, quarantine.Failure ?? "complete");
        Assert.Equal(4, quarantine.Moved.Count, "main, journal, -wal and -shm");
        Assert.SequenceEqual(new[] { LibraryNames.MainFile, LibraryNames.JournalFile, LibraryNames.WalFile, LibraryNames.ShmFile }, quarantine.Moved, "the main file first, then the journal, wal and shm");
        var names = world.Names();
        Assert.Equal(5, names.Count, "the lock file and the four set-aside members: " + string.Join(", ", names));
        Assert.Equal(3L, World.Length(world.Member(quarantine.Stem + LibraryNames.QuarantineWalSuffix)), "the -wal bytes are intact");
        var events = audit.Events();
        Assert.Equal(4, events.Count(e => e.StartsWith("Renamed ", StringComparison.Ordinal)), "four renames: " + string.Join("; ", events));
        Assert.False(events.Any(e => e.StartsWith("Deleted ", StringComparison.Ordinal)), "nothing was deleted");
        var problems = audit.Violations();
        Assert.Equal(0, problems.Count, string.Join(Environment.NewLine, problems));
        session.TestOnlyShutdown();
    }

    [Test]
    public static void The_audit_of_a_recovery_after_a_crash_shows_the_one_deletion_SQLite_may_make_inside_the_Open_lease()
    {
        var world = World.Create();
        Directory.CreateDirectory(world.AppData);
        var ready = Path.Combine(world.Root, "ready");
        using (var child = Child.Start(world, "import-hang", world.LibraryDirectory, world.AppData, ready, "300000", "400000"))
        {
            child.WaitFor(ready, 120_000);
            child.Kill();
        }
        var session = world.NewSession();
        using var audit = new SideFileAudit(world, session);
        Assert.Equal(LibraryState.Available, session.RunStartupOpen().State);
        var events = audit.Events();
        Assert.True(events.Any(e => e == "Deleted " + LibraryNames.JournalFile), "the hot journal was deleted by SQLite during the Open lease");
        Assert.Equal(MutationKind.Open, audit.Transitions[0].State.Mutation, "and the start-up lease was an Open lease");
        var problems = audit.Violations();
        Assert.Equal(0, problems.Count, string.Join(Environment.NewLine, problems));
        session.TestOnlyShutdown();
    }

    // ---- TEST-L7: recovery fails at open ----

    [Test]
    public static void A_recovery_that_cannot_write_leaves_the_Library_Available_with_saving_disabled_and_a_later_open_recovers()
    {
        var world = World.Create();
        var dead = world.CreatedSession();
        using (var prepare = World.Lease(dead, MutationKind.Prepare, 1))
        {
            dead.RecordAttemptStartAsync(prepare, new AttemptStart(new byte[16], null, Utf16.ToBytes("x"), null, "dead", 1)).GetAwaiter().GetResult();
        }
        dead.TestOnlyShutdown();

        // the database is read-only to the OS (a stand-in for a full disk: every write fails)
        File.SetAttributes(world.Main, File.GetAttributes(world.Main) | FileAttributes.ReadOnly);
        try
        {
            var session = world.NewSession();
            var status = session.RunStartupOpen();
            Console.WriteLine("L7: " + status.State + " " + status.Reason + " pending=" + status.RecoveryPending + " " + status.Message);
            Assert.Equal(LibraryState.Available, status.State, status.Message);
            Assert.True(status.RecoveryPending, "recovery pending: browsing works, saving is disabled");
            Assert.False(status.CanSave);
            Assert.True(status.CanRead);
            Assert.Equal(1L, session.Read(r => r.Long("SELECT count(*) FROM scan_attempt WHERE outcome = 1")), "the dead session's attempt could not be marked yet");
            using (var prepare = World.Lease(session, MutationKind.Prepare, 2))
            {
                Assert.Throws<LibraryUnavailableException>(() => session.RecordAttemptStartAsync(prepare, new AttemptStart(new byte[16], null, Utf16.ToBytes("y"), null, "new", 2)).GetAwaiter().GetResult());
            }

            File.SetAttributes(world.Main, File.GetAttributes(world.Main) & ~FileAttributes.ReadOnly);
            Assert.True(session.TryRetryOpen(out var retried, out _));
            Assert.Equal(LibraryState.Available, retried.State);
            Assert.False(retried.RecoveryPending, "a later open recovers");
            Assert.Equal(1L, session.Read(r => r.Long("SELECT count(*) FROM scan_attempt WHERE outcome = 8")));
            session.TestOnlyShutdown();
        }
        finally
        {
            File.SetAttributes(world.Main, File.GetAttributes(world.Main) & ~FileAttributes.ReadOnly);
        }
    }

    // ---- TEST-W2: pause inside every mutation kind ----

    private sealed class Pause
    {
        internal readonly ManualResetEventSlim Entered = new();
        internal readonly ManualResetEventSlim Release = new();
        internal volatile bool Armed;

        internal void Hook()
        {
            if (!Armed) return;
            Armed = false;
            Entered.Set();
            Release.Wait(60_000);
        }
    }

    /// <summary>While a mutation is paused inside its operation: an observation and every other mutation are refused with the
    /// operation's reason, nothing changes, and after it ends the observation is granted.</summary>
    private static void RunPaused(LibrarySession session, Pause pause, Action<LibrarySession> operation, MutationKind expected, string reasonContains, bool arm = true)
    {
        pause.Entered.Reset();
        pause.Release.Reset();
        if (arm) pause.Armed = true;
        var task = Task.Run(() => operation(session));
        Assert.True(pause.Entered.Wait(120_000), $"{expected}: the operation reached its pause point");
        var state = session.Interlock.Snapshot;
        Assert.Equal(InterlockStateKind.Mutating, state.Kind, expected + ": Mutating while it runs");
        Assert.Equal(expected, state.Mutation, "the kind of the lease");
        Assert.False(session.Interlock.TryBeginObservation(99, out _, out var why), $"{expected}: an observation request is refused while it runs");
        Assert.Contains(reasonContains, why);
        foreach (var other in new[] { MutationKind.Open, MutationKind.Create, MutationKind.Prepare, MutationKind.Delete, MutationKind.SetAside })
        {
            Assert.False(session.Interlock.TryBeginMutation(other, 98, out _, out _), $"{expected}: no second mutation ({other}) while it runs");
        }
        Assert.Equal(state, session.Interlock.Snapshot, "the refusals changed nothing");
        pause.Release.Set();
        task.GetAwaiter().GetResult();
        Assert.Equal(InterlockStateKind.Idle, session.Interlock.Snapshot.Kind, $"{expected}: Idle after it ends cleanly");
        Assert.True(session.Interlock.TryBeginObservation(99, out var window, out _), $"{expected}: an observation is granted only after it ends");
        window.Dispose();
    }

    [Test]
    public static void An_observation_is_refused_while_each_kind_of_mutation_runs_and_granted_only_after_it_ends()
    {
        // the start-up open, with T-RECOVER in progress
        {
            var world = World.Create("w2-open");
            world.CreatedSession().TestOnlyShutdown();
            var pause = new Pause();
            var session = world.NewSession(new LibrarySessionOptions { Faults = new LibraryFaultInjection { AfterBegin = pause.Hook } });
            RunPaused(session, pause, s => s.RunStartupOpen(), MutationKind.Open, "Opening");
            session.TestOnlyShutdown();
        }
        // Create (T-CREATE)
        {
            var world = World.Create("w2-create");
            var pause = new Pause();
            var session = world.NewSession(new LibrarySessionOptions { Faults = new LibraryFaultInjection { AfterBegin = pause.Hook } });
            session.RunStartupOpen();
            RunPaused(session, pause, s => { using var lease = World.Lease(s, MutationKind.Create); s.CreateLibrary(lease); }, MutationKind.Create, "Creating");
            session.TestOnlyShutdown();
        }
        // Prepare (T0), Save (T-IMPORT, T-OUTCOME) and Delete (T-DELETE)
        {
            var world = World.Create("w2-capture");
            var pause = new Pause();
            var faults = new LibraryFaultInjection { AfterBegin = pause.Hook };
            var session = world.CreatedSession(new LibrarySessionOptions { Faults = faults });
            AttemptRef? attempt = null;
            MutationLease prepare = default;
            pause.Armed = true;
            var t0 = Task.Run(() =>
            {
                prepare = World.Lease(session, MutationKind.Prepare, 5);
                attempt = session.RecordAttemptStartAsync(prepare, new AttemptStart(new byte[16], null, Utf16.ToBytes("p"), null, "w2", 1)).GetAwaiter().GetResult();
            });
            Assert.True(pause.Entered.Wait(60_000));
            Assert.Equal(MutationKind.Prepare, session.Interlock.Snapshot.Mutation);
            Assert.False(session.Interlock.TryBeginObservation(1, out _, out var prepareReason));
            Assert.Contains("Preparing", prepareReason);
            pause.Release.Set();
            t0.GetAwaiter().GetResult();
            pause.Release.Reset();

            var save = prepare.HandOffToObservation().HandOffToSave(out _);
            var snapshot = new SyntheticSnapshot(200);
            var importPaused = new ManualResetEventSlim();
            var importRelease = new ManualResetEventSlim();
            var options = new ImportOptions { AfterRows = () => { importPaused.Set(); importRelease.Wait(60_000); } };
            var import = Task.Run(() => session.ImportSnapshotAsync(save, attempt!, SyntheticSnapshot.NewSource(), snapshot.Header("w2"), snapshot, options).GetAwaiter().GetResult());
            Assert.True(importPaused.Wait(60_000));
            Assert.Equal(MutationKind.Save, session.Interlock.Snapshot.Mutation, "T-IMPORT runs under the Save lease");
            Assert.False(session.Interlock.TryBeginObservation(1, out _, out var saveReason), "no window while a snapshot is being imported");
            Assert.Contains("Saving", saveReason);
            importRelease.Set();
            import.GetAwaiter().GetResult();
            save.Dispose();
            Assert.Equal(InterlockStateKind.Idle, session.Interlock.Snapshot.Kind);

            // T-OUTCOME: a second capture ending in NotEligible
            RunPaused(session, pause, s =>
            {
                var p = World.Lease(s, MutationKind.Prepare, 6);
                var a = s.RecordAttemptStartAsync(p, new AttemptStart(new byte[16], null, Utf16.ToBytes("q"), null, "w2b", 2)).GetAwaiter().GetResult();
                var sv = p.HandOffToObservation().HandOffToSave(out _);
                pause.Armed = true;
                s.RecordAttemptOutcomeAsync(sv, a, AttemptOutcome.NotEligible, CaptureFailureKind.SpoolInvalid, "x").GetAwaiter().GetResult();
                sv.Dispose();
            }, MutationKind.Save, "Saving", arm: false);

            // Delete
            RunPaused(session, pause, s => { using var lease = World.Lease(s, MutationKind.Delete); s.DeleteSnapshotAsync(lease, 1).GetAwaiter().GetResult(); }, MutationKind.Delete, "Deleting");
            session.TestOnlyShutdown();
        }
        // SetAside (paused after the first rename)
        {
            var world = World.Create("w2-aside");
            var pause = new Pause();
            var session = world.CreatedSession(new LibrarySessionOptions { StoreHooks = new LibraryStoreHooks { AfterRename = _ => pause.Hook() } });
            RunPaused(session, pause, s => { using var lease = World.Lease(s, MutationKind.SetAside); s.SetAsideAsync(lease).GetAwaiter().GetResult(); }, MutationKind.SetAside, "Setting aside");
            session.TestOnlyShutdown();
        }
    }

    [Test]
    public static void An_open_that_rolls_back_a_hot_journal_is_a_mutation_and_an_observation_is_refused_while_it_runs()
    {
        var world = World.Create("w2-hot");
        var ready = Path.Combine(world.Root, "ready");
        using (var child = Child.Start(world, "import-hang", world.LibraryDirectory, world.AppData, ready, "300000", "400000"))
        {
            child.WaitFor(ready, 120_000);
            child.Kill();
        }
        var pause = new Pause();
        var session = world.NewSession(new LibrarySessionOptions { Faults = new LibraryFaultInjection { AfterBegin = pause.Hook } });
        RunPaused(session, pause, s => s.RunStartupOpen(), MutationKind.Open, "Opening");
        Assert.Equal(LibraryState.Available, session.Status.State);
        session.TestOnlyShutdown();
    }
}

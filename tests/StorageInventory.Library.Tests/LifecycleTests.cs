using System.Text.RegularExpressions;
using StorageInventory.Testing;

namespace StorageInventory.Library.Tests;

/// <summary>A-26 as a helper: a directory watcher and a names-only listing taken at every interlock transition (through the
/// interlock's test hook), so that every create, delete and rename in the Library directory is attributed to the interlock state
/// in which it happened. Names in a directory index change at once; only sizes and times can lag, so listings use names only.</summary>
internal sealed class SideFileAudit : IDisposable
{
    internal sealed record Transition(long Tick, InterlockSnapshot State, List<string> Names);

    private readonly World _world;
    private readonly FileSystemWatcher _watcher;
    private readonly List<(long Tick, string Event)> _events = [];
    private readonly object _lock = new();

    internal List<Transition> Transitions { get; } = [];

    internal SideFileAudit(World world, LibrarySession session)
    {
        _world = world;
        _watcher = new FileSystemWatcher(world.AppData) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName, InternalBufferSize = 64 * 1024 };
        void Record(string verb, string? name) { lock (_lock) _events.Add((Environment.TickCount64, verb + " " + Normalise(name))); }
        _watcher.Created += (_, e) => Record("Created", e.Name);
        _watcher.Deleted += (_, e) => Record("Deleted", e.Name);
        _watcher.Renamed += (_, e) => Record("Renamed", Normalise(e.OldName) + " -> " + Normalise(e.Name));
        _watcher.EnableRaisingEvents = true;
        session.Interlock.TransitionHook = state =>
        {
            var tick = Environment.TickCount64;
            lock (_lock) Transitions.Add(new Transition(tick, state, world.Names()));
        };
        // the construction state itself (OBS-14) is the first "transition"
        lock (_lock) Transitions.Add(new Transition(Environment.TickCount64, session.Interlock.Snapshot, world.Names()));
    }

    private static string Normalise(string? name) => (name ?? "").Replace("Library\\", "", StringComparison.Ordinal);

    /// <summary>Waits for the watcher to deliver what it has, then returns the events.</summary>
    internal List<(long Tick, string Event)> Events(int settleMilliseconds = 400)
    {
        Thread.Sleep(settleMilliseconds);
        lock (_lock) return [.. _events];
    }

    private static readonly Regex Allowed = new(
        @"^(Created (Library|library\.lock|library\.sqlite3|library\.sqlite3-journal)|Deleted library\.sqlite3-journal|Renamed library\.(sqlite3|sqlite3-journal) -> library\.damaged-\d{8}_\d{6}-[0-9a-f]{6}\.sqlite3(-journal)?)$",
        RegexOptions.CultureInvariant);

    /// <summary>The A-26 rule: every event is one the product permits, and none happens while the interlock is Idle, Observing or
    /// Faulted (judged both by the names-only listings at the transitions and by the watcher, with a margin for event delivery).</summary>
    internal List<string> Violations(int idleMarginMilliseconds = 250)
    {
        var events = Events();
        List<Transition> transitions;
        lock (_lock) transitions = [.. Transitions];
        var problems = new List<string>();
        foreach (var (_, text) in events)
        {
            if (!Allowed.IsMatch(text)) problems.Add("an event the product never causes: " + text);
        }
        for (var i = 0; i < transitions.Count; i++)
        {
            var state = transitions[i].State;
            var start = transitions[i].Tick;
            var end = i + 1 < transitions.Count ? transitions[i + 1].Tick : long.MaxValue;
            if (state.Kind is InterlockStateKind.Mutating) continue;
            // names-only listing: nothing may appear or disappear between this transition and the next
            if (i + 1 < transitions.Count && !transitions[i].Names.SequenceEqual(transitions[i + 1].Names))
            {
                problems.Add($"transition {i} ({state.Kind}): the names changed from [{string.Join(",", transitions[i].Names)}] to [{string.Join(",", transitions[i + 1].Names)}]");
            }
            foreach (var (tick, text) in events)
            {
                if (tick >= start + idleMarginMilliseconds && tick < end) problems.Add($"transition {i} ({state.Kind}): event during the window: {text}");
            }
        }
        // A-23: the Library directory holds only the owned members
        foreach (var name in _world.Names())
        {
            if (!(name is LibraryNames.LockFile or LibraryNames.MainFile or LibraryNames.JournalFile || name.StartsWith(LibraryNames.QuarantinePrefix, StringComparison.Ordinal)))
            {
                problems.Add("a file outside the owned set: " + name);
            }
        }
        return problems;
    }

    public void Dispose() => _watcher.Dispose();
}

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
        Thread.Sleep(300);   // Idle

        using (var create = World.Lease(session, MutationKind.Create))
        {
            Assert.Equal(LibraryState.Available, session.CreateLibrary(create).State);
        }
        Thread.Sleep(300);   // Idle

        // a capture: Prepare (T0) → window (reads only) → Save (import, outcome) → Idle
        var captureId = session.NewCaptureId();
        var prepare = World.Lease(session, MutationKind.Prepare, captureId);
        var attempt = session.RecordAttemptStartAsync(prepare, new AttemptStart(new byte[16], null, Utf16.ToBytes(@"D:\Media"), null, "audit", 1)).GetAwaiter().GetResult();
        var observation = prepare.HandOffToObservation();
        for (var i = 0; i < 5; i++) session.Read(r => r.Long("SELECT count(*) FROM scan_attempt"));
        Thread.Sleep(400);   // Observing
        var save = observation.HandOffToSave(out _);
        var snapshot = new SyntheticSnapshot(400);
        session.ImportSnapshotAsync(save, attempt, SyntheticSnapshot.NewSource(), snapshot.Header("audit"), snapshot).GetAwaiter().GetResult();
        save.Dispose();
        Thread.Sleep(300);   // Idle

        // a rolled-back import: no event may leave the owned set
        var failing = new SyntheticSnapshot(100, mutation: SyntheticSnapshot.Mutation.WrongSealedFileCount);
        Assert.Throws<ImportException>(() => LibraryStateTests.ImportOne(session, failing, "audit-2", new ImportSourceSpec.Existing(1)));
        Thread.Sleep(300);

        using (var delete = World.Lease(session, MutationKind.Delete))
        {
            session.DeleteSnapshotAsync(delete, 1).GetAwaiter().GetResult();
        }
        Thread.Sleep(300);

        using (var aside = World.Lease(session, MutationKind.SetAside))
        {
            Assert.True(session.SetAsideAsync(aside).GetAwaiter().GetResult().Quarantine!.Complete);
        }
        Thread.Sleep(300);

        using (var create = World.Lease(session, MutationKind.Create))
        {
            Assert.Equal(LibraryState.Available, session.CreateLibrary(create).State, "a new Library beside the set-aside files");
        }
        Thread.Sleep(300);

        var events = audit.Events();
        Console.WriteLine("A-26 events: " + string.Join("; ", events.Select(e => e.Event)));
        Assert.True(events.Any(e => e.Event == "Created library.lock"), "the watcher works: the lock file's creation was seen");
        Assert.True(events.Any(e => e.Event == "Created library.sqlite3"), "main file creation by LibraryStore");
        Assert.True(events.Any(e => e.Event == "Created library.sqlite3-journal"), "the journal is created natively by SQLite at T-CREATE");
        Assert.True(events.Any(e => e.Event.StartsWith("Renamed library.sqlite3 -> library.damaged-", StringComparison.Ordinal)), "the set-aside rename");
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
    public static void The_side_file_audit_catches_a_planted_violation()
    {
        // negative self-test (A-21) of the A-26 helper: a file created while the interlock is Idle is reported
        var world = World.Create();
        Directory.CreateDirectory(world.LibraryDirectory);
        var session = world.NewSession();
        using var audit = new SideFileAudit(world, session);
        session.RunStartupOpen();
        Thread.Sleep(300);
        File.WriteAllBytes(world.Member("library.sqlite3-wal"), [1]);   // an event nobody may cause
        Thread.Sleep(300);
        var problems = audit.Violations();
        Assert.True(problems.Any(p => p.Contains("library.sqlite3-wal", StringComparison.Ordinal)), "the planted -wal file was caught: " + string.Join("; ", problems));
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
        Thread.Sleep(300);
        var events = audit.Events();
        Assert.True(events.Any(e => e.Event == "Deleted " + LibraryNames.JournalFile), "the hot journal was deleted by SQLite during the Open lease");
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

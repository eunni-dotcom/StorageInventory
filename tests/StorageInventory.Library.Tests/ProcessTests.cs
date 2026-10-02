using StorageInventory.Testing;

namespace StorageInventory.Library.Tests;

/// <summary>
/// Real-process tests: TEST-K1 (the writer lock across processes, the race to create a Library, and the sequential case of
/// G0F-M01), TEST-L6 (set-aside, including a crash between renames and a second process that cannot set aside) and the crash half
/// of TEST-L8 (a killed import leaves a real hot journal). Several objects in one process would prove none of this.
/// </summary>
public static class ProcessTests
{
    private static string[] Args(World world, params string[] more) => [world.LibraryDirectory, world.AppData, .. more];

    private static string Flag(World world, string name) => Path.Combine(world.Root, name);

    // ---------------------------------------------------------------- TEST-K1

    [Test]
    public static void A_second_process_gets_In_use_opens_nothing_and_changes_nothing()
    {
        var world = World.Create();
        using var owner = Child.Start(world, "hold", Args(world, Flag(world, "ready"), Flag(world, "stop"), "create", "2"));
        Assert.Equal("Available||", owner.WaitFor(Flag(world, "ready")));
        var before = world.ContentHashes();
        var names = world.Names();

        // this test process is "process B"
        var b = world.NewSession();
        var status = b.RunStartupOpen();
        Assert.Equal(LibraryState.InUse, status.State, "B: " + status.Message);
        Assert.SequenceEqual(names, world.Names(), "B created, renamed and deleted nothing");
        Assert.SequenceEqual(before.OrderBy(p => p.Key).Select(p => p.Key + p.Value), world.ContentHashes().OrderBy(p => p.Key).Select(p => p.Key + p.Value), "B changed nothing");
        Assert.Throws<LibraryUnavailableException>(() => b.Read(r => r.Long("SELECT 1")));   // B opens no connection

        // and a third process agrees
        using var c = Child.Start(world, "try-open", Args(world, Flag(world, "c-out")));
        Assert.Equal("InUse|locked-by-another-process|", c.WaitFor(Flag(world, "c-out")));
        b.TestOnlyShutdown();

        File.WriteAllText(Flag(world, "stop"), "");
        Assert.True(owner.WaitForExit(), "the owner exits when told to");
        // once the owner has exited, the lock is free and the Library opens, with both snapshots
        var after = world.OpenedSession();
        Assert.Equal(LibraryState.Available, after.Status.State);
        Assert.Equal(2L, after.Read(r => r.Long("SELECT count(*) FROM snapshot WHERE state = 2")));
        after.TestOnlyShutdown();
    }

    [Test]
    public static void Two_processes_racing_to_create_the_Library_produce_exactly_one_Library_and_one_In_use_never_Leftover_files()
    {
        for (var round = 0; round < 6; round++)
        {
            var world = World.Create("race" + round);
            Directory.CreateDirectory(world.AppData);
            var barrier = Flag(world, "go");
            var stop = Flag(world, "stop");
            using var one = Child.Start(world, "race", Args(world, barrier, Flag(world, "one"), stop));
            using var two = Child.Start(world, "race", Args(world, barrier, Flag(world, "two"), stop));
            // both have started, opened (Not created, no lock) and wait at the barrier: give them time to reach it
            Thread.Sleep(1500);
            File.WriteAllText(barrier, "");
            var outcomes = new[] { one.WaitFor(Flag(world, "one")), two.WaitFor(Flag(world, "two")) };
            File.WriteAllText(stop, "");
            Assert.True(one.WaitForExit() && two.WaitForExit());

            foreach (var outcome in outcomes) Assert.True(outcome.StartsWith("NotCreated|", StringComparison.Ordinal), $"round {round}: each started with Not created ({outcome})");
            var results = outcomes.Select(o => o.Split("=>")[1]).ToList();
            Assert.Equal(1, results.Count(r => r.StartsWith("Available|", StringComparison.Ordinal)), $"round {round}: exactly one process created the Library ({string.Join(" / ", outcomes)})");
            Assert.Equal(1, results.Count(r => r.StartsWith("InUse|", StringComparison.Ordinal)), $"round {round}: exactly one process was told In use");
            Assert.False(results.Any(r => r.StartsWith("LeftoverFiles", StringComparison.Ordinal)), $"round {round}: never Leftover files");

            var check = world.OpenedSession();
            Assert.Equal(LibraryState.Available, check.Status.State, $"round {round}: one valid Library exists");
            check.TestOnlyShutdown();
            Assert.True(world.Names().All(n => n is LibraryNames.LockFile or LibraryNames.MainFile or LibraryNames.JournalFile), $"round {round}: only owned members: {string.Join(", ", world.Names())}");
        }
    }

    [Test]
    public static void The_sequential_case_of_a_Library_created_by_another_process_after_this_one_started_is_opened_never_set_aside()
    {
        // run 1: A creates a Library and a snapshot and exits cleanly; B opens it at its next save, never creating a second database
        {
            var world = World.Create("seq1");
            var b = world.OpenedSession();   // B starts first: Not created, no directory, no lock
            Assert.Equal(LibraryState.NotCreated, b.Status.State);
            Assert.False(Directory.Exists(world.LibraryDirectory));
            using (var a = Child.Start(world, "hold", Args(world, Flag(world, "ready"), Flag(world, "stop"), "create", "1")))
            {
                Assert.Equal("Available||", a.WaitFor(Flag(world, "ready")));
                File.WriteAllText(Flag(world, "stop"), "");
                Assert.True(a.WaitForExit());
            }
            using var prepare = World.Lease(b, MutationKind.Prepare, 1);
            var status = b.PrepareForSave(prepare);
            Assert.Equal(LibraryState.Available, status.State, "run 1: " + status.Message);
            Assert.Equal(1L, b.Read(r => r.Long("SELECT count(*) FROM snapshot")), "A's snapshot is there: one database, not a second one");
            Assert.Equal(1L, b.Read(r => r.Long("SELECT count(*) FROM library_info")));
            b.TestOnlyShutdown();
        }

        // run 2: A is killed mid-import, leaving a hot journal; B rolls it back at its next save and saves into the same Library
        {
            var world = World.Create("seq2");
            var b = world.OpenedSession();
            using (var a = Child.Start(world, "import-hang", Args(world, Flag(world, "ready"), "300000", "400000")))
            {
                a.WaitFor(Flag(world, "ready"), 120_000);
                Assert.True(World.Length(world.Journal) > 0, "A was killed with a non-empty (hot) journal");
                a.Kill();
            }
            var hotJournal = World.Length(world.Journal);
            Assert.True(hotJournal > 0, "the journal survived the kill");
            using var prepare = World.Lease(b, MutationKind.Prepare, 1);
            var status = b.PrepareForSave(prepare);
            Assert.Equal(LibraryState.Available, status.State, "run 2: B opens A's Library (rolling the hot journal back), never Leftover files: " + status.Message);
            Assert.Equal(1L, b.Read(r => r.Long("SELECT count(*) FROM snapshot")), "only A's first snapshot is committed");
            Assert.Equal(1L, b.Read(r => r.Long("SELECT count(*) FROM scan_attempt WHERE outcome = 8")), "A's interrupted attempt was recovered");
            prepare.Dispose();
            LibraryStateTests.ImportOne(b, new SyntheticSnapshot(50, seed: 9, label: "b"), "b-run", new ImportSourceSpec.Existing(1));
            Assert.Equal(2L, b.Read(r => r.Long("SELECT count(*) FROM snapshot WHERE state = 2")), "B saved its snapshot into A's Library");
            b.TestOnlyShutdown();
        }

        // run 3: A exits and the user deletes library.sqlite3: B reports Missing and never recreates silently
        {
            var world = World.Create("seq3");
            var b = world.OpenedSession();
            using (var a = Child.Start(world, "hold", Args(world, Flag(world, "ready"), Flag(world, "stop"), "create", "1")))
            {
                a.WaitFor(Flag(world, "ready"));
                File.WriteAllText(Flag(world, "stop"), "");
                Assert.True(a.WaitForExit());
            }
            File.Delete(world.Main);
            if (File.Exists(world.Journal)) File.Delete(world.Journal);
            using var prepare = World.Lease(b, MutationKind.Prepare, 1);
            var status = b.PrepareForSave(prepare);
            Assert.Equal(LibraryState.Missing, status.State, "run 3: " + status.Message);
            Assert.False(File.Exists(world.Main), "nothing was recreated silently");
            b.TestOnlyShutdown();
        }

        // run 4: A is still running: B reports In use and scans without saving
        {
            var world = World.Create("seq4");
            var b = world.OpenedSession();
            using var a = Child.Start(world, "hold", Args(world, Flag(world, "ready"), Flag(world, "stop"), "create", "1"));
            Assert.Equal("Available||", a.WaitFor(Flag(world, "ready")));
            using var prepare = World.Lease(b, MutationKind.Prepare, 1);
            var status = b.PrepareForSave(prepare);
            Assert.Equal(LibraryState.InUse, status.State, "run 4: " + status.Message);
            b.TestOnlyShutdown();
            File.WriteAllText(Flag(world, "stop"), "");
            Assert.True(a.WaitForExit());
        }
    }

    // ---------------------------------------------------------------- TEST-L6

    [Test]
    public static void A_process_that_does_not_hold_the_lock_can_never_set_aside_and_the_lock_stays_held_through_set_aside_and_creation()
    {
        var world = World.Create();
        var owner = world.CreatedSession();
        LibraryStateTests.ImportOne(owner, new SyntheticSnapshot(30));
        var names = world.Names();
        var hashes = world.ContentHashes();

        // a second process tries: In use, nothing renamed
        using (var other = Child.Start(world, "setaside", Args(world, Flag(world, "other"), "-")))
        {
            var report = other.WaitFor(Flag(world, "other"));
            Assert.True(report.StartsWith("InUse|", StringComparison.Ordinal), "the second process: " + report);
            Assert.Contains("moved=none", report);
            Assert.True(other.WaitForExit());
        }
        Assert.SequenceEqual(names, world.Names(), "nothing was renamed by the process that does not hold the lock");

        // the owner sets the Library aside; the lock is held throughout; a new Library is created without releasing it
        using (var aside = World.Lease(owner, MutationKind.SetAside))
        {
            var result = owner.SetAsideAsync(aside).GetAwaiter().GetResult();
            Assert.True(result.Quarantine!.Complete, "set-aside moved every member: " + (result.Quarantine.Failure ?? "none"));
            Assert.Equal(LibraryState.Missing, result.Status.State);
            Assert.SequenceEqual(new[] { LibraryNames.MainFile, LibraryNames.JournalFile }.Where(n => names.Contains(n)), result.Quarantine.Moved, "main file first, then the journal");
        }
        Assert.False(File.Exists(world.Main), "the Library path is free");
        Assert.True(owner.Store.HoldsWriterLock, "the lock is still held");
        using (var probe = Child.Start(world, "try-open", Args(world, Flag(world, "probe1"))))
        {
            Assert.True(probe.WaitFor(Flag(world, "probe1")).StartsWith("InUse|", StringComparison.Ordinal), "no gap: another process still sees In use after set-aside");
            Assert.True(probe.WaitForExit());
        }
        using (var create = World.Lease(owner, MutationKind.Create))
        {
            Assert.Equal(LibraryState.Available, owner.CreateLibrary(create).State, "a new Library is created beside the set-aside files");
        }
        using (var probe = Child.Start(world, "try-open", Args(world, Flag(world, "probe2"))))
        {
            Assert.True(probe.WaitFor(Flag(world, "probe2")).StartsWith("InUse|", StringComparison.Ordinal), "and after creation");
            Assert.True(probe.WaitForExit());
        }

        // nothing was deleted: the set-aside files' contents are exactly the old members' contents
        var damaged = world.Names().Where(n => n.StartsWith(LibraryNames.QuarantinePrefix, StringComparison.Ordinal)).ToList();
        Assert.True(damaged.Count >= 1);
        var mainCopy = damaged.Single(n => n.EndsWith(LibraryNames.QuarantineMainSuffix, StringComparison.Ordinal));
        Assert.Equal(hashes[LibraryNames.MainFile], world.ContentHashes()[mainCopy], "the set-aside database is byte-identical to the old library.sqlite3");
        owner.TestOnlyShutdown();
    }

    [Test]
    public static void Set_aside_after_a_crash_with_a_hot_journal_moves_every_member_and_the_copy_opens_with_all_committed_rows()
    {
        var world = World.Create();
        using (var child = Child.Start(world, "import-hang", Args(world, Flag(world, "ready"), "300000", "400000")))
        {
            child.WaitFor(Flag(world, "ready"), 120_000);
            child.Kill();
        }
        Assert.True(World.Length(world.Journal) > 0, "a hot journal remains");
        var mainBefore = Bytes(world.Main);
        var journalBefore = Bytes(world.Journal);

        using (var aside = Child.Start(world, "setaside", Args(world, Flag(world, "aside"), "-")))
        {
            var report = aside.WaitFor(Flag(world, "aside"));
            Assert.Contains("moved=library.sqlite3,library.sqlite3-journal", report);
            Assert.True(aside.WaitForExit());
        }
        Assert.False(File.Exists(world.Main), "moved");
        Assert.False(File.Exists(world.Journal), "moved");
        var set = world.Names().Where(n => n.StartsWith(LibraryNames.QuarantinePrefix, StringComparison.Ordinal)).ToList();
        Assert.Equal(2, set.Count, string.Join(",", set));
        var movedMain = set.Single(n => n.EndsWith(".sqlite3", StringComparison.Ordinal));
        var movedJournal = set.Single(n => n.EndsWith(".sqlite3-journal", StringComparison.Ordinal));
        Assert.SequenceEqual(mainBefore, Bytes(world.Member(movedMain)), "the database moved byte for byte");
        Assert.SequenceEqual(journalBefore, Bytes(world.Member(movedJournal)), "and so did its hot journal: nothing was deleted");

        // a COPY of the set-aside set opens (rolling the journal back) with every committed row and none of the interrupted import
        var copy = World.Create("copy");
        Directory.CreateDirectory(copy.LibraryDirectory);
        File.Copy(world.Member(movedMain), copy.Main);
        File.Copy(world.Member(movedJournal), copy.Journal);
        var session = copy.NewSession();
        var status = session.RunStartupOpen();
        Assert.Equal(LibraryState.Available, status.State, status.Message);
        Assert.Equal(1L, session.Read(r => r.Long("SELECT count(*) FROM snapshot")), "the committed snapshot is there");
        Assert.Equal(0L, session.Read(r => r.Long("SELECT count(*) FROM snapshot WHERE state <> 2")));
        Assert.Null(session.Read(r => SnapshotVerifier.VerifyPublished(r, 1)), "and verifies against its sealed totals");
        Assert.Equal(1L, session.Read(r => r.Long("SELECT count(*) FROM scan_attempt WHERE outcome = 8")), "the interrupted attempt");
        session.TestOnlyShutdown();
    }

    [Test]
    public static void A_crash_between_renames_leaves_Leftover_files_and_the_action_finishes_the_job_without_deleting_anything()
    {
        var world = World.Create();
        using (var child = Child.Start(world, "import-hang", Args(world, Flag(world, "ready"), "300000", "400000")))
        {
            child.WaitFor(Flag(world, "ready"), 120_000);
            child.Kill();
        }
        var journalBefore = Bytes(world.Journal);

        // a set-aside that dies after the first rename: the main file is moved, the journal is not
        using (var crashing = Child.Start(world, "setaside", Args(world, Flag(world, "never"), "1")))
        {
            Assert.True(crashing.WaitForExit(), "the child killed itself");
        }
        Assert.False(File.Exists(world.Main), "the database was renamed first");
        Assert.True(File.Exists(world.Journal), "the journal was not");
        var first = world.Names().Where(n => n.StartsWith(LibraryNames.QuarantinePrefix, StringComparison.Ordinal)).ToList();
        Assert.Equal(1, first.Count);

        var session = world.NewSession();
        var open = session.RunStartupOpen();
        Assert.Equal(LibraryState.LeftoverFiles, open.State, "a crash between renames leaves Leftover files: " + open.Message);
        using (var create = World.Lease(session, MutationKind.Create))
        {
            Assert.Equal(LibraryState.LeftoverFiles, session.CreateLibrary(create).State, "creation is refused while a non-empty journal remains");
        }
        Assert.SequenceEqual(journalBefore, Bytes(world.Journal), "the journal was not touched by the refused creation");

        // the user's Set aside finishes the job under a NEW stem
        using (var aside = World.Lease(session, MutationKind.SetAside))
        {
            var result = session.SetAsideAsync(aside).GetAwaiter().GetResult();
            Assert.True(result.Quarantine!.Complete, result.Quarantine.Failure ?? "complete");
            Assert.SequenceEqual(new[] { LibraryNames.JournalFile }, result.Quarantine.Moved, "only the journal remained");
            Assert.True(result.Quarantine.Stem != Path.GetFileNameWithoutExtension(first[0]), "a different stem: nothing was overwritten");
            Assert.Equal(LibraryState.Missing, result.Status.State);
        }
        Assert.SequenceEqual(journalBefore, Bytes(world.Member(world.Names().Single(n => n.EndsWith(".sqlite3-journal", StringComparison.Ordinal)))), "no byte of the journal changed");
        using (var create = World.Lease(session, MutationKind.Create))
        {
            Assert.Equal(LibraryState.Available, session.CreateLibrary(create).State, "a new Library can now be created");
        }
        session.TestOnlyShutdown();
    }

    [Test]
    public static void Set_aside_with_open_reader_connections_closes_them_first_and_moves_every_member()
    {
        var world = World.Create();
        var session = world.CreatedSession();
        LibraryStateTests.ImportOne(session, new SyntheticSnapshot(30));
        var readerOpen = new ManualResetEventSlim();
        var readerSawCancellation = false;
        var read = Task.Run(() => session.ReadAsync(r =>
        {
            r.Long("SELECT count(*) FROM file_obs");
            readerOpen.Set();
            // a long read: it stays open until the gate cancels it because set-aside wants exclusive access
            readerSawCancellation = r.Token.WaitHandle.WaitOne(30_000);
            return 0;
        }));
        Assert.True(readerOpen.Wait(30_000), "the reader is open");

        using (var aside = World.Lease(session, MutationKind.SetAside))
        {
            var result = session.SetAsideAsync(aside).GetAwaiter().GetResult();
            Assert.True(result.Quarantine!.Complete, "no sharing violation from this process's own reader: " + (result.Quarantine.Failure ?? "none"));
        }
        Assert.True(readerSawCancellation, "the in-flight read was cancelled through the gate before the renames");
        read.GetAwaiter().GetResult();
        Assert.False(File.Exists(world.Main));
        session.TestOnlyShutdown();
    }

    // ---------------------------------------------------------------- TEST-L8 (crash half)

    [Test]
    public static void A_killed_import_leaves_a_hot_journal_that_the_next_open_rolls_back_and_deletes_and_the_next_write_recreates_at_zero_bytes()
    {
        var world = World.Create();
        using (var child = Child.Start(world, "import-hang", Args(world, Flag(world, "ready"), "300000", "400000")))
        {
            child.WaitFor(Flag(world, "ready"), 120_000);
            Assert.True(World.Length(world.Journal) > 0, "the journal is non-empty while the import runs");
            Assert.True(World.Length(world.Main) > 0);
            child.Kill();
        }
        Assert.True(World.Length(world.Journal) > 0, "hot after the kill");

        // a read-only connection facing the hot journal fails with SQLITE_READONLY_ROLLBACK and changes nothing
        var before = world.ContentHashes();
        using (var readOnly = RawSqlite.OpenReadOnly(world.Main))
        {
            using var command = readOnly.CreateCommand();
            command.CommandText = "SELECT count(*) FROM snapshot";
            var failure = Assert.Throws<Exception>(() => command.ExecuteScalar());
            Assert.Equal("SqliteException", failure.GetType().Name);
            Assert.Equal(776, (int)failure.GetType().GetProperty("SqliteExtendedErrorCode")!.GetValue(failure)!, "SQLITE_READONLY_ROLLBACK");
        }
        Assert.SequenceEqual(before.OrderBy(p => p.Key).Select(p => p.Key + p.Value), world.ContentHashes().OrderBy(p => p.Key).Select(p => p.Key + p.Value), "the read-only connection changed nothing");
        Assert.True(World.Length(world.Journal) > 0, "and did not delete the journal");

        // the next open (read-write, inside the Open lease) rolls it back and SQLite DELETES the journal; T-RECOVER's own write
        // transaction, which runs inside the same open, then creates a fresh one that TRUNCATE keeps at 0 bytes
        using var watcher = new SideFileWatcher(world.LibraryDirectory);
        var session = world.NewSession();
        var status = session.RunStartupOpen();
        Assert.Equal(LibraryState.Available, status.State, status.Message);
        var events = watcher.Settle();
        Console.WriteLine("L8 events during the open after the crash: " + string.Join("; ", events));
        Assert.True(events.Any(e => e == "Deleted " + LibraryNames.JournalFile), "SQLite deleted the hot journal at recovery (the only deletion the product permits): " + string.Join("; ", events));
        Assert.True(events.All(e => e.EndsWith(LibraryNames.JournalFile, StringComparison.Ordinal) || e.EndsWith(LibraryNames.LockFile, StringComparison.Ordinal) || e.EndsWith(LibraryNames.MainFile, StringComparison.Ordinal)), "only owned members were involved: " + string.Join("; ", events));
        Assert.False(events.Any(e => e.StartsWith("Deleted ", StringComparison.Ordinal) && !e.EndsWith(LibraryNames.JournalFile, StringComparison.Ordinal)), "nothing but the journal was deleted");
        Assert.Equal(0L, World.Length(world.Journal), "after the open the journal is the 0-byte side file of the T-RECOVER transaction");
        Assert.Equal(1L, session.Read(r => r.Long("SELECT count(*) FROM snapshot")), "only the committed snapshot");
        Assert.Null(session.Read(r => SnapshotVerifier.VerifyPublished(r, 1)));

        // a further write transaction keeps it at 0 bytes
        using (var prepare = World.Lease(session, MutationKind.Prepare, 1))
        {
            session.RecordAttemptStartAsync(prepare, new AttemptStart(new byte[16], null, Utf16.ToBytes("x"), null, "r", 1)).GetAwaiter().GetResult();
        }
        Assert.Equal(0L, World.Length(world.Journal), "and a later write transaction leaves it at 0 bytes");
        session.TestOnlyShutdown();
    }

    private static byte[] Bytes(string path) => File.ReadAllBytes(path);
}

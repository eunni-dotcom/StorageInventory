using System.Diagnostics;
using StorageInventory.Testing;

namespace StorageInventory.Library.Tests;

/// <summary>
/// TEST-L1 (every §6.8 state forced deterministically), TEST-L3 (creation is create-new), TEST-L5 (the header pre-check refuses a
/// suspect file before SQLite opens it), TEST-T4 (a committed snapshot that is not Published is Damaged) and TEST-C1 (corruption
/// is classified by where it is met). The states that need a second process are in <see cref="ProcessTests"/>; here, a second
/// handle in the same process excludes the lock just as a second process does (Windows share modes are per handle).
/// </summary>
public static class LibraryStateTests
{
    // ---- helpers ----

    /// <summary>Opens a session on the world and returns its start-up status, releasing the process-level hold afterwards (as the
    /// end of a process would) so that the next session of the test can take the lock.</summary>
    internal static LibraryStatus OpenAndRelease(World world, LibrarySessionOptions? options = null)
    {
        var session = world.NewSession(options);
        var status = session.RunStartupOpen();
        session.TestOnlyShutdown();
        return status;
    }

    internal static void Patch(string path, long offset, params byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        stream.Position = offset;
        stream.Write(bytes);
    }

    internal static byte[] Bytes(string path) => File.ReadAllBytes(path);

    /// <summary>A Library that was created, holds one published snapshot and was closed (the lock released as at process exit).</summary>
    internal static World LibraryWithSnapshot(int folders = 40)
    {
        var world = World.Create();
        var session = world.CreatedSession();
        ImportOne(session, new SyntheticSnapshot(folders));
        session.TestOnlyShutdown();
        return world;
    }

    internal static ImportResult ImportOne(LibrarySession session, SyntheticSnapshot snapshot, string runId = "run-1", ImportSourceSpec? source = null)
    {
        var captureId = session.NewCaptureId();
        using var prepare = World.Lease(session, MutationKind.Prepare, captureId);
        var attempt = session.RecordAttemptStartAsync(prepare, new AttemptStart(new byte[16], null, Utf16.ToBytes(@"D:\Media"), null, runId, DateTime.UtcNow.Ticks)).GetAwaiter().GetResult();
        var observation = prepare.HandOffToObservation();
        using var save = observation.HandOffToSave(out _);
        return session.ImportSnapshotAsync(save, attempt, source ?? SyntheticSnapshot.NewSource(), snapshot.Header(runId), snapshot).GetAwaiter().GetResult();
    }

    /// <summary>The same capture sequence for any row source and header.</summary>
    internal static ImportResult ImportRows(LibrarySession session, ISnapshotRowSource rows, ImportSnapshotHeader header, string runId = "run-1", ImportSourceSpec? source = null)
    {
        var captureId = session.NewCaptureId();
        using var prepare = World.Lease(session, MutationKind.Prepare, captureId);
        var attempt = session.RecordAttemptStartAsync(prepare, new AttemptStart(new byte[16], null, Utf16.ToBytes(@"D:\Media"), null, runId, DateTime.UtcNow.Ticks)).GetAwaiter().GetResult();
        var observation = prepare.HandOffToObservation();
        using var save = observation.HandOffToSave(out _);
        return session.ImportSnapshotAsync(save, attempt, source ?? SyntheticSnapshot.NewSource(), header, rows).GetAwaiter().GetResult();
    }

    private static void AssertState(LibraryState expected, LibraryStatus actual, string? context = null) =>
        Assert.Equal(expected, actual.State, $"{context}: {actual.Reason}: {actual.Message}");

    // ---- TEST-L1: every state ----

    [Test]
    public static void Not_created_when_there_is_no_directory_and_an_open_creates_nothing()
    {
        var world = World.Create();
        var status = OpenAndRelease(world);
        AssertState(LibraryState.NotCreated, status);
        Assert.False(Directory.Exists(world.LibraryDirectory), "an open creates no directory");
    }

    [Test]
    public static void Not_created_for_an_empty_directory_and_for_an_uninitialised_zero_byte_database()
    {
        var world = World.Create();
        Directory.CreateDirectory(world.LibraryDirectory);
        var first = world.NewSession();
        AssertState(LibraryState.NotCreated, first.RunStartupOpen(), "an existing empty directory");
        Assert.SequenceEqual(["library.lock"], world.Names(), "the open adds at most the 0-byte lock file");
        Assert.Equal(0L, World.Length(world.Lock));
        first.TestOnlyShutdown();

        File.WriteAllBytes(world.Main, []);
        var second = world.NewSession();
        AssertState(LibraryState.NotCreated, second.RunStartupOpen(), "a 0-byte main file is uninitialised");
        Assert.Equal(0L, World.Length(world.Main), "the uninitialised file was not touched");
        second.TestOnlyShutdown();
    }

    [Test]
    public static void Available_after_creation_and_again_when_it_is_reopened()
    {
        var world = World.Create();
        var session = world.CreatedSession();
        session.TestOnlyShutdown();
        var status = OpenAndRelease(world);
        AssertState(LibraryState.Available, status);
        Assert.False(status.RecoveryPending);
        Assert.True(status.CanSave);
    }

    [Test]
    public static void Missing_when_the_lock_pre_existed_but_the_database_is_gone_and_it_is_never_recreated_silently()
    {
        var world = World.Create();
        world.CreatedSession().TestOnlyShutdown();
        File.Delete(world.Main);
        if (File.Exists(world.Journal)) File.Delete(world.Journal);

        var session = world.NewSession();
        var open = session.RunStartupOpen();
        AssertState(LibraryState.Missing, open, "open");
        Assert.Equal(LibraryReason.MainFileMissingLockExisted, open.Reason);

        using (var prepare = World.Lease(session, MutationKind.Prepare, 1))
        {
            AssertState(LibraryState.Missing, session.PrepareForSave(prepare), "an implicit first save never recreates");
        }
        Assert.False(File.Exists(world.Main), "nothing was created behind the user's back");

        using (var create = World.Lease(session, MutationKind.Create))
        {
            AssertState(LibraryState.Available, session.CreateLibrary(create), "the explicit Create action may");
        }
        Assert.True(File.Exists(world.Main));
        session.TestOnlyShutdown();
    }

    [Test]
    public static void Leftover_files_for_a_non_empty_journal_without_a_usable_main_file_and_nothing_is_created_or_deleted()
    {
        foreach (var mainBytes in new byte[]?[] { null, [] })
        {
            var world = World.Create();
            Directory.CreateDirectory(world.LibraryDirectory);
            var journal = new byte[512];
            new Random(5).NextBytes(journal);
            File.WriteAllBytes(world.Journal, journal);
            if (mainBytes is not null) File.WriteAllBytes(world.Main, mainBytes);

            var session = world.NewSession();
            var open = session.RunStartupOpen();
            AssertState(LibraryState.LeftoverFiles, open, mainBytes is null ? "absent main file" : "0-byte main file");
            Assert.SequenceEqual(journal, Bytes(world.Journal), "the journal is essential evidence and is never deleted or changed");

            using (var prepare = World.Lease(session, MutationKind.Prepare, 1))
            {
                AssertState(LibraryState.LeftoverFiles, session.PrepareForSave(prepare), "an implicit first save is refused");
            }
            using (var create = World.Lease(session, MutationKind.Create))
            {
                AssertState(LibraryState.LeftoverFiles, session.CreateLibrary(create), "the explicit Create is refused too");
            }
            Assert.SequenceEqual(journal, Bytes(world.Journal), "still intact after both refusals");
            Assert.Equal(mainBytes is null ? -1L : 0L, World.Length(world.Main), "no database was created beside the journal");
            session.TestOnlyShutdown();
        }
    }

    [Test]
    public static void Unavailable_when_the_path_is_blocked_by_a_junction_a_network_location_or_an_unusable_lock()
    {
        // a junction in the path (LIB-05b)
        var world = World.Create();
        var target = Path.Combine(world.Root, "target");
        Directory.CreateDirectory(target);
        MakeJunction(world.LibraryDirectory, target);
        var junction = OpenAndRelease(world);
        AssertState(LibraryState.Unavailable, junction, "junction");
        Assert.Equal(LibraryReason.ReparsePoint, junction.Reason);
        Assert.Equal(0, Directory.GetFileSystemEntries(target).Length, "nothing was created through the junction");

        // a network location (LIB-05a): UNC path to the local admin share; nothing is accessed
        var unc = new LibrarySession(@"\\localhost\C$\StorageInventory-test\AppData\Library", @"\\localhost\C$\StorageInventory-test\AppData");
        var network = unc.RunStartupOpen();
        AssertState(LibraryState.Unavailable, network, "network");
        Assert.Equal(LibraryReason.PathBlocked, network.Reason);

        // a lock file that cannot be opened as a file (a directory of that name): Unavailable, not In use
        var odd = World.Create();
        Directory.CreateDirectory(odd.Lock);
        var oddStatus = OpenAndRelease(odd);
        AssertState(LibraryState.Unavailable, oddStatus, "unusable lock");
    }

    [Test]
    public static void Unavailable_for_an_unexpected_engine_and_nothing_at_all_is_written()
    {
        var world = World.Create();
        var faults = new LibraryFaultInjection { ExpectedEngine = ("3.0.0", "not-the-shipped-build") };
        var missing = world.NewSession(new LibrarySessionOptions { Faults = faults });
        var status = missing.RunStartupOpen();
        AssertState(LibraryState.Unavailable, status);
        Assert.Equal(LibraryReason.UnexpectedEngine, status.Reason);
        Assert.False(Directory.Exists(world.LibraryDirectory), "not even a directory or a lock file");
        missing.TestOnlyShutdown();

        // the same for a real Library: untouched
        var real = World.Create();
        real.CreatedSession().TestOnlyShutdown();
        var before = real.ContentHashes();
        var session = real.NewSession(new LibrarySessionOptions { Faults = faults });
        AssertState(LibraryState.Unavailable, session.RunStartupOpen(), "existing Library, wrong engine");
        session.TestOnlyShutdown();
        Assert.SequenceEqual(before.OrderBy(p => p.Key).Select(p => p.Key + p.Value), real.ContentHashes().OrderBy(p => p.Key).Select(p => p.Key + p.Value), "an unexpected engine writes nothing");
    }

    [Test]
    public static void Unavailable_restart_required_once_the_interlock_is_Faulted()
    {
        var world = World.Create();
        var session = world.CreatedSession();
        session.Interlock.ReportCatastrophic("test");
        var status = session.Status;
        AssertState(LibraryState.Unavailable, status);
        Assert.True(status.RestartRequired);
        Assert.Equal(LibraryReason.RestartRequired, status.Reason);
        Assert.Throws<LibraryUnavailableException>(() => session.Read(r => r.Long("SELECT 1")));
        session.TestOnlyShutdown();
    }

    [Test]
    public static void In_use_when_another_handle_holds_the_lock_and_no_connection_is_opened_and_nothing_changes()
    {
        var world = World.Create();
        var owner = world.CreatedSession();
        ImportOne(owner, new SyntheticSnapshot(10));
        var before = world.ContentHashes();
        var namesBefore = world.Names();

        var second = world.NewSession();
        var status = second.RunStartupOpen();
        AssertState(LibraryState.InUse, status);
        Assert.Equal(LibraryReason.LockedByAnotherProcess, status.Reason);
        Assert.SequenceEqual(namesBefore, world.Names(), "no member was created, renamed or deleted");
        Assert.SequenceEqual(before.OrderBy(p => p.Key).Select(p => p.Key + p.Value), world.ContentHashes().OrderBy(p => p.Key).Select(p => p.Key + p.Value), "no member changed");
        Assert.Throws<LibraryUnavailableException>(() => second.Read(r => r.Long("SELECT 1")));   // an In use session opens no connection and serves no read

        using (var create = World.Lease(second, MutationKind.Create))
        {
            AssertState(LibraryState.InUse, second.CreateLibrary(create), "creation is unavailable in this window");
        }
        using (var aside = World.Lease(second, MutationKind.SetAside))
        {
            var result = second.SetAsideAsync(aside).GetAwaiter().GetResult();
            AssertState(LibraryState.InUse, result.Status, "a process that does not hold the lock can never set aside");
            Assert.Null(result.Quarantine, "nothing was renamed");
        }
        Assert.SequenceEqual(namesBefore, world.Names());
        second.TestOnlyShutdown();
        owner.TestOnlyShutdown();
    }

    [Test]
    public static void Incompatible_for_a_newer_schema_version_and_the_file_is_never_modified()
    {
        var world = LibraryWithSnapshot();
        Patch(world.Main, 60, 0, 0, 0, 2);   // user_version = 2
        var before = Bytes(world.Main);
        var status = OpenAndRelease(world);
        AssertState(LibraryState.Incompatible, status);
        Assert.Equal(LibraryReason.NewerSchema, status.Reason);
        Assert.SequenceEqual(before, Bytes(world.Main), "never downgraded or modified");
        Assert.True(!File.Exists(world.Journal) || World.Length(world.Journal) == 0, "no recovery material was created");
    }

    // ---- TEST-L5 / TEST-C1: the header pre-check and corruption ----

    [Test]
    public static void The_header_pre_check_refuses_a_foreign_file_a_WAL_header_a_wrong_application_id_and_a_short_file_before_SQLite_opens_it()
    {
        var cases = new (string Name, Action<string> Make, LibraryState State, string Reason)[]
        {
            ("foreign text file", p => File.WriteAllText(p, new string('x', 5000)), LibraryState.NotALibrary, LibraryReason.ForeignFile),
            ("WAL-format header", p => { MakeValidLibraryFile(p); Patch(p, 18, 2, 2); }, LibraryState.NotALibrary, LibraryReason.WalHeader),
            ("wrong application id", p => { MakeValidLibraryFile(p); Patch(p, 68, 0, 0, 0, 7); }, LibraryState.NotALibrary, LibraryReason.WrongApplicationId),
            ("user_version 0", p => { MakeValidLibraryFile(p); Patch(p, 60, 0, 0, 0, 0); }, LibraryState.NotALibrary, LibraryReason.UnsupportedUserVersion),
            ("newer user_version", p => { MakeValidLibraryFile(p); Patch(p, 60, 0, 0, 0, 9); }, LibraryState.Incompatible, LibraryReason.NewerSchema),
            ("shorter than a header", p => File.WriteAllBytes(p, new byte[40]), LibraryState.NotALibrary, LibraryReason.TooShort),
        };
        foreach (var (name, make, state, reason) in cases)
        {
            var world = World.Create();
            Directory.CreateDirectory(world.LibraryDirectory);
            make(world.Main);
            var before = Bytes(world.Main);
            var status = OpenAndRelease(world);
            AssertState(state, status, name);
            Assert.Equal(reason, status.Reason, name);
            Assert.SequenceEqual(before, Bytes(world.Main), name + ": the file's bytes are unchanged");
            Assert.False(File.Exists(world.Journal), name + ": SQLite never opened the file, so no journal exists");
            Assert.SequenceEqual(new[] { LibraryNames.LockFile, LibraryNames.MainFile }, world.Names(), name + ": nothing else was created");
        }
    }

    private static void MakeValidLibraryFile(string path)
    {
        // a real Library's bytes, in another world (the product never opened this copy)
        var source = World.Create();
        source.CreatedSession().TestOnlyShutdown();
        File.Copy(source.Main, path, overwrite: true);
    }

    [Test]
    public static void A_creation_never_overwrites_a_file_that_is_not_a_Library()
    {
        var world = World.Create();
        Directory.CreateDirectory(world.LibraryDirectory);
        File.WriteAllText(world.Main, "this is somebody's notes, not a database");
        var before = Bytes(world.Main);
        var session = world.NewSession();
        AssertState(LibraryState.NotALibrary, session.RunStartupOpen(), "start-up");
        using (var create = World.Lease(session, MutationKind.Create))
        {
            AssertState(LibraryState.NotALibrary, session.CreateLibrary(create), "explicit creation reaches the header pre-check through LIB-07 step 4");
        }
        using (var prepare = World.Lease(session, MutationKind.Prepare, 1))
        {
            AssertState(LibraryState.NotALibrary, session.PrepareForSave(prepare), "an implicit first save does too");
        }
        Assert.SequenceEqual(before, Bytes(world.Main), "never overwritten");
        session.TestOnlyShutdown();
    }

    [Test]
    public static void An_interrupted_initialisation_is_initialised_again_on_the_next_save()
    {
        // LIB-07 step 7: a main file that is 0 bytes with no non-empty journal is uninitialised
        var world = World.Create();
        Directory.CreateDirectory(world.LibraryDirectory);
        File.WriteAllBytes(world.Main, []);
        var session = world.OpenedSession();
        AssertState(LibraryState.NotCreated, session.Status);
        using (var prepare = World.Lease(session, MutationKind.Prepare, 1))
        {
            AssertState(LibraryState.Available, session.PrepareForSave(prepare), "the 0-byte file is initialised, not replaced");
        }
        Assert.True(World.Length(world.Main) > 0);
        session.TestOnlyShutdown();
        AssertState(LibraryState.Available, OpenAndRelease(world));
    }

    [Test]
    public static void Not_a_Library_for_wal_and_shm_files_and_for_an_unexpected_schema()
    {
        foreach (var side in new[] { LibraryNames.WalFile, LibraryNames.ShmFile })
        {
            var world = World.Create();
            world.CreatedSession().TestOnlyShutdown();
            File.WriteAllBytes(world.Member(side), [1, 2, 3]);
            var status = OpenAndRelease(world);
            AssertState(LibraryState.NotALibrary, status, side);
            Assert.Equal(LibraryReason.WalOrShmPresent, status.Reason);
            Assert.SequenceEqual(new byte[] { 1, 2, 3 }, Bytes(world.Member(side)), "never deleted");
        }

        // an extra trigger, view and index each change the fingerprint (TEST-S1 asserts the details)
        foreach (var ddl in new[]
        {
            "CREATE TRIGGER sneaky AFTER INSERT ON name BEGIN SELECT 1; END",
            "CREATE VIEW sneaky AS SELECT * FROM name",
            "CREATE INDEX sneaky ON name (name_id)",
            "CREATE TABLE sneaky (x INTEGER)",
        })
        {
            var world = World.Create();
            world.CreatedSession().TestOnlyShutdown();
            RawSqlite.Execute(world.Main, ddl);
            var status = OpenAndRelease(world);
            AssertState(LibraryState.NotALibrary, status, ddl);
            Assert.Equal(LibraryReason.SchemaFingerprint, status.Reason);
        }
    }

    [Test]
    public static void Damaged_when_a_committed_snapshot_is_not_Published()
    {
        // TEST-T4: a Library copy edited to hold a committed state <> 2 row
        foreach (var state in new[] { 1, 3 })
        {
            var world = LibraryWithSnapshot();
            RawSqlite.Execute(world.Main, $"UPDATE snapshot SET state = {state}");
            var status = OpenAndRelease(world);
            AssertState(LibraryState.Damaged, status, "state " + state);
            Assert.Equal(LibraryReason.NotPublishedSnapshot, status.Reason);
            Assert.Equal((long)state, Convert.ToInt64(RawSqlite.Scalar(world.Main, "SELECT state FROM snapshot")), "nothing was repaired or silently ignored");
        }
    }

    [Test]
    public static void Corruption_met_while_opening_is_Damaged_and_corruption_in_an_untouched_table_is_found_only_when_it_is_read()
    {
        // (a) a truncated file: the header is intact, the pages are missing
        {
            var world = LibraryWithSnapshot();
            var length = World.Length(world.Main);
            using (var stream = new FileStream(world.Main, FileMode.Open, FileAccess.Write, FileShare.None)) stream.SetLength(8192);
            var status = OpenAndRelease(world);
            AssertState(LibraryState.Damaged, status, $"truncated from {length} to 8192 bytes");
            Assert.Equal(8192L, World.Length(world.Main), "nothing was deleted or repaired");
        }

        // (b) pages the open reads: sqlite_schema (page 1), library_info and snapshot (the step 7 check)
        foreach (var table in new[] { "sqlite_schema", "library_info", "snapshot" })
        {
            var world = LibraryWithSnapshot();
            var root = table == "sqlite_schema" ? 1 : Convert.ToInt32(RawSqlite.Scalar(world.Main, $"SELECT rootpage FROM sqlite_schema WHERE name = '{table}'"));
            CorruptPage(world.Main, root);
            var status = OpenAndRelease(world);
            AssertState(LibraryState.Damaged, status, "corrupt " + table);
            Assert.True(status.Reason is LibraryReason.Corrupt or LibraryReason.NotPublishedSnapshot or LibraryReason.SchemaFingerprint, status.Reason);
        }

        // (c) a page of an observation table the open does not read: Available at open, Damaged at the first read that touches it
        {
            var world = LibraryWithSnapshot(folders: 400);
            var root = Convert.ToInt32(RawSqlite.Scalar(world.Main, "SELECT rootpage FROM sqlite_schema WHERE name = 'file_obs'"));
            CorruptPage(world.Main, root);
            var session = world.NewSession();
            var open = session.RunStartupOpen();
            AssertState(LibraryState.Available, open, "an untouched observation table is not examined at open (no quick_check at every open)");
            var failure = Assert.Throws<Exception>(() => session.Read(r => r.Long("SELECT count(*) FROM file_obs")));
            Assert.Equal("SqliteException", failure.GetType().Name, "the engine reports the corruption when the damaged table is read");
            AssertState(LibraryState.Damaged, session.Status, "quick_check confirmed it after the read failed");
            Assert.Throws<LibraryUnavailableException>(() => session.Read(r => r.Long("SELECT 1")));   // a Damaged Library is not read again
            Assert.Equal(World.Length(world.Main), World.Length(world.Main), "nothing deleted");
            session.TestOnlyShutdown();
        }
    }

    private static void CorruptPage(string path, int page)
    {
        const int pageSize = 4096;
        var junk = new byte[200];
        Array.Fill(junk, (byte)0xFF);
        Patch(path, (long)(page - 1) * pageSize + (page == 1 ? 100 : 0), junk);
    }

    private static void MakeJunction(string link, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode, "mklink /J");
    }
}

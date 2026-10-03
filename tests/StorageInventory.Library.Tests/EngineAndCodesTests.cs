using System.Text.RegularExpressions;
using StorageInventory.Core;
using StorageInventory.Testing;

namespace StorageInventory.Library.Tests;

/// <summary>
/// Review observations closed with tests rather than words: the stable codes that appear as literals inside constant SQL are pinned to
/// <see cref="StableCodes"/> (C4-O13); SQLite creates no temporary file anywhere (<c>temp_store = MEMORY</c>, SEC-05; C4-O12); an
/// engine that cannot be loaded is Unavailable with nothing written (C4-O08); a lease of another interlock ends nothing (C4-O05); the
/// set-aside publishes its state outside the gate (C4-O07); a hostile sealed total allocates nothing large (C4-O14).
/// </summary>
public static class EngineAndCodesTests
{
    // ---------------------------------------------------------------- C4-O13

    private static int[] Literals(string sql, string pattern)
    {
        var match = Regex.Match(sql, pattern, RegexOptions.CultureInvariant);
        Assert.True(match.Success, $"the SQL no longer contains <{pattern}>: {sql}");
        return [.. Regex.Matches(match.Groups[1].Value, @"\d+").Select(m => int.Parse(m.Value))];
    }

    [Test]
    public static void The_stable_codes_that_appear_as_literals_in_constant_SQL_are_the_codes_of_StableCodes()
    {
        // an enum value renamed or a code remapped in StableCodes can never silently change what a hard-coded statement means
        Assert.SequenceEqual(new[] { StableCodes.ToCode(ScanErrorType.ReparsePointSkipped), StableCodes.ToCode(ScanErrorType.ReparsePointFile) }, Literals(ImportSql.CountRealScanErrors, @"error_type NOT IN \(([^)]*)\)"), "the informational error types of invariant 10");
        Assert.SequenceEqual(new[] { StableCodes.ToCode(FolderScanStatus.Ok), StableCodes.ToCode(FolderScanStatus.Partial) }, Literals(ImportSql.VerifyParentClosure, @"po\.status NOT IN \(([^)]*)\)"), "the statuses of a listed parent (invariant 3)");
        Assert.SequenceEqual(new[] { StableCodes.ToCode(FolderScanStatus.Unreadable), StableCodes.ToCode(FolderScanStatus.ReparsePointSkipped) }, Literals(ImportSql.VerifyNoChildrenOfUnlisted, @"o\.status IN \(([^)]*)\)"), "the unlisted statuses (invariant 8)");
        Assert.SequenceEqual(new[] { StableCodes.ToCode(FolderScanStatus.Unreadable), StableCodes.ToCode(FolderScanStatus.Partial) }, Literals(ImportSql.VerifyIncompleteFolders, @"o\.status IN \(([^)]*)\)"), "the incomplete statuses (invariant 9)");
        Assert.SequenceEqual(new[] { StableCodes.ToCode(SnapshotState.Published) }, Literals(ImportSql.PublishSnapshot, @"SET state = (\d+),"), "publishing sets Published");
        Assert.SequenceEqual(new[] { StableCodes.ToCode(SnapshotState.Importing) }, Literals(ImportSql.PublishSnapshot, @"AND state = (\d+)"), "only an Importing snapshot is published");
        Assert.SequenceEqual(new[] { StableCodes.ToCode(SnapshotState.Importing) }, Literals(ImportSql.InsertSnapshot, @"VALUES \(\s*\$snapshot_id, \$source_id, \$attempt_id, (\d+),"), "a snapshot row is inserted Importing");
        Assert.SequenceEqual(new[] { StableCodes.ToCode(SnapshotState.Deleting) }, Literals(DeleteSql.MarkDeleting, @"SET state = (\d+)"), "deletion marks Deleting");
        Assert.SequenceEqual(new[] { StableCodes.ToCode(SnapshotState.Published) }, Literals(DeleteSql.MarkDeleting, @"AND state = (\d+)"), "only a Published snapshot is deleted");
        Assert.SequenceEqual(new[] { StableCodes.ToCode(SnapshotState.Published) }, Literals(OpenSql.CountUnpublishedSnapshots, @"state <> (\d+)"), "the committed-state check (LIB-08 step 7)");
        Assert.SequenceEqual(new[] { StableCodes.ToCode(AttemptOutcome.InProgress) }, Literals(ImportSql.InsertAttempt, @"\$started_utc, (\d+)\)"), "an attempt is inserted InProgress");
        Assert.SequenceEqual(new[] { StableCodes.ToCode(AttemptOutcome.InProgress) }, Literals(ImportSql.RecordAttemptOutcome, @"AND outcome = (\d+)"), "T-OUTCOME changes only an InProgress attempt");
        Assert.SequenceEqual(new[] { StableCodes.ToCode(AttemptOutcome.InProgress) }, Literals(ImportSql.VerifyAttempt, @"AND outcome = (\d+)"), "the attempt must be InProgress");
        Assert.SequenceEqual(new[] { StableCodes.ToCode(AttemptOutcome.Published) }, Literals(ImportSql.PublishAttempt, @"SET outcome = (\d+),"), "publishing records Published");
        Assert.SequenceEqual(new[] { StableCodes.ToCode(AttemptOutcome.InProgress) }, Literals(ImportSql.PublishAttempt, @"AND outcome = (\d+)"), "only an InProgress attempt is published");
        Assert.SequenceEqual(new[] { StableCodes.ToCode(AttemptOutcome.Interrupted) }, Literals(OpenSql.RecoverInterruptedAttempts, @"SET outcome = (\d+),"), "recovery records Interrupted");
        Assert.SequenceEqual(new[] { StableCodes.ToCode(AttemptOutcome.InProgress) }, Literals(OpenSql.RecoverInterruptedAttempts, @"WHERE outcome = (\d+)"), "recovery changes only InProgress attempts");
    }

    // ---------------------------------------------------------------- C4-O12

    [Test]
    public static void SQLite_creates_no_temporary_file_during_an_import_a_verification_a_deletion_and_reads()
    {
        var scratch = Path.Combine(World.RunRoot, "tmp-watch-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(scratch);
        var previous = Environment.GetEnvironmentVariable("TMP");
        var previousTemp = Environment.GetEnvironmentVariable("TEMP");
        Environment.SetEnvironmentVariable("TMP", scratch);    // SQLite asks Windows for the temp folder at the moment it needs one
        Environment.SetEnvironmentVariable("TEMP", scratch);
        try
        {
            var created = new List<string>();
            using var watcher = new FileSystemWatcher(scratch) { NotifyFilter = NotifyFilters.FileName, IncludeSubdirectories = true };
            watcher.Created += (_, e) => { lock (created) created.Add(e.Name ?? ""); };
            watcher.EnableRaisingEvents = true;

            // CONTROL: a plain connection that sorts more rows than its cache holds, with temp_store = FILE, does create etilqs_* files
            // here: so the watcher can see one, and its silence below means something
            var controlWorld = World.Create();
            Directory.CreateDirectory(controlWorld.LibraryDirectory);
            File.WriteAllBytes(controlWorld.Main, []);
            RawSqlite.Execute(controlWorld.Main, ["PRAGMA temp_store = FILE", "PRAGMA cache_size = 10", "CREATE TABLE sorted_t (a INTEGER, b TEXT)",
                "WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c WHERE x < 60000) INSERT INTO sorted_t SELECT x, hex(randomblob(40)) FROM c",
                "SELECT count(*) FROM (SELECT b FROM sorted_t ORDER BY b)"]);
            Thread.Sleep(300);
            int controlCount;
            lock (created) controlCount = created.Count(n => n.Contains("etilqs_", StringComparison.OrdinalIgnoreCase));
            Assert.True(controlCount > 0, "the control: temp_store = FILE with a small cache creates an etilqs_ temporary file, and the watcher saw it (" + string.Join(",", created) + ")");
            lock (created) created.Clear();

            // the product: an import, its verification inside the transaction, reads, a deletion
            var world = World.Create();
            var session = world.CreatedSession();
            LibraryStateTests.ImportOne(session, new SyntheticSnapshot(1500, seed: 1));
            var source = session.Read(r => r.Long("SELECT source_id FROM snapshot WHERE snapshot_id = 1"));
            LibraryStateTests.ImportOne(session, new SyntheticSnapshot(1500, seed: 2, label: "b"), "run-2", new ImportSourceSpec.Existing(source));
            session.Read(r => SnapshotVerifier.VerifyPublished(r, 1));
            session.Read(r => r.Query(ReadSql.TopFilesBySize, ("$snapshot_id", 1L), ("$limit", 20)).Count);
            using (var lease = World.Lease(session, MutationKind.Delete)) session.DeleteSnapshotAsync(lease, 1).GetAwaiter().GetResult();
            Thread.Sleep(300);
            lock (created) Assert.Equal(0, created.Count(n => n.Contains("etilqs_", StringComparison.OrdinalIgnoreCase)), "no temporary file: " + string.Join(",", created));
            Assert.True(world.Names().All(n => n is LibraryNames.LockFile or LibraryNames.MainFile or LibraryNames.JournalFile), "nor in the Library folder");
            session.TestOnlyShutdown();
        }
        finally
        {
            Environment.SetEnvironmentVariable("TMP", previous);
            Environment.SetEnvironmentVariable("TEMP", previousTemp);
        }
    }

    // ---------------------------------------------------------------- C4-O08

    [Test]
    public static void An_engine_that_cannot_be_loaded_is_Unavailable_and_nothing_is_created()
    {
        foreach (Exception failure in new Exception[] { new DllNotFoundException("e_sqlite3"), new BadImageFormatException("wrong architecture"), new EntryPointNotFoundException("sqlite3_open"), new TypeInitializationException("SQLitePCL.raw", new DllNotFoundException()) })
        {
            foreach (var directoryExists in new[] { false, true })
            {
                var world = World.Create();
                if (directoryExists) Directory.CreateDirectory(world.LibraryDirectory);
                var session = world.NewSession(new LibrarySessionOptions { Faults = new LibraryFaultInjection { EngineLoadFailure = failure } });
                var status = session.RunStartupOpen();
                Assert.Equal(LibraryState.Unavailable, status.State, $"{failure.GetType().Name}: {status.Message}");
                Assert.Equal(LibraryReason.UnexpectedEngine, status.Reason, failure.GetType().Name);
                Assert.Equal(InterlockStateKind.Idle, session.Interlock.Snapshot.Kind, "a class A outcome: the start-up open ends cleanly, scans run without saving");
                Assert.Equal(directoryExists, Directory.Exists(world.LibraryDirectory), "no directory was created");
                Assert.Equal(0, world.Names().Count, "no lock file, no member of any kind");
                session.TestOnlyShutdown();
            }
        }
    }

    // ---------------------------------------------------------------- C4-O05, C4-O07

    [Test]
    public static void A_lease_of_another_interlock_ends_nothing_even_with_an_equal_id()
    {
        var worldA = World.Create("a");
        var worldB = World.Create("b");
        var a = worldA.NewSession();
        var b = worldB.NewSession();
        var leaseOfA = a.Interlock.TakeStartupLease();
        var leaseOfB = b.Interlock.TakeStartupLease();
        Assert.Equal(leaseOfA.Id, leaseOfB.Id, "both start-up leases are lease 1");
        b.Interlock.EndMutation(leaseOfA);   // A's lease offered to B's interlock: it must not end B's lease
        Assert.Equal(InterlockStateKind.Mutating, b.Interlock.Snapshot.Kind, "B is still in its start-up open");
        a.Interlock.EndMutation(default);   // a default value ends nothing either
        Assert.Equal(InterlockStateKind.Mutating, a.Interlock.Snapshot.Kind);
        leaseOfB.Dispose();
        Assert.Equal(InterlockStateKind.Idle, b.Interlock.Snapshot.Kind, "B's own lease ends it");
        leaseOfA.Dispose();
        a.TestOnlyShutdown();
        b.TestOnlyShutdown();
    }

    [Test]
    public static void The_set_aside_publishes_its_state_after_the_gate_is_released()
    {
        var world = World.Create();
        var session = world.CreatedSession();
        var writerActiveWhenPublished = new List<bool>();
        session.StatusChanged += _ => writerActiveWhenPublished.Add(session.Gate.WriterActive);
        using (var aside = World.Lease(session, MutationKind.SetAside)) session.SetAsideAsync(aside).GetAwaiter().GetResult();
        Assert.True(writerActiveWhenPublished.Count > 0, "the state change was published");
        Assert.False(writerActiveWhenPublished.Any(x => x), "never while the set-aside still held the gate's writer ticket: a handler that reads synchronously would deadlock");
        session.TestOnlyShutdown();
    }

    // ---------------------------------------------------------------- C4-O14

    [Test]
    public static void A_hostile_sealed_total_is_refused_without_allocating_a_bitmap_for_it()
    {
        var world = World.Create();
        var session = world.CreatedSession();
        LibraryStateTests.ImportOne(session, new SyntheticSnapshot(200));
        RawSqlite.Execute(world.Main, ["UPDATE snapshot SET files = 2147483647, folders = 2147483647 WHERE snapshot_id = 1"]);
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var message = session.Read(r => SnapshotVerifier.VerifyPublished(r, 1));
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        Assert.True(message is not null && message.Contains("invariant 6", StringComparison.Ordinal), "the sealed total is compared with the data first: " + message);
        Assert.True(allocated < 32L * 1024 * 1024, $"no 256 MB bitmap was allocated for a count that the data does not hold ({allocated} bytes allocated)");
        session.TestOnlyShutdown();
    }
}

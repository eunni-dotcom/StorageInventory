using System.Security.Cryptography;
using StorageInventory.Testing;

namespace StorageInventory.Library.Tests;

/// <summary>
/// T-DELETE's rollback (LIB-12, TEST-T3 intent; C4-M12), the gate that every writer takes before it opens (CONC-06; C4-M10) and the
/// path and header checks that run before every writer open, not only at the first open (SEC-32, §6.4; C4-M11).
/// </summary>
public static class WriterOpenGuardTests
{
    // ---------------------------------------------------------------- C4-M12: a partial deletion is never committed

    private static (World World, LibrarySession Session, LibraryFaultInjection Faults, Action<Action<string>> Arm) WithThreeSnapshots()
    {
        Action<string>? hook = null;
        var faults = new LibraryFaultInjection { AfterDeleteStep = step => hook?.Invoke(step) };
        var world = World.Create();
        var session = world.CreatedSession(new LibrarySessionOptions { Faults = faults });
        LibraryStateTests.ImportOne(session, new SyntheticSnapshot(150, seed: 1));
        var source = session.Read(r => r.Long("SELECT source_id FROM snapshot WHERE snapshot_id = 1"));
        LibraryStateTests.ImportOne(session, new SyntheticSnapshot(150, seed: 2, label: "b"), "run-2", new ImportSourceSpec.Existing(source));
        LibraryStateTests.ImportOne(session, new SyntheticSnapshot(150, seed: 3, label: "c"), "run-3", new ImportSourceSpec.Existing(source));
        return (world, session, faults, a => hook = a);
    }

    [Test]
    public static void A_deletion_that_fails_or_is_cancelled_after_any_statement_leaves_the_snapshot_whole_and_published()
    {
        foreach (var step in new[] { "mark", "file_obs", "folder_obs", "scan_error", "extension_total" })
        {
            foreach (var mode in new[] { "fail", "cancel" })
            {
                var (world, session, _, arm) = WithThreeSnapshots();
                using var cancel = new CancellationTokenSource();
                var one = ImportTests.Checksum(session, 1);
                var two = ImportTests.Checksum(session, 2);
                var three = ImportTests.Checksum(session, 3);
                var files = session.Read(r => r.Long("SELECT count(*) FROM file_obs WHERE snapshot_id = 2"));
                var folders = session.Read(r => r.Long("SELECT count(*) FROM folder_obs WHERE snapshot_id = 2"));
                var next = session.Read(r => r.Long("SELECT next_snapshot_id FROM library_info"));
                var mainLength = World.Length(world.Main);
                arm(s =>
                {
                    if (s != step) return;
                    if (mode == "fail") throw new InvalidOperationException($"planted failure after the {step} statement");
                    cancel.Cancel();
                });

                using (var lease = World.Lease(session, MutationKind.Delete))
                {
                    if (mode == "fail") Assert.Throws<InvalidOperationException>(() => session.DeleteSnapshotAsync(lease, 2, cancel.Token).GetAwaiter().GetResult());
                    else Assert.Throws<OperationCanceledException>(() => session.DeleteSnapshotAsync(lease, 2, cancel.Token).GetAwaiter().GetResult());
                }
                arm(_ => { });
                var label = $"{mode} after {step}";
                Assert.Equal(two, ImportTests.Checksum(session, 2), label + ": the snapshot is byte-identical (nothing of a partial deletion was committed)");
                Assert.Equal(2L, session.Read(r => r.Long("SELECT state FROM snapshot WHERE snapshot_id = 2")), label + ": still Published, never left Deleting");
                Assert.Equal(files, session.Read(r => r.Long("SELECT count(*) FROM file_obs WHERE snapshot_id = 2")), label + ": every file row");
                Assert.Equal(folders, session.Read(r => r.Long("SELECT count(*) FROM folder_obs WHERE snapshot_id = 2")), label);
                Assert.Equal(one, ImportTests.Checksum(session, 1), label + ": other snapshots untouched");
                Assert.Equal(three, ImportTests.Checksum(session, 3), label + ": other snapshots untouched");
                Assert.Equal(next, session.Read(r => r.Long("SELECT next_snapshot_id FROM library_info")), label);
                Assert.Equal(mainLength, World.Length(world.Main), label + ": the file is as long as it was");
                Assert.True(World.Length(world.Journal) <= 0, label + ": the journal is 0 bytes");
                Assert.Null(session.Read(r => SnapshotVerifier.VerifyPublished(r, 2)), label + ": and it still verifies");
                Assert.False(session.Interlock.IsFaulted, label + ": class A and cancellation are not faults");

                // and the deletion then works when nothing interferes
                using (var lease = World.Lease(session, MutationKind.Delete)) session.DeleteSnapshotAsync(lease, 2).GetAwaiter().GetResult();
                Assert.Equal(0L, session.Read(r => r.Long("SELECT count(*) FROM snapshot WHERE snapshot_id = 2")), label + ": the retry deleted it");
                session.TestOnlyShutdown();
            }
        }
    }

    [Test]
    public static void Deleting_a_snapshot_that_is_not_Published_is_refused_and_changes_nothing()
    {
        var (world, session, _, _) = WithThreeSnapshots();
        var before = ImportTests.Checksum(session, 2);
        // a committed snapshot that is not Published proves SQLite's atomic commit was defeated: the writer open refuses (Damaged) ...
        RawSqlite.Execute(world.Main, "UPDATE snapshot SET state = 3 WHERE snapshot_id = 2");
        using (var lease = World.Lease(session, MutationKind.Delete))
        {
            var refusal = Assert.Throws<LibraryChangedException>(() => session.DeleteSnapshotAsync(lease, 2).GetAwaiter().GetResult());
            Assert.Contains("never completed", refusal.Message);
        }
        RawSqlite.Execute(world.Main, "UPDATE snapshot SET state = 2 WHERE snapshot_id = 2");
        Assert.Equal(before, ImportTests.Checksum(session, 2), "nothing was deleted");
        // ... and a snapshot id that is not a snapshot at all is refused by the statement itself: exactly one row must change
        using (var lease = World.Lease(session, MutationKind.Delete))
        {
            var failure = Assert.Throws<DeleteException>(() => session.DeleteSnapshotAsync(lease, 4242).GetAwaiter().GetResult());
            Assert.Contains("0 rows changed", failure.Message);
        }
        Assert.Equal(before, ImportTests.Checksum(session, 2));
        session.TestOnlyShutdown();
    }

    // ---------------------------------------------------------------- C4-M10: open, create and recover take the gate

    [Test]
    public static void An_open_that_rolls_a_journal_back_or_recovers_takes_the_gate_so_an_active_reader_is_preempted_first()
    {
        var world = World.Create();
        var session = world.CreatedSession();
        LibraryStateTests.ImportOne(session, new SyntheticSnapshot(60));
        var readerStarted = new ManualResetEventSlim();
        var readerSawPreemption = false;
        var readerFinished = new ManualResetEventSlim();
        // a long read that finishes only when the gate tells it to stop (a writer asked for the gate)
        var reading = Task.Run(() => session.ReadAsync(r =>
        {
            readerStarted.Set();
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!r.Token.IsCancellationRequested && DateTime.UtcNow < deadline) Thread.Sleep(5);
            readerSawPreemption = r.Token.IsCancellationRequested;
            readerFinished.Set();
            return 0;
        }));
        Assert.True(readerStarted.Wait(10_000), "the reader is inside its read");

        // a Retry-open from Idle: an Open lease, the writer opens, T-RECOVER commits. It must take the gate as a writer first, which
        // cancels the reader and waits for it to leave; it must never run beside an active reader (SQLITE_BUSY, a rollback that
        // races a reader's connection)
        Assert.True(session.TryRetryOpen(out var status, out var refusal), refusal);
        Assert.Equal(LibraryState.Available, status.State, status.Message);
        Assert.True(readerSawPreemption, "the gate cancelled the reader's token before the open's writer was opened");
        Assert.True(readerFinished.IsSet, "and waited for the reader to finish: the open never ran beside it");
        Assert.True(reading.Wait(10_000));
        Assert.Equal(0, session.Gate.ActiveReaders, "no reader left");
        Assert.False(session.Gate.WriterActive, "the open released the gate");
        session.TestOnlyShutdown();
    }

    // ---------------------------------------------------------------- C4-M11: every writer open re-checks the path and the header

    private static (World World, LibrarySession Session, long SourceId) Opened()
    {
        var world = World.Create();
        var session = world.CreatedSession();
        var first = Imports.Synthetic(session, new SyntheticSnapshot(100, seed: 1), "older");
        return (world, session, first.SourceId);
    }

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [Test]
    public static void A_main_file_swapped_for_another_file_after_the_open_is_refused_before_any_writer_is_opened()
    {
        foreach (var (name, replace) in new (string, Action<World>)[]
        {
            ("a foreign file", w => File.WriteAllBytes(w.Main, [.. Enumerable.Repeat((byte)'x', 8192)])),
            ("a Library of a newer version", w => RawSqlite.Execute(w.Main, "PRAGMA user_version = 9")),
            ("a database with another application id", w => RawSqlite.Execute(w.Main, "PRAGMA application_id = 7")),
        })
        {
            var (world, session, sourceId) = Opened();
            var captureId = session.NewCaptureId();
            using var prepare = World.Lease(session, MutationKind.Prepare, captureId);
            var attempt = session.RecordAttemptStartAsync(prepare, new AttemptStart(new byte[16], null, Utf16.ToBytes(@"D:\Media"), null, "run-2", 1)).GetAwaiter().GetResult();
            using var save = prepare.HandOffToObservation().HandOffToSave(out _);

            replace(world);   // hours later, with the application running: the lock does not protect the main file
            var swapped = Sha(world.Main);
            var writers = WriterConnection.WritersOpenedTotal;
            var snapshot = new SyntheticSnapshot(100, seed: 2, label: "t");
            var failure = Assert.Throws<ImportException>(() => session.ImportSnapshotAsync(save, attempt, new ImportSourceSpec.Existing(sourceId), snapshot.Header("run-2"), snapshot).GetAwaiter().GetResult());
            Assert.Equal(CaptureFailureKind.LibraryUnavailable, failure.Kind, name + ": " + failure.Message);
            Assert.Equal(writers, WriterConnection.WritersOpenedTotal, name + ": the swapped file was never opened read-write");
            Assert.Equal(swapped, Sha(world.Main), name + ": the swapped file was not changed");
            Assert.False(session.Interlock.IsFaulted, name + ": class A");
            session.TestOnlyShutdown();
        }
    }

    [Test]
    public static void Every_mutation_re_checks_the_Library_before_it_writes_T0_T_OUTCOME_T_DELETE_and_T_IMPORT()
    {
        var (world, session, sourceId) = Opened();
        LibraryStateTests.ImportOne(session, new SyntheticSnapshot(60, seed: 4, label: "d"), "run-d", new ImportSourceSpec.Existing(sourceId));
        File.WriteAllBytes(world.Main, [.. Enumerable.Repeat((byte)'x', 8192)]);
        var swapped = Sha(world.Main);
        var writers = WriterConnection.WritersOpenedTotal;

        using (var prepare = World.Lease(session, MutationKind.Prepare, 1))
        {
            var failure = Assert.Throws<LibraryChangedException>(() => session.RecordAttemptStartAsync(prepare, new AttemptStart(new byte[16], null, Utf16.ToBytes("x"), null, "r", 1)).GetAwaiter().GetResult());
            Assert.Contains("no longer a Library", failure.Message);
        }
        using (var delete = World.Lease(session, MutationKind.Delete))
        {
            Assert.Throws<LibraryChangedException>(() => session.DeleteSnapshotAsync(delete, 1).GetAwaiter().GetResult());
        }
        Assert.Equal(writers, WriterConnection.WritersOpenedTotal, "no writer was opened for any of them");
        Assert.Equal(swapped, Sha(world.Main), "the file was never touched");
        session.TestOnlyShutdown();
    }

    [Test]
    public static void A_database_with_the_right_header_but_another_schema_is_refused_by_the_check_through_SQLite()
    {
        var (world, session, sourceId) = Opened();
        RawSqlite.Execute(world.Main, "CREATE TABLE smuggled (a INTEGER)");   // the header is still a Library's: only the schema fingerprint can tell
        using var prepare = World.Lease(session, MutationKind.Prepare, 1);
        var failure = Assert.Throws<LibraryChangedException>(() => session.RecordAttemptStartAsync(prepare, new AttemptStart(new byte[16], null, Utf16.ToBytes("x"), null, "r", 1)).GetAwaiter().GetResult());
        Assert.Contains("expected structure", failure.Message);
        Assert.Equal(0L, Convert.ToInt64(RawSqlite.Scalar(world.Main, "SELECT count(*) FROM scan_attempt WHERE outcome = 1")), "T0 wrote nothing");
        _ = sourceId;
        session.TestOnlyShutdown();
    }
}

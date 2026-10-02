using System.Security.Cryptography;
using System.Text;
using StorageInventory.Core;
using StorageInventory.Testing;

namespace StorageInventory.Library.Tests;

/// <summary>The one-transaction import primitive (T-IMPORT), snapshot deletion (T-DELETE) and open-time recovery (T-RECOVER): the
/// storage engine's atomicity, isolation and durability, exercised on real SQLite files. Not C5's capture: the rows come from a
/// synthetic abstract stream.</summary>
public static class ImportTests
{
    /// <summary>A checksum of every row of one snapshot in every table that holds them, in key order.</summary>
    internal static string Checksum(LibrarySession session, long snapshotId) => session.Read(r =>
    {
        var text = new StringBuilder();
        (string Table, string Sql)[] tables =
        [
            ("snapshot", "SELECT * FROM snapshot WHERE snapshot_id = $s"),
            ("folder_obs", "SELECT * FROM folder_obs WHERE snapshot_id = $s"),
            ("file_obs", "SELECT * FROM file_obs WHERE snapshot_id = $s"),
            ("scan_error", "SELECT * FROM scan_error WHERE snapshot_id = $s"),
            ("snapshot_extension_total", "SELECT * FROM snapshot_extension_total WHERE snapshot_id = $s"),
        ];
        foreach (var (table, sql) in tables)
        {
            text.Append('[').Append(table).Append(']');
            foreach (var row in r.Query(sql, ("$s", snapshotId)))
            {
                foreach (var value in row) text.Append(value is byte[] b ? Convert.ToHexString(b) : value?.ToString() ?? "NULL").Append('|');
                text.Append('\n');
            }
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    });

    private static long Count(LibrarySession session, string sql) => session.Read(r => r.Long(sql));

    private static string? Verify(LibrarySession session, long snapshotId) => session.Read(r => SnapshotVerifier.VerifyPublished(r, snapshotId));

    [Test]
    public static void An_import_publishes_one_verified_snapshot_in_one_transaction()
    {
        var world = World.Create();
        var session = world.CreatedSession();
        var snapshot = new SyntheticSnapshot(300);
        var result = LibraryStateTests.ImportOne(session, snapshot);

        Assert.Equal(1L, result.SnapshotId, "snapshot ids come from next_snapshot_id, starting at 1");
        Assert.Equal(snapshot.Files, result.Files);
        Assert.Equal(300L, result.Folders);
        Assert.Equal(2L, Count(session, "SELECT state FROM snapshot WHERE snapshot_id = 1"), "Published");
        Assert.Equal(snapshot.Files, Count(session, "SELECT count(*) FROM file_obs"));
        Assert.Equal(snapshot.Bytes, Count(session, "SELECT sum(size_bytes) FROM file_obs"));
        Assert.Equal(2L, Count(session, "SELECT next_snapshot_id FROM library_info"), "advanced by one");
        Assert.Equal(2L, Count(session, "SELECT outcome FROM scan_attempt WHERE attempt_id = 1"), "the attempt is Published");
        Assert.Equal(0L, Count(session, "SELECT count(*) FROM snapshot WHERE state <> 2"));
        Assert.Null(Verify(session, 1), "invariants 1 to 12 hold for the committed snapshot");
        Assert.True(Count(session, "SELECT count(*) FROM snapshot_extension_total") > 0);

        // exact names: the root's empty name and an ordinary one, as UTF-16LE bytes
        var rootName = session.Read(r => r.Query("SELECT n.utf16 FROM folder_obs o JOIN folder_path p ON p.path_id = o.path_id JOIN name n ON n.name_id = p.name_id WHERE o.snapshot_id = 1 AND o.discovery_index = 0"));
        Assert.Equal(0, ((byte[])rootName[0][0]!).Length, "the root folder's name is the empty name");
        session.TestOnlyShutdown();
    }

    [Test]
    public static void A_second_snapshot_of_the_source_shares_dictionaries_and_leaves_the_first_byte_identical()
    {
        var world = World.Create();
        var session = world.CreatedSession();
        LibraryStateTests.ImportOne(session, new SyntheticSnapshot(200, seed: 1));
        var first = Checksum(session, 1);
        var paths = Count(session, "SELECT count(*) FROM folder_path");
        var names = Count(session, "SELECT count(*) FROM name");
        var sourceId = Count(session, "SELECT source_id FROM snapshot WHERE snapshot_id = 1");

        var second = LibraryStateTests.ImportOne(session, new SyntheticSnapshot(200, seed: 2, label: "t"), "run-2", new ImportSourceSpec.Existing(sourceId));
        Assert.Equal(2L, second.SnapshotId);
        Assert.Equal(first, Checksum(session, 1), "an import never mutates an older snapshot");
        Assert.Equal(paths, Count(session, "SELECT count(*) FROM folder_path"), "folder paths are shared per source: the second snapshot interned none");
        Assert.True(Count(session, "SELECT count(*) FROM name") > names, "new file names were added to the dictionary");
        Assert.True(Count(session, "SELECT count(*) FROM name") < names + second.Files, "and existing names were reused");
        Assert.Equal(1L, Count(session, "SELECT count(*) FROM source"), "one source");
        Assert.Equal(1L, Count(session, "SELECT count(*) FROM volume"), "one volume");
        Assert.Null(Verify(session, 2));
        session.TestOnlyShutdown();
    }

    [Test]
    public static void Every_planted_defect_rolls_the_whole_import_back_and_leaves_no_trace()
    {
        foreach (var defect in new[]
        {
            SyntheticSnapshot.Mutation.WrongFileSize,
            SyntheticSnapshot.Mutation.DuplicateSeq,
            SyntheticSnapshot.Mutation.MissingFolderRow,
            SyntheticSnapshot.Mutation.WrongSealedFileCount,
        })
        {
            var world = World.Create();
            var session = world.CreatedSession();
            LibraryStateTests.ImportOne(session, new SyntheticSnapshot(200, seed: 1));
            var sourceId = Count(session, "SELECT source_id FROM snapshot WHERE snapshot_id = 1");
            var first = Checksum(session, 1);
            var names = Count(session, "SELECT count(*) FROM name");
            var paths = Count(session, "SELECT count(*) FROM folder_path");
            var nextId = Count(session, "SELECT next_snapshot_id FROM library_info");

            var failure = Assert.Throws<ImportException>(() => LibraryStateTests.ImportOne(session, new SyntheticSnapshot(200, seed: 2, label: "t", mutation: defect), "run-2", new ImportSourceSpec.Existing(sourceId)));
            Assert.Equal(CaptureFailureKind.InvariantViolation, failure.Kind, defect + ": " + failure.Message);

            Assert.Equal(1L, Count(session, "SELECT count(*) FROM snapshot"), defect + ": no partial snapshot is visible");
            Assert.Equal(first, Checksum(session, 1), defect + ": the older snapshot is intact");
            Assert.Equal(names, Count(session, "SELECT count(*) FROM name"), defect + ": the rolled-back names are gone");
            Assert.Equal(paths, Count(session, "SELECT count(*) FROM folder_path"), defect + ": the rolled-back paths are gone");
            Assert.Equal(nextId, Count(session, "SELECT next_snapshot_id FROM library_info"), defect + ": the sequence did not advance");
            Assert.Equal(1L, Count(session, "SELECT count(*) FROM scan_attempt WHERE outcome = 1"), defect + ": the attempt of the failed import is still InProgress (T-OUTCOME is the caller's)");
            Assert.False(session.Interlock.IsFaulted, defect + ": a class A failure is not a fault");
            session.TestOnlyShutdown();
        }
    }

    [Test]
    public static void A_failure_thrown_by_the_row_source_rolls_back_and_a_retry_publishes_correct_data()
    {
        var world = World.Create();
        var session = world.CreatedSession();
        Assert.Throws<InvalidOperationException>(() => LibraryStateTests.ImportOne(session, new SyntheticSnapshot(120, mutation: SyntheticSnapshot.Mutation.ThrowAfterFolders)));
        Assert.Equal(0L, Count(session, "SELECT count(*) FROM snapshot"));
        Assert.Equal(0L, Count(session, "SELECT count(*) FROM name"), "the names interned before the failure were rolled back with it (no stale per-capture cache)");
        Assert.Equal(0L, Count(session, "SELECT count(*) FROM folder_path"));
        Assert.Equal(1L, Count(session, "SELECT next_snapshot_id FROM library_info"));

        var retry = LibraryStateTests.ImportOne(session, new SyntheticSnapshot(120), "run-retry");
        Assert.Equal(1L, retry.SnapshotId, "the failed attempt consumed no snapshot id");
        Assert.Null(Verify(session, 1));
        session.TestOnlyShutdown();
    }

    [Test]
    public static void A_source_that_disappeared_fails_as_SourceChanged_and_changes_nothing()
    {
        var world = World.Create();
        var session = world.CreatedSession();
        var failure = Assert.Throws<ImportException>(() => LibraryStateTests.ImportOne(session, new SyntheticSnapshot(20), "r", new ImportSourceSpec.Existing(999)));
        Assert.Equal(CaptureFailureKind.SourceChanged, failure.Kind);
        Assert.Equal(0L, Count(session, "SELECT count(*) FROM snapshot"));
        session.TestOnlyShutdown();
    }

    [Test]
    public static void SQLITE_FULL_from_the_engine_rolls_the_import_back_without_damage()
    {
        var world = World.Create();
        var faults = new LibraryFaultInjection { LimitDatabaseToTwoThousandPages = true };
        var session = world.CreatedSession(new LibrarySessionOptions { Faults = faults });
        // an older snapshot first (small enough to fit under the 2,048-page limit)
        LibraryStateTests.ImportOne(session, new SyntheticSnapshot(300, seed: 1));
        var first = Checksum(session, 1);
        var sizeBefore = World.Length(world.Main);
        var sourceId = Count(session, "SELECT source_id FROM snapshot WHERE snapshot_id = 1");

        var failure = Assert.Throws<ImportException>(() => LibraryStateTests.ImportOne(session, new SyntheticSnapshot(40_000, seed: 2, label: "big"), "run-2", new ImportSourceSpec.Existing(sourceId)));
        Assert.Equal(CaptureFailureKind.LibraryFull, failure.Kind, "the engine's SQLITE_FULL is classified LibraryFull (201): " + failure.Message);
        Assert.Equal(1L, Count(session, "SELECT count(*) FROM snapshot"), "no partial snapshot");
        Assert.Equal(first, Checksum(session, 1), "the older snapshot is intact");
        Assert.Equal(sizeBefore, World.Length(world.Main), "the rollback restored the file size");
        session.TestOnlyShutdown();

        // the Library is coherent: a fresh session opens it Available (no fault injection), verifies it and imports normally
        var after = world.OpenedSession();
        Assert.Equal(LibraryState.Available, after.Status.State, after.Status.Message);
        Assert.Null(Verify(after, 1));
        Assert.Equal(1L, Count(after, "SELECT count(*) FROM scan_attempt WHERE outcome = 8"), "the interrupted attempt was recovered");
        LibraryStateTests.ImportOne(after, new SyntheticSnapshot(300, seed: 3, label: "ok"), "run-3", new ImportSourceSpec.Existing(sourceId));
        Assert.Null(Verify(after, 2));
        after.TestOnlyShutdown();
    }

    // ---- T-DELETE ----

    [Test]
    public static void Deleting_a_snapshot_removes_exactly_its_rows_and_nothing_of_any_other_snapshot()
    {
        var world = World.Create();
        var session = world.CreatedSession();
        LibraryStateTests.ImportOne(session, new SyntheticSnapshot(150, seed: 1));
        var sourceId = Count(session, "SELECT source_id FROM snapshot WHERE snapshot_id = 1");
        LibraryStateTests.ImportOne(session, new SyntheticSnapshot(150, seed: 2, label: "b"), "run-2", new ImportSourceSpec.Existing(sourceId));
        LibraryStateTests.ImportOne(session, new SyntheticSnapshot(150, seed: 3, label: "c"), "run-3", new ImportSourceSpec.Existing(sourceId));
        var one = Checksum(session, 1);
        var three = Checksum(session, 3);
        var filesOfTwo = Count(session, "SELECT count(*) FROM file_obs WHERE snapshot_id = 2");
        Assert.True(filesOfTwo > 0);

        using (var lease = World.Lease(session, MutationKind.Delete))
        {
            session.DeleteSnapshotAsync(lease, 2).GetAwaiter().GetResult();
        }
        Assert.Equal(0L, Count(session, "SELECT count(*) FROM snapshot WHERE snapshot_id = 2"));
        Assert.Equal(0L, Count(session, "SELECT count(*) FROM file_obs WHERE snapshot_id = 2"));
        Assert.Equal(0L, Count(session, "SELECT count(*) FROM folder_obs WHERE snapshot_id = 2"));
        Assert.Equal(0L, Count(session, "SELECT count(*) FROM snapshot_extension_total WHERE snapshot_id = 2"));
        Assert.Equal(one, Checksum(session, 1), "other snapshots are never touched");
        Assert.Equal(three, Checksum(session, 3), "other snapshots are never touched");
        Assert.Equal(1L, Count(session, "SELECT count(*) FROM scan_attempt WHERE attempt_id = 2 AND outcome = 2"), "the attempt row stays");
        Assert.Equal(4L, Count(session, "SELECT next_snapshot_id FROM library_info"), "snapshot ids are never reused, even after deletion (SCH-11)");
        Assert.Null(Verify(session, 1));
        Assert.Null(Verify(session, 3));
        session.TestOnlyShutdown();
    }

    [Test]
    public static void Deleting_something_that_is_not_a_published_snapshot_changes_nothing()
    {
        var world = World.Create();
        var session = world.CreatedSession();
        LibraryStateTests.ImportOne(session, new SyntheticSnapshot(50));
        var before = Checksum(session, 1);
        using (var lease = World.Lease(session, MutationKind.Delete))
        {
            Assert.Throws<DeleteException>(() => session.DeleteSnapshotAsync(lease, 77).GetAwaiter().GetResult());
        }
        Assert.Equal(before, Checksum(session, 1));
        Assert.False(session.Interlock.IsFaulted);
        session.TestOnlyShutdown();
    }

    [Test]
    public static void Cancelling_a_deletion_rolls_it_back()
    {
        var world = World.Create();
        using var cancel = new CancellationTokenSource();
        var faults = new LibraryFaultInjection { AfterBegin = () => cancel.Cancel() };   // cancelled right after BEGIN IMMEDIATE
        var session = world.CreatedSession(new LibrarySessionOptions { Faults = faults });
        LibraryStateTests.ImportOne(session, new SyntheticSnapshot(80));
        var before = Checksum(session, 1);
        using (var lease = World.Lease(session, MutationKind.Delete))
        {
            Assert.Throws<OperationCanceledException>(() => session.DeleteSnapshotAsync(lease, 1, cancel.Token).GetAwaiter().GetResult());
        }
        Assert.Equal(before, Checksum(session, 1), "the snapshot is intact");
        Assert.Equal(2L, Count(session, "SELECT state FROM snapshot WHERE snapshot_id = 1"), "still Published, never left Deleting");
        Assert.False(session.Interlock.IsFaulted, "cancellation is not a fault");
        session.TestOnlyShutdown();
    }

    // ---- T-RECOVER ----

    [Test]
    public static void Recovery_marks_attempts_of_dead_sessions_Interrupted_and_never_those_of_the_live_session()
    {
        var world = World.Create();
        var dead = world.CreatedSession();
        using (var prepare = World.Lease(dead, MutationKind.Prepare, 1))
        {
            dead.RecordAttemptStartAsync(prepare, new AttemptStart(new byte[16], null, Utf16.ToBytes(@"D:\Media"), null, "run-dead", 1)).GetAwaiter().GetResult();
        }
        dead.TestOnlyShutdown();

        var live = world.NewSession();
        Assert.Equal(LibraryState.Available, live.RunStartupOpen().State);
        Assert.Equal(1L, live.Read(r => r.Long("SELECT count(*) FROM scan_attempt WHERE outcome = 8")), "the dead session's attempt became Interrupted");
        Assert.Equal(0L, live.Read(r => r.Long("SELECT count(*) FROM scan_attempt WHERE outcome = 1")));

        // an attempt of THIS session, then a re-open (Retry): it must stay InProgress
        using (var prepare = World.Lease(live, MutationKind.Prepare, 2))
        {
            live.RecordAttemptStartAsync(prepare, new AttemptStart(new byte[16], null, Utf16.ToBytes(@"D:\Media"), null, "run-live", 2)).GetAwaiter().GetResult();
        }
        Assert.True(live.TryRetryOpen(out var retried, out _));
        Assert.Equal(LibraryState.Available, retried.State);
        Assert.Equal(1L, live.Read(r => r.Long("SELECT count(*) FROM scan_attempt WHERE outcome = 1")), "the live session's own attempt is not recovered");
        Assert.Equal(1L, live.Read(r => r.Long("SELECT count(*) FROM scan_attempt WHERE outcome = 8")));
        live.TestOnlyShutdown();
    }

    [Test]
    public static void The_T_OUTCOME_update_only_changes_this_sessions_InProgress_attempt()
    {
        var world = World.Create();
        var session = world.CreatedSession();
        using (var prepare = World.Lease(session, MutationKind.Prepare, 1))
        {
            var attempt = session.RecordAttemptStartAsync(prepare, new AttemptStart(new byte[16], null, Utf16.ToBytes(@"D:\Media"), null, "run-1", 1)).GetAwaiter().GetResult();
            var save = prepare.HandOffToObservation().HandOffToSave(out _);
            Assert.True(session.RecordAttemptOutcomeAsync(save, attempt, AttemptOutcome.NotEligible, CaptureFailureKind.LibraryChangedDuringScan, "changed").GetAwaiter().GetResult());
            Assert.False(session.RecordAttemptOutcomeAsync(save, attempt, AttemptOutcome.Cancelled, null, "again").GetAwaiter().GetResult(), "an attempt that is no longer InProgress is not changed again");
            save.Dispose();
        }
        Assert.Equal(5L, session.Read(r => r.Long("SELECT outcome FROM scan_attempt WHERE attempt_id = 1")));
        Assert.Equal(104L, session.Read(r => r.Long("SELECT failure_kind FROM scan_attempt WHERE attempt_id = 1")));
        session.TestOnlyShutdown();
    }
}

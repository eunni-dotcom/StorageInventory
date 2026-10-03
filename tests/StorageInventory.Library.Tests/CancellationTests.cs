using System.Diagnostics;
using StorageInventory.Testing;

namespace StorageInventory.Library.Tests;

/// <summary>A capture-and-import in one call, with the options and the save token a test needs.</summary>
internal static class Imports
{
    internal static ImportResult Run(LibrarySession session, ISnapshotRowSource rows, ImportSnapshotHeader header, string runId, ImportSourceSpec source,
        ImportOptions? options = null, CancellationToken token = default)
    {
        var captureId = session.NewCaptureId();
        using var prepare = World.Lease(session, MutationKind.Prepare, captureId);
        var attempt = session.RecordAttemptStartAsync(prepare, new AttemptStart(new byte[16], null, Utf16.ToBytes(@"D:\Media"), null, runId, DateTime.UtcNow.Ticks)).GetAwaiter().GetResult();
        using var save = prepare.HandOffToObservation().HandOffToSave(out _);
        return session.ImportSnapshotAsync(save, attempt, source, header, rows, options, token).GetAwaiter().GetResult();
    }

    internal static ImportResult Synthetic(LibrarySession session, SyntheticSnapshot snapshot, string runId, ImportSourceSpec? source = null, ImportOptions? options = null, CancellationToken token = default) =>
        Run(session, snapshot, snapshot.Header(runId), runId, source ?? SyntheticSnapshot.NewSource(), options, token);

    /// <summary>Everything an import must leave exactly as it was when it is rolled back.</summary>
    internal sealed record Footprint(long Snapshots, long Names, long Paths, long NextSnapshotId, long InProgress, long MainLength, string FirstChecksum)
    {
        internal static Footprint Of(World world, LibrarySession session, long checksummedSnapshot = 1) => new(
            session.Read(r => r.Long("SELECT count(*) FROM snapshot")),
            session.Read(r => r.Long("SELECT count(*) FROM name")),
            session.Read(r => r.Long("SELECT count(*) FROM folder_path")),
            session.Read(r => r.Long("SELECT next_snapshot_id FROM library_info")),
            session.Read(r => r.Long("SELECT count(*) FROM scan_attempt WHERE outcome = 1")),
            World.Length(world.Main),
            ImportTests.Checksum(session, checksummedSnapshot));
    }
}

/// <summary>
/// Cancellation of the one-transaction import (CAN-01d, CAN-03, IMP-07; C4-M02): the save token is looked at during row insertion
/// (at every space check), inside the in-transaction verification (between queries, every 65,536 rows of a streamed query and by the
/// engine's progress callback inside a statement), after the verification, and at the final check immediately before <c>COMMIT</c>.
/// Each cancel point is shown to roll back to the exact state before; the last look at the token is the final check, and a
/// cancellation after it publishes (CAN-01e). The progress callback's lifecycle (installed for the scope only, removed on dispose,
/// confined to its own connection) is tested directly.
/// </summary>
public static class CancellationTests
{
    private static (World World, LibrarySession Session, long SourceId) WithOlderSnapshot(LibrarySessionOptions? options = null)
    {
        var world = World.Create();
        var session = world.CreatedSession(options);
        var first = Imports.Synthetic(session, new SyntheticSnapshot(300, seed: 1), "older");
        return (world, session, first.SourceId);
    }

    private static void AssertRolledBack(World world, LibrarySession session, Imports.Footprint before, string label)
    {
        var after = Imports.Footprint.Of(world, session);
        // the failed import's own attempt (recorded by T0 before the import) is still InProgress: T-OUTCOME is the caller's
        Assert.Equal(before with { InProgress = before.InProgress + 1 }, after, label + ": the Library is exactly as it was before the import");
        Assert.True(World.Length(world.Journal) <= 0, label + ": the journal is 0 bytes after the rollback");
        Assert.Null(session.Read(r => SnapshotVerifier.VerifyPublished(r, 1)));
        Assert.False(session.Interlock.IsFaulted, label + ": a cancellation is not a fault");
    }

    [Test]
    public static void A_cancellation_at_each_of_the_four_cancel_points_rolls_back_to_the_exact_state_before()
    {
        foreach (var point in new[] { ImportPoint.AfterRowCheck, ImportPoint.VerificationStart, ImportPoint.VerificationMiddle, ImportPoint.FinalStatements })
        {
            var (world, session, sourceId) = WithOlderSnapshot();
            var before = Imports.Footprint.Of(world, session);
            using var cancel = new CancellationTokenSource();
            var reached = false;
            var options = new ImportOptions { Probe = p => { if (p == point) { reached = true; cancel.Cancel(); } } };
            // 1,500 folders: about 8,000 observation rows, so there are two row checks before the verification
            Assert.Throws<OperationCanceledException>(() => Imports.Synthetic(session, new SyntheticSnapshot(1500, seed: 2, label: "t"), "second", new ImportSourceSpec.Existing(sourceId), options, cancel.Token));
            Assert.True(reached, $"{point}: the import reached the cancel point");
            AssertRolledBack(world, session, before, point.ToString());
            // and the Library is usable: the same import, not cancelled, publishes
            var retry = Imports.Synthetic(session, new SyntheticSnapshot(1500, seed: 2, label: "t"), "third", new ImportSourceSpec.Existing(sourceId));
            Assert.Equal(2L, retry.SnapshotId, point + ": the cancelled import consumed no snapshot id");
            session.TestOnlyShutdown();
        }
    }

    [Test]
    public static void A_cancellation_before_BEGIN_or_right_after_it_changes_nothing()
    {
        // a token that is already cancelled never reaches the writer
        {
            var (world, session, sourceId) = WithOlderSnapshot();
            var before = Imports.Footprint.Of(world, session);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Assert.Throws<OperationCanceledException>(() => Imports.Synthetic(session, new SyntheticSnapshot(100, seed: 2, label: "t"), "second", new ImportSourceSpec.Existing(sourceId), token: cancelled.Token));
            AssertRolledBack(world, session, before, "before BEGIN");
            session.TestOnlyShutdown();
        }
        // cancelled right after BEGIN IMMEDIATE of the import (its second transaction: T0 is the first): the first check, IMP-11's
        // BEGIN check, looks at the token and refuses before any row is inserted
        {
            using var cancel = new CancellationTokenSource();
            var armed = false;
            var begins = 0;
            var faults = new LibraryFaultInjection { AfterBegin = () => { if (armed && ++begins == 2) cancel.Cancel(); } };
            var world = World.Create();
            var session = world.CreatedSession(new LibrarySessionOptions { Faults = faults });
            var first = Imports.Synthetic(session, new SyntheticSnapshot(300, seed: 1), "older");
            var before = Imports.Footprint.Of(world, session);
            var rowsInserted = false;
            var options = new ImportOptions { AfterRows = () => rowsInserted = true };
            armed = true;
            Assert.Throws<OperationCanceledException>(() => Imports.Synthetic(session, new SyntheticSnapshot(100, seed: 2, label: "t"), "second", new ImportSourceSpec.Existing(first.SourceId), options, cancel.Token));
            armed = false;
            Assert.Equal(2, begins, "the cancellation happened right after the import's BEGIN IMMEDIATE");
            Assert.False(rowsInserted, "no row was inserted: the BEGIN check refused");
            AssertRolledBack(world, session, before, "right after BEGIN");
            session.TestOnlyShutdown();
        }
    }

    [Test]
    public static void A_cancellation_after_the_final_check_is_not_observed_and_the_save_publishes()
    {
        // CAN-01e: the final check is the last look at the token; nothing between it and COMMIT looks again
        using var cancel = new CancellationTokenSource();
        var armed = false;
        var commits = 0;
        var faults = new LibraryFaultInjection { BeforeCommit = () => { if (armed && ++commits == 2) cancel.Cancel(); } };   // T0 commits first, then the import
        var world = World.Create();
        var session = world.CreatedSession(new LibrarySessionOptions { Faults = faults });
        var first = Imports.Synthetic(session, new SyntheticSnapshot(300, seed: 1), "older");
        armed = true;
        var result = Imports.Synthetic(session, new SyntheticSnapshot(300, seed: 2, label: "t"), "second", new ImportSourceSpec.Existing(first.SourceId), token: cancel.Token);
        armed = false;
        Assert.True(cancel.IsCancellationRequested, "the token was cancelled before COMMIT");
        Assert.Equal(2L, result.SnapshotId, "and the snapshot was published: the cancellation came after the last check");
        Assert.Equal(2L, session.Read(r => r.Long("SELECT state FROM snapshot WHERE snapshot_id = 2")));
        Assert.Null(session.Read(r => SnapshotVerifier.VerifyPublished(r, 2)));
        session.TestOnlyShutdown();
    }

    [Test]
    public static void The_final_check_looks_at_the_token_after_the_final_statements_and_before_COMMIT()
    {
        // cancel point 4 detected by the final check itself (not by the check that follows the verification): cancelling in the
        // probe that runs between the final statements is seen only by the check that precedes COMMIT
        var (world, session, sourceId) = WithOlderSnapshot();
        var before = Imports.Footprint.Of(world, session);
        using var cancel = new CancellationTokenSource();
        var checks = new List<SpaceCheck>();
        var options = new ImportOptions { Probe = p => { if (p == ImportPoint.FinalStatements) cancel.Cancel(); }, OnSpaceCheck = checks.Add };
        Assert.Throws<OperationCanceledException>(() => Imports.Synthetic(session, new SyntheticSnapshot(200, seed: 2, label: "t"), "second", new ImportSourceSpec.Existing(sourceId), options, cancel.Token));
        Assert.True(checks.Count > 0 && checks[^1].Kind != SpaceCheckKind.Final, "the token check refused before the final space check recorded anything");
        AssertRolledBack(world, session, before, "final statements");
        session.TestOnlyShutdown();
    }

    [Test]
    public static void Every_check_looks_at_the_token_and_the_largest_gap_between_looks_is_reported()
    {
        var world = World.Create();
        var session = world.CreatedSession();
        var result = Imports.Synthetic(session, new SyntheticSnapshot(1500, seed: 1), "run-1");
        var checks = result.Checks!;
        Assert.True(checks.SpaceChecks >= 3, "at least BEGIN, one row check and the final check");
        Assert.True(checks.TokenObservations >= checks.SpaceChecks, $"every space check looked at the token ({checks.TokenObservations} looks, {checks.SpaceChecks} checks)");
        Assert.True(checks.MaxTokenGap < TimeSpan.FromSeconds(0.5), "no two looks at the save token were 0.5 s apart (CAN-01d): " + checks.MaxTokenGap);
        session.TestOnlyShutdown();
    }

    // ---------------------------------------------------------------- the progress callback's lifecycle

    [Test]
    public static void The_progress_callback_interrupts_a_long_statement_promptly_is_removed_with_its_scope_and_touches_no_other_connection()
    {
        var world = World.Create();
        var session = world.CreatedSession();
        var captureId = session.NewCaptureId();
        using var prepare = World.Lease(session, MutationKind.Prepare, captureId);
        using var writer = LibraryDatabase.OpenWriter(prepare, session.Interlock, world.Main, "probe", [MutationKind.Prepare], false, null);
        writer.Begin(prepare);
        // a statement that runs for many seconds without touching a table: only the progress callback can stop it
        const string Long = "WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c WHERE x < 400000000) SELECT count(*) FROM c";

        using var cancel = new CancellationTokenSource();
        var checker = new TokenChecker(cancel.Token);
        var cancelledAt = new Stopwatch();
        var elapsed = TimeSpan.Zero;
        using (writer.BeginCancellationScope(prepare, cancel.Token, checker))
        {
            var longRun = Task.Run(() =>
            {
                var sw = Stopwatch.StartNew();
                try { writer.Scalar(prepare, Long); }
                finally { elapsed = sw.Elapsed; }
            });
            // while it runs, a reader on ANOTHER connection is unaffected by the callback (it is per connection)
            Thread.Sleep(150);
            Assert.Equal(1L, session.Read(r => r.Long("SELECT count(*) FROM library_info")), "an unrelated reader reads normally while the writer's statement runs under a progress callback");
            cancelledAt.Start();
            cancel.Cancel();
            var failure = Assert.Throws<AggregateException>(() => longRun.Wait(30_000));
            Assert.True(failure.InnerException is OperationCanceledException, "the interrupted statement surfaces as a cancellation, not an error: " + failure.InnerException);
            Assert.True(cancelledAt.Elapsed < TimeSpan.FromSeconds(1.5), "the statement stopped within a moment of the cancellation: " + cancelledAt.Elapsed);
        }
        Assert.True(checker.MaxGap < TimeSpan.FromSeconds(0.5), "the callback looked at the token at least every 0.5 s while the statement ran: " + checker.MaxGap);
        Assert.True(checker.Observations > 10, "the callback ran many times: " + checker.Observations);

        // the scope is gone: the same, still cancelled token no longer interrupts anything on this connection, and the callback is
        // never called again
        var observationsAfter = checker.Observations;
        var counted = Convert.ToInt64(writer.Scalar(prepare, "WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c WHERE x < 3000000) SELECT count(*) FROM c"));
        Assert.Equal(3_000_000L, counted, "a statement after the scope runs to completion although the token is cancelled");
        Assert.Equal(observationsAfter, checker.Observations, "the callback was uninstalled: it was not called again");
        writer.Rollback(prepare);
        session.TestOnlyShutdown();
    }
}

using StorageInventory.Testing;

namespace StorageInventory.Library.Tests;

/// <summary>
/// The import primitive's space guard (IMP-11, C4's part; TEST-L9; D-R3): it is consulted at <c>BEGIN</c>, after every 4,096 inserted
/// observation rows and in a final check immediately before <c>COMMIT</c>, with exact inputs (the transaction's page count, the page
/// size, the main file's and the journal's lengths through handles, and the pending growth <c>Λ</c> = page_count × page size − main
/// length). A "stop", an exception from the guard, or an input that cannot be read rolls back as <c>LibraryFull</c>; the save token is
/// checked at every check before the guard is asked. C5 wires the rule (free space, margin, reserve): nothing here knows the volume.
/// </summary>
public static class SpaceGuardTests
{
    private sealed class RecordingGuard(Func<SpaceCheck, int, bool> permit) : ISpaceGuard
    {
        internal List<SpaceCheck> Checks { get; } = [];

        public bool Permit(in SpaceCheck check)
        {
            Checks.Add(check);
            return permit(check, Checks.Count);
        }
    }

    private static (World World, LibrarySession Session, long SourceId) WithOlderSnapshot()
    {
        var world = World.Create();
        var session = world.CreatedSession();
        var first = Imports.Synthetic(session, new SyntheticSnapshot(300, seed: 1), "older");
        return (world, session, first.SourceId);
    }

    /// <summary>Runs an import whose attempt (T0) has already been recorded, so the state "before" includes the attempt row, which a
    /// failed import must leave untouched (T-OUTCOME is the caller's).</summary>
    private static (Exception? Failure, ImportResult? Result, Imports.Footprint Before, string AttemptBefore) AttemptImport(World world, LibrarySession session, ISnapshotRowSource rows,
        ImportSnapshotHeader header, string runId, ImportSourceSpec source, ImportOptions options, CancellationToken token = default)
    {
        var captureId = session.NewCaptureId();
        using var prepare = World.Lease(session, MutationKind.Prepare, captureId);
        var attempt = session.RecordAttemptStartAsync(prepare, new AttemptStart(new byte[16], null, Utf16.ToBytes(@"D:\Media"), null, runId, 1)).GetAwaiter().GetResult();
        using var save = prepare.HandOffToObservation().HandOffToSave(out _);
        var before = Imports.Footprint.Of(world, session);
        var attemptBefore = AttemptRow(session, attempt.AttemptId);
        try
        {
            return (null, session.ImportSnapshotAsync(save, attempt, source, header, rows, options, token).GetAwaiter().GetResult(), before, attemptBefore);
        }
        catch (Exception ex)
        {
            return (ex, null, before, attemptBefore);
        }
    }

    private static string AttemptRow(LibrarySession session, long attemptId) =>
        string.Join('|', session.Read(r => r.Query("SELECT outcome, source_id, ended_utc, failure_kind, run_id FROM scan_attempt WHERE attempt_id = $a", ("$a", attemptId)))[0].Select(v => v?.ToString() ?? "NULL"));

    private static void AssertGuardStopRolledBack(World world, LibrarySession session, Exception? failure, Imports.Footprint before, string attemptBefore, long attemptId, string label)
    {
        var stopped = failure as ImportException;
        Assert.True(stopped is not null, label + ": expected an ImportException but got " + (failure?.ToString() ?? "no failure"));
        Assert.Equal(CaptureFailureKind.LibraryFull, stopped!.Kind, label + ": " + stopped.Message);
        Assert.Equal(before, Imports.Footprint.Of(world, session), label + ": the main file's size through a handle, the dictionaries, the snapshots and the sequence are as before");
        Assert.True(World.Length(world.Journal) <= 0, label + ": the journal is 0 bytes");
        Assert.Equal(attemptBefore, AttemptRow(session, attemptId), label + ": the attempt row is unchanged");
        Assert.Null(session.Read(r => SnapshotVerifier.VerifyPublished(r, 1)), label + ": the older snapshot verifies");
        Assert.False(session.Interlock.IsFaulted, label + ": LibraryFull is class A");
    }

    [Test]
    public static void A_guard_that_stops_at_BEGIN_at_the_kth_check_or_at_the_final_check_rolls_back_as_LibraryFull_without_damage()
    {
        var cases = new (string Name, Func<SpaceCheck, int, bool> Permit)[]
        {
            ("stop at BEGIN", (c, n) => c.Kind != SpaceCheckKind.Begin),
            ("stop at the 2nd check", (c, n) => n != 2),
            ("stop at the 4th check", (c, n) => n != 4),
            ("stop at the final check", (c, n) => c.Kind != SpaceCheckKind.Final),
        };
        foreach (var (name, permit) in cases)
        {
            var (world, session, sourceId) = WithOlderSnapshot();
            var guard = new RecordingGuard(permit);
            var snapshot = new SyntheticSnapshot(2500, seed: 2, label: "t");   // about 13,000 rows: BEGIN, three row checks, final
            var (failure, result, before, attemptBefore) = AttemptImport(world, session, snapshot, snapshot.Header("second"), "second", new ImportSourceSpec.Existing(sourceId), new ImportOptions { SpaceGuard = guard });
            Assert.Null(result, name);
            AssertGuardStopRolledBack(world, session, failure, before, attemptBefore, attemptId: 2, name);
            if (name.EndsWith("final check", StringComparison.Ordinal)) Assert.Equal(SpaceCheckKind.Final, guard.Checks[^1].Kind, "the guard stopped the import at the final check, after every other check passed");
            session.TestOnlyShutdown();
        }
    }

    [Test]
    public static void A_guard_that_throws_stops_the_import_as_LibraryFull_but_a_class_C_failure_in_it_faults_the_interlock()
    {
        foreach (var (name, exception) in new (string, Exception)[]
        {
            ("a guard that throws", new InvalidOperationException("the guard failed")),
            ("a guard whose query failed", new IOException("the free space could not be read")),
            ("a guard that reports cancellation without a cancelled token", new OperationCanceledException()),
        })
        {
            var (world, session, sourceId) = WithOlderSnapshot();
            var guard = new RecordingGuard((c, n) => n < 2 ? true : throw exception);
            var snapshot = new SyntheticSnapshot(1500, seed: 2, label: "t");
            var (failure, result, before, attemptBefore) = AttemptImport(world, session, snapshot, snapshot.Header("second"), "second", new ImportSourceSpec.Existing(sourceId), new ImportOptions { SpaceGuard = guard });
            Assert.Null(result, name);
            AssertGuardStopRolledBack(world, session, failure, before, attemptBefore, attemptId: 2, name);
            session.TestOnlyShutdown();
        }
        {
            var (world, session, sourceId) = WithOlderSnapshot();
            var guard = new RecordingGuard((c, n) => n < 2 ? true : throw new OutOfMemoryException());
            var snapshot = new SyntheticSnapshot(1500, seed: 2, label: "t");
            var (failure, result, _, _) = AttemptImport(world, session, snapshot, snapshot.Header("second"), "second", new ImportSourceSpec.Existing(sourceId), new ImportOptions { SpaceGuard = guard });
            Assert.Null(result);
            Assert.True(failure is OutOfMemoryException, "class C passes through unchanged: " + failure);
            Assert.True(session.Interlock.IsFaulted, "and the interlock is Faulted (OBS-13)");
            session.TestOnlyShutdown();
        }
    }

    [Test]
    public static void The_save_token_is_looked_at_at_every_check_before_the_guard_is_asked()
    {
        var (world, session, sourceId) = WithOlderSnapshot();
        using var cancel = new CancellationTokenSource();
        var guard = new RecordingGuard((c, n) => { if (n == 2) cancel.Cancel(); return true; });
        // about 13,000 rows: BEGIN, three row checks, final. The guard cancels at the 2nd check; only the NEXT check's own look at the
        // token can stop the import before the guard is asked again (the explicit check after the rows comes later)
        var snapshot = new SyntheticSnapshot(2500, seed: 2, label: "t");
        var (failure, result, before, attemptBefore) = AttemptImport(world, session, snapshot, snapshot.Header("second"), "second", new ImportSourceSpec.Existing(sourceId), new ImportOptions { SpaceGuard = guard }, cancel.Token);
        Assert.Null(result);
        Assert.True(failure is OperationCanceledException, "a cancellation, not LibraryFull: " + failure);
        Assert.Equal(2, guard.Checks.Count, "the guard was not asked again once the token was cancelled: the next check looked at the token first");
        Assert.Equal(before, Imports.Footprint.Of(world, session));
        Assert.Equal(attemptBefore, AttemptRow(session, 2));
        session.TestOnlyShutdown();
    }

    [Test]
    public static void The_hook_receives_exact_inputs_at_BEGIN_every_4096_rows_and_the_final_check_and_COMMIT_grows_the_file_by_the_final_pending_growth()
    {
        var world = World.Create();
        var session = world.CreatedSession();
        var checks = new List<SpaceCheck>();
        var snapshot = new SyntheticSnapshot(2500, seed: 1);   // a NEW source: append-shaped
        var result = Imports.Synthetic(session, snapshot, "run-1", options: new ImportOptions { OnSpaceCheck = checks.Add });

        Assert.Equal(SpaceCheckKind.Begin, checks[0].Kind, "the first check is at BEGIN");
        Assert.Equal(SpaceCheckKind.Final, checks[^1].Kind, "the last check is the final check");
        Assert.Equal(1, checks.Count(c => c.Kind == SpaceCheckKind.Final), "one final check");
        Assert.Equal(0L, checks[0].RowsInserted, "nothing inserted at BEGIN");
        var rows = checks.Where(c => c.Kind == SpaceCheckKind.Rows).ToList();
        var observationRows = result.Files + result.Folders + result.ScanErrors + Convert.ToInt64(session.Read(r => r.Long("SELECT count(*) FROM snapshot_extension_total")));
        Assert.Equal(observationRows / 4096, (long)rows.Count, "a row check after every 4,096 inserted observation rows (IMP-11, CAN-01d)");
        for (var i = 0; i < rows.Count; i++) Assert.True(rows[i].RowsInserted >= (i + 1) * 4096L, $"row check {i + 1} came after at least {(i + 1) * 4096} rows");
        Assert.Equal(observationRows, checks[^1].RowsInserted, "the final check came after every row");
        foreach (var c in checks)
        {
            Assert.Equal(Math.Max(0L, c.PageCount * c.PageSize - c.MainFileLength), c.PendingGrowth, $"{c.Kind}: Λ = max(0, page_count × page_size − main length)");
            Assert.Equal(4096L, c.PageSize, "the page size");
            Assert.True(c.PageCount >= 1 && c.MainFileLength >= 0 && c.JournalLength >= 0, "every input was read");
        }
        Assert.True(checks[^1].PendingGrowth > 0, "an import of this size has pages that only COMMIT writes to the file");
        // COMMIT writes exactly the pending pages (SQLite's commit phase one extends the file to page_count x page size): measured equal
        var checksRecord = result.Checks!;
        Assert.Equal(checks[^1].PendingGrowth, checksRecord.FinalPendingGrowth, "the result carries the final check's Λ");
        Assert.True(checksRecord.CommitGrowth >= 0 && checksRecord.CommitGrowth <= checksRecord.FinalPendingGrowth + 1024 * 1024, $"COMMIT's growth {checksRecord.CommitGrowth} lies within the final Λ {checksRecord.FinalPendingGrowth} + the 1 MiB commit reserve (R_C)");
        Assert.Equal(checksRecord.FinalPendingGrowth, checksRecord.CommitGrowth, "and equals it to the byte (measured, given no chunk size, no auto-vacuum and 4 KiB pages)");
        Assert.Equal(checks[^1].JournalLength, result.PeakJournalBytes, "the reported peak journal is the journal's length through a handle at the final check");
        Assert.True(result.PeakJournalBytes > 0, "the journal holds the transaction's original page images immediately before COMMIT");
        Assert.Equal(0L, Math.Max(0L, World.Length(world.Journal)), "and is 0 bytes once COMMIT has truncated it");
        session.TestOnlyShutdown();
    }

    [Test]
    public static void An_input_that_cannot_be_read_stops_the_import_as_LibraryFull_but_only_when_something_consults_the_numbers()
    {
        foreach (var (name, failure) in new (string, Exception)[]
        {
            ("the main file's length is unreadable", new IOException("the main file could not be measured")),
            ("access to the file is denied", new UnauthorizedAccessException("denied")),
            ("a malformed value", new FormatException("not a number")),
        })
        {
            var world = World.Create();
            var session = world.CreatedSession();
            SnapshotImporter.FileLengths broken = () => throw failure;
            // with a guard: stops, and rolls everything back
            var stopped = Assert.Throws<ImportException>(() => RunDirect(world, session, broken, new ImportOptions { SpaceGuard = new RecordingGuard((c, n) => true) }));
            Assert.Equal(CaptureFailureKind.LibraryFull, stopped.Kind, name + ": " + stopped.Message);
            Assert.Equal(0L, session.Read(r => r.Long("SELECT count(*) FROM snapshot")), name + ": nothing was published");
            Assert.Equal(0L, session.Read(r => r.Long("SELECT count(*) FROM name")), name + ": nothing remained");
            // with nobody consulting the numbers (no guard, no recorder) an unreadable length is not a reason to refuse a save
            var published = RunDirect(world, session, broken, null);
            Assert.Equal(1L, published.SnapshotId, name + ": without a consumer the import publishes");
            session.TestOnlyShutdown();
        }
    }

    private static ImportResult RunDirect(World world, LibrarySession session, SnapshotImporter.FileLengths? lengths, ImportOptions? options)
    {
        var captureId = session.NewCaptureId();
        using var prepare = World.Lease(session, MutationKind.Prepare, captureId);
        var attempt = session.RecordAttemptStartAsync(prepare, new AttemptStart(new byte[16], null, Utf16.ToBytes(@"D:\Media"), null, "direct", 1)).GetAwaiter().GetResult();
        using var save = prepare.HandOffToObservation().HandOffToSave(out _);
        using var writer = LibraryDatabase.OpenWriter(save, session.Interlock, world.Main, "T-IMPORT", [MutationKind.Save], true, null);
        var rows = new SyntheticSnapshot(1500, seed: 5);
        return SnapshotImporter.Run(writer, save, session.SessionToken, attempt, SyntheticSnapshot.NewSource(), rows.Header("direct"), rows, options, lengths, CancellationToken.None);
    }

    // ---------------------------------------------------------------- the footprint query

    [Test]
    public static void The_dictionary_footprint_of_a_source_is_read_before_the_import_and_is_zero_for_a_new_source()
    {
        var world = World.Create();
        var session = world.CreatedSession();
        var first = Imports.Synthetic(session, new SyntheticSnapshot(400, seed: 1), "run-1");
        var other = Imports.Synthetic(session, new SyntheticSnapshot(300, seed: 2, label: "o"), "run-2", SyntheticSnapshot.NewSource(@"\Other", 0x9999));

        var footprint = session.ReadDictionaryFootprintAsync(first.SourceId).GetAwaiter().GetResult();
        Assert.Equal(session.Read(r => r.Long($"SELECT count(*) FROM name WHERE source_id = {first.SourceId}")), footprint.Names, "E_n");
        Assert.Equal(session.Read(r => r.Long($"SELECT sum(length(utf16)) FROM name WHERE source_id = {first.SourceId}")), footprint.NameBytes, "E_b: the sum of the names' UTF-16 bytes");
        Assert.Equal(session.Read(r => r.Long($"SELECT count(*) FROM folder_path WHERE source_id = {first.SourceId}")), footprint.FolderPaths, "E_p");
        Assert.True(footprint.Names > 0 && footprint.NameBytes > 0 && footprint.FolderPaths > 0);
        var otherFootprint = session.ReadDictionaryFootprintAsync(other.SourceId).GetAwaiter().GetResult();
        Assert.True(otherFootprint.Names != footprint.Names || otherFootprint.NameBytes != footprint.NameBytes, "each source has its own footprint");

        Assert.Equal(new DictionaryFootprint(0, 0, 0), session.ReadDictionaryFootprintAsync(424242).GetAwaiter().GetResult(), "a source that does not exist yet (a new source) has an empty dictionary");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => session.ReadDictionaryFootprintAsync(first.SourceId, cancelled.Token).GetAwaiter().GetResult());
        session.TestOnlyShutdown();
    }
}

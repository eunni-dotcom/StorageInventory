using System.Diagnostics;
using StorageInventory.Testing;

namespace StorageInventory.Library.Tests;

/// <summary>
/// C4R-M01: the checks of T-IMPORT (lease, save token, space guard) must fall INSIDE one very large folder, not only between folders.
/// The importer interns the names of a folder before it inserts the folder's file rows, and that stretch used to hold no check at all
/// (a folder of 100,000 names went 0.68 s, one of 200,000 names 1.28 s, without looking at the token). Now the dictionary rows count
/// toward the 4,096-row interval of IMP-11 (3) ("inserted rows of any table"), a look at the token is forced by TIME when work goes
/// by that inserts nothing (CAN-03), and neither counter is reset by a folder boundary.
/// <para>The tests do not rely on elapsed time: <see cref="BigFolderSnapshot"/> counts the elements of the folder's file list that the
/// importer has read, so "inside the folder" is a count (while the count is at most the folder's size the importer is still interning),
/// and the time bound is tested with a fake clock that advances per element read. One limited real-time assertion remains, with a
/// wide margin, for the largest gap between two looks.</para>
/// </summary>
public static class LargeFolderTests
{
    private const int Interval = SnapshotImporter.GuardInterval;

    /// <summary>The rows inserted before the folder's names are interned: the root's own (empty) name, its folder path and its folder row.</summary>
    private const int RootRows = 3;

    private static void AssertRolledBack(World world, LibrarySession session, Imports.Footprint before, string label)
    {
        // the failed import's own attempt (recorded by T0 before the import) is still InProgress: T-OUTCOME is the caller's
        Assert.Equal(before with { InProgress = before.InProgress + 1 }, Imports.Footprint.Of(world, session), label + ": the Library is exactly as it was before the import");
        Assert.True(World.Length(world.Journal) <= 0, label + ": the journal is 0 bytes after the rollback");
        Assert.False(session.Interlock.IsFaulted, label + ": not a fault");
    }

    private sealed class StoppingGuard(int stopAtRowCheck) : ISpaceGuard
    {
        private int _rowChecks;

        public bool Permit(in SpaceCheck check) => check.Kind != SpaceCheckKind.Rows || ++_rowChecks < stopAtRowCheck;
    }

    // ------------------------------------------------------------------ the row cadence inside one folder

    [Test]
    public static void The_checks_of_a_folder_of_100000_and_of_200000_new_names_fall_every_4096_rows_inside_the_folder_while_it_is_still_being_interned()
    {
        foreach (var names in new[] { 100_000, 200_000 })
        {
            var world = World.Create();
            var session = world.CreatedSession();
            var snapshot = new BigFolderSnapshot(names);
            var checks = new List<(SpaceCheck Check, long Reads)>();
            var options = new ImportOptions { OnSpaceCheck = c => checks.Add((c, snapshot.Reads)) };
            var result = Imports.Run(session, snapshot, snapshot.Header("big"), "big", SyntheticSnapshot.NewSource(), options);

            // while the importer interns, every name it reads is one new row of `name`: the rows inserted are the root's three plus the
            // names read so far, and the folder's file rows have not begun
            var inside = checks.Where(c => c.Check.Kind == SpaceCheckKind.Rows && c.Check.RowsInserted <= RootRows + names).ToList();
            var expected = (RootRows + names) / Interval;
            Assert.Equal(expected, inside.Count, $"{names} names: a check after every {Interval} rows of the interning ({expected} of them, before the first file row)");
            for (var j = 0; j < inside.Count; j++)
            {
                Assert.Equal((j + 1L) * Interval, inside[j].Check.RowsInserted, $"{names} names: check {j + 1} falls at exactly {(j + 1) * Interval} rows");
                Assert.Equal(inside[j].Check.RowsInserted - RootRows, inside[j].Reads, $"{names} names: at check {j + 1} the importer had read only {inside[j].Check.RowsInserted - RootRows} names: it was INSIDE the interning");
                Assert.True(inside[j].Reads < names, $"{names} names: check {j + 1} fell before the folder was fully interned");
            }
            Assert.True(inside.Count >= 24, "a folder of 100,000 names or more has at least 24 checks inside its interning");

            // and the rest of the import is the same cadence: names, then file rows, every 4,096 rows of any table
            var totalRows = RootRows + 2L * names + 1;   // the root's three, a name and a file row per file, the one extension total
            Assert.Equal(totalRows, checks[^1].Check.RowsInserted, $"{names} names: the final check came after every row of every table");
            Assert.Equal(1 + (int)(totalRows / Interval) + 1, result.Checks!.SpaceChecks, $"{names} names: BEGIN, a row check per {Interval} rows, the final check");
            Assert.True(result.Checks.TokenObservations >= result.Checks.SpaceChecks, "every check looked at the save token");
            Assert.Null(session.Read(r => SnapshotVerifier.VerifyPublished(r, result.SnapshotId)), $"{names} names: the snapshot verifies");
            Assert.True(result.Checks.MaxTokenGap < TimeSpan.FromSeconds(0.5), $"{names} names: no two looks at the save token were 0.5 s apart (CAN-01d): {result.Checks.MaxTokenGap}");
            session.TestOnlyShutdown();
        }
    }

    [Test]
    public static void A_cancellation_requested_during_the_interning_of_a_big_folder_is_observed_inside_the_folder_and_rolls_back()
    {
        const int Names = 200_000;
        var world = World.Create();
        var session = world.CreatedSession();
        Imports.Synthetic(session, new SyntheticSnapshot(300, seed: 1), "older");
        var before = Imports.Footprint.Of(world, session);
        using var cancel = new CancellationTokenSource();
        var snapshot = new BigFolderSnapshot(Names);
        var rowChecks = 0;
        long readsWhenCancelled = 0;
        var options = new ImportOptions
        {
            Probe = p =>
            {
                if (p != ImportPoint.AfterRowCheck || ++rowChecks != 3) return;
                readsWhenCancelled = snapshot.Reads;
                cancel.Cancel();   // right after the third row check, inside the folder's interning
            },
        };
        Assert.Throws<OperationCanceledException>(() => Imports.Run(session, snapshot, snapshot.Header("big"), "big", SyntheticSnapshot.NewSource(@"\Big", 0x4242), options, cancel.Token));
        Assert.True(readsWhenCancelled > 0 && readsWhenCancelled < Names, "the cancellation was requested inside the interning: " + readsWhenCancelled + " of " + Names + " names read");
        Assert.True(snapshot.Reads > readsWhenCancelled, "the importer went on after the request, to its next look");
        Assert.True(snapshot.Reads - readsWhenCancelled <= Interval, $"and saw the cancellation within {Interval} rows of work, not at the end of the folder: {snapshot.Reads - readsWhenCancelled} more names read");
        Assert.True(snapshot.Reads < Names, "before the folder was fully interned (the old behaviour read every name first)");
        AssertRolledBack(world, session, before, "a cancellation inside the interning");
        // the Library is usable afterwards: the same import, not cancelled, publishes with the next snapshot id
        var retry = Imports.Run(session, new BigFolderSnapshot(2_000), new BigFolderSnapshot(2_000).Header("retry"), "retry", SyntheticSnapshot.NewSource(@"\Big", 0x4242));
        Assert.Equal(2L, retry.SnapshotId, "the cancelled import consumed no snapshot id");
        session.TestOnlyShutdown();
    }

    [Test]
    public static void The_space_guard_is_consulted_inside_a_big_folder_and_a_stop_there_rolls_back_before_any_file_row()
    {
        const int Names = 100_000;
        var world = World.Create();
        var session = world.CreatedSession();
        Imports.Synthetic(session, new SyntheticSnapshot(300, seed: 1), "older");
        var before = Imports.Footprint.Of(world, session);
        var snapshot = new BigFolderSnapshot(Names);
        var seen = new List<(SpaceCheck Check, long Reads)>();
        var options = new ImportOptions { SpaceGuard = new StoppingGuard(stopAtRowCheck: 3), OnSpaceCheck = c => seen.Add((c, snapshot.Reads)) };
        var failure = Assert.Throws<ImportException>(() => Imports.Run(session, snapshot, snapshot.Header("big"), "big", SyntheticSnapshot.NewSource(@"\Big", 0x4242), options));
        Assert.Equal(CaptureFailureKind.LibraryFull, failure.Kind, failure.Message);
        var third = seen.Where(s => s.Check.Kind == SpaceCheckKind.Rows).ElementAt(2);
        Assert.Equal(3L * Interval, third.Check.RowsInserted, "the guard stopped the import at its third row check, 3 x 4,096 rows in");
        Assert.Equal(3L * Interval - RootRows, third.Reads, "at which point only that many names had been interned: the stop came INSIDE the folder");
        Assert.Equal(third.Reads, snapshot.Reads, "and the importer read no further name after the guard said stop");
        Assert.True(snapshot.Reads < Names / 4, "far short of the folder's size");
        AssertRolledBack(world, session, before, "a guard stop inside the interning");
        session.TestOnlyShutdown();
    }

    // ------------------------------------------------------------------ the time bound

    [Test]
    public static void A_stretch_that_inserts_no_row_is_still_looked_at_by_time_with_a_fake_clock()
    {
        // The names of the second import are all in the source's dictionary already: interning them is a look-up per name and inserts
        // no row, so the row cadence has nothing to count. Only the time bound (CAN-03) can force a look at the token inside the folder.
        const int Names = 2_000;
        const int CancelAtRead = 500;
        var tick = Stopwatch.Frequency / 100;   // each element the importer reads takes 10 ms on the fake clock

        (long Reads, int Checks) CancelledAt500(long stepTicks)
        {
            var world = World.Create();
            var session = world.CreatedSession();
            var first = new BigFolderSnapshot(Names, "same");
            var existing = Imports.Run(session, first, first.Header("first"), "first", SyntheticSnapshot.NewSource(@"\Big", 0x4242));
            var before = Imports.Footprint.Of(world, session);
            using var cancel = new CancellationTokenSource();
            var snapshot = new BigFolderSnapshot(Names, "same", stepTicks, read => { if (read == CancelAtRead) cancel.Cancel(); });
            var checks = new List<SpaceCheck>();
            var options = new ImportOptions { Clock = snapshot.Clock, OnSpaceCheck = checks.Add };
            Assert.Throws<OperationCanceledException>(() => Imports.Run(session, snapshot, snapshot.Header("second"), "second", new ImportSourceSpec.Existing(existing.SourceId), options, cancel.Token));
            AssertRolledBack(world, session, before, "a cancellation inside a stretch that inserts nothing");
            session.TestOnlyShutdown();
            return (snapshot.Reads, checks.Count);
        }

        // 10 ms per element: every 64th unit of work reads the clock, finds more than 100 ms gone and looks at the token
        var (reads, checkCount) = CancelledAt500(tick);
        Assert.Equal(1, checkCount, "the BEGIN check only: no row check fell during the interning (the names were look-ups, not inserted rows)");
        Assert.True(reads >= CancelAtRead, "the cancellation had been requested when the importer looked");
        Assert.True(reads <= CancelAtRead + SnapshotImporter.ClockPoll, $"and the importer saw it within {SnapshotImporter.ClockPoll} units of work, by time: {reads} elements read (cancelled at {CancelAtRead})");
        Assert.True(reads < Names, "inside the interning, not after it");

        // the control: with a clock that never advances nothing is due by time and nothing counts as rows, so the first look is after the
        // rows (the importer read every name for interning and every file for insertion)
        var (standing, _) = CancelledAt500(0);
        Assert.Equal(2L * Names, standing, "with a standing clock the first look came after the folder's rows: the bound is a time bound");
    }

    [Test]
    public static void A_folder_boundary_resets_neither_counter_so_many_small_folders_are_looked_at_by_time_too()
    {
        // 400 folders of 4 files each, all already in the source's dictionary: no name or path row is inserted, and the 2,000 rows that are
        // (folder and file rows) never reach the 4,096-row interval. A counter that restarted at each folder (a boundary that postpones the
        // look) would never reach its threshold: no look at all for 3,200 element reads, 32 s on this fake clock.
        const int Folders = 400, PerFolder = 4, CancelAtRead = 500;
        var tick = Stopwatch.Frequency / 100;   // 10 ms per element read
        var world = World.Create();
        var session = world.CreatedSession();
        var first = new BigFolderSnapshot(PerFolder, "same", folders: Folders);
        var existing = Imports.Run(session, first, first.Header("first"), "first", SyntheticSnapshot.NewSource(@"\Big", 0x4242));
        var before = Imports.Footprint.Of(world, session);
        using var cancel = new CancellationTokenSource();
        var snapshot = new BigFolderSnapshot(PerFolder, "same", tick, read => { if (read == CancelAtRead) cancel.Cancel(); }, Folders);
        var checks = new List<SpaceCheck>();
        var options = new ImportOptions { Clock = snapshot.Clock, OnSpaceCheck = checks.Add };
        Assert.Throws<OperationCanceledException>(() => Imports.Run(session, snapshot, snapshot.Header("second"), "second", new ImportSourceSpec.Existing(existing.SourceId), options, cancel.Token));
        Assert.Equal(1, checks.Count, "no row check fell: the folders hold too few rows for the interval");
        Assert.True(snapshot.Reads >= CancelAtRead, "the cancellation had been requested when the importer looked");
        Assert.True(snapshot.Reads <= CancelAtRead + 2 * SnapshotImporter.ClockPoll, $"and the importer saw it within {2 * SnapshotImporter.ClockPoll} elements of work across folder boundaries: {snapshot.Reads} read (cancelled at {CancelAtRead})");
        Assert.True(snapshot.Reads < Folders * PerFolder, "inside the file loop, not after it");
        AssertRolledBack(world, session, before, "a cancellation across many small folders");
        session.TestOnlyShutdown();
    }

    [Test]
    public static void Names_already_in_the_dictionary_are_work_not_inserted_rows_so_the_row_cadence_is_unchanged_for_a_re_import()
    {
        const int Names = 5_000;
        var world = World.Create();
        var session = world.CreatedSession();
        var first = new BigFolderSnapshot(Names, "same");
        var existing = Imports.Run(session, first, first.Header("first"), "first", SyntheticSnapshot.NewSource(@"\Big", 0x4242));
        var checks = new List<SpaceCheck>();
        var second = new BigFolderSnapshot(Names, "same");
        var result = Imports.Run(session, second, second.Header("second"), "second", new ImportSourceSpec.Existing(existing.SourceId), new ImportOptions { OnSpaceCheck = checks.Add });
        Assert.Equal(0L, result.NewNames, "every name was already in the source's dictionary");
        // the rows inserted: the folder row and the file rows and the one extension total; no name row, no folder-path row
        Assert.Equal(1L + Names + 1, checks[^1].RowsInserted, "look-ups and found folder paths are not inserted rows");
        Assert.Equal(1 + (1 + Names + 1) / Interval + 1, result.Checks!.SpaceChecks, "BEGIN, a row check per 4,096 inserted rows, the final check");
        session.TestOnlyShutdown();
    }

    [Test]
    public static void A_small_import_makes_no_check_beyond_BEGIN_and_the_final_one_and_the_clock_forces_none()
    {
        var world = World.Create();
        var session = world.CreatedSession();
        var checks = new List<SpaceCheck>();
        var result = Imports.Synthetic(session, new SyntheticSnapshot(40, seed: 1), "small", options: new ImportOptions { OnSpaceCheck = checks.Add });
        Assert.Equal(2, result.Checks!.SpaceChecks, "BEGIN and the final check: a small import is unaffected");
        Assert.Equal(SpaceCheckKind.Begin, checks[0].Kind);
        Assert.Equal(SpaceCheckKind.Final, checks[1].Kind);
        session.TestOnlyShutdown();
    }
}

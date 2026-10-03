using StorageInventory.Testing;

namespace StorageInventory.Library.Tests;

/// <summary>
/// TEST-L9, C4's part, closed in full (C4R-M02, D-R3): the inputs the import primitive gives the space guard at EVERY check are exact and
/// read at the check, and an input that cannot be read stops the import as <c>LibraryFull</c> at every kind of check. The cases the review
/// found implied rather than tested are here: an unreadable <c>page_count</c> and an unreadable page size at <c>BEGIN</c>, at a periodic check
/// and at the final check; <c>Λ</c> as a pure 64-bit function (values above 2^31 and 2^32 bytes, the clamp at 0, out-of-range inputs); and the
/// periodic checks' inputs pinned against independent readings (the main file's and the journal's lengths through a second handle, the
/// page count and page size against the file and the engine). The rule itself (free space, margin, reserve) is C5's; its exact-reserve,
/// one-byte-below and external-consumption cases are run here against a TEST-ONLY reference of IMP-11's rule, which shows the primitive hands
/// a guard everything the rule needs and rolls back as the rule says. <see cref="SpaceGuardTests"/> holds the stop, throw, token and
/// COMMIT write-out cases; <c>ImportTests</c> the engine's own <c>SQLITE_FULL</c>.
/// </summary>
public static class SpaceInputsTests
{
    /// <summary>A real <c>SqliteException</c> (the engine's own error type, which the test project does not reference at compile time),
    /// as a failing <c>PRAGMA</c> would raise it: SQLITE_IOERR.</summary>
    private static Exception EngineIoError() => (Exception)Activator.CreateInstance(
        Type.GetType("Microsoft.Data.Sqlite.SqliteException, Microsoft.Data.Sqlite", throwOnError: true)!, "disk I/O error", 10)!;

    private static long HandleLength(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1);
            return stream.Length;
        }
        catch (FileNotFoundException)
        {
            return 0;
        }
    }

    private sealed class Permitting : ISpaceGuard
    {
        internal List<SpaceCheck> Checks { get; } = [];

        public bool Permit(in SpaceCheck check)
        {
            Checks.Add(check);
            return true;
        }
    }

    private static (World World, LibrarySession Session, long SourceId) WithOlderSnapshot(LibrarySessionOptions? options = null)
    {
        var world = World.Create();
        var session = world.CreatedSession(options);
        var first = Imports.Synthetic(session, new SyntheticSnapshot(300, seed: 1), "older");
        return (world, session, first.SourceId);
    }

    // ------------------------------------------------------------------ Λ as a pure function

    [Test]
    public static void Pending_growth_is_a_pure_64_bit_function_with_the_clamp_and_out_of_range_inputs_refused()
    {
        // above 2^31 and 2^32 bytes: an int would wrap or overflow
        Assert.Equal(1L << 31, SnapshotImporter.PendingGrowth(1L << 19, 4096, 0), "2^19 pages of 4 KiB = 2^31 bytes");
        Assert.Equal(1L << 32, SnapshotImporter.PendingGrowth(1L << 20, 4096, 0), "2^20 pages of 4 KiB = 2^32 bytes");
        Assert.Equal((1L << 33) - (1L << 32), SnapshotImporter.PendingGrowth(1L << 21, 4096, 1L << 32), "2^33 bytes of image against a 2^32-byte file");
        Assert.Equal(4_294_967_294L * 65_536L, SnapshotImporter.PendingGrowth(4_294_967_294L, 65_536, 0), "the largest legal image: 2^32 - 2 pages of 64 KiB");
        Assert.Equal(4_294_967_294L * 65_536L - 5_000_000_000L, SnapshotImporter.PendingGrowth(4_294_967_294L, 65_536, 5_000_000_000L), "a main file of 5 GB");
        // the clamp: the file already holds the whole image (nothing pending), or is longer than it (a freed tail)
        Assert.Equal(0L, SnapshotImporter.PendingGrowth(100, 4096, 409_600), "the file equals the image: nothing pending");
        Assert.Equal(1L, SnapshotImporter.PendingGrowth(100, 4096, 409_599), "one byte short: one byte pending");
        Assert.Equal(0L, SnapshotImporter.PendingGrowth(100, 4096, 409_601), "the file is one byte longer than the image: clamped to 0, not -1");
        Assert.Equal(0L, SnapshotImporter.PendingGrowth(1, 512, 1L << 40), "a file much longer than the image: clamped to 0");
        // inputs that cannot be real: the check fails closed (the importer reads FormatException and OverflowException as unreadable)
        Assert.Throws<FormatException>(() => SnapshotImporter.PendingGrowth(-1, 4096, 0));
        Assert.Throws<FormatException>(() => SnapshotImporter.PendingGrowth(10, 0, 0));
        Assert.Throws<FormatException>(() => SnapshotImporter.PendingGrowth(10, -4096, 0));
        Assert.Throws<FormatException>(() => SnapshotImporter.PendingGrowth(10, 4096, -1));
        Assert.Throws<OverflowException>(() => SnapshotImporter.PendingGrowth(long.MaxValue, 2, 0));
    }

    // ------------------------------------------------------------------ an input that cannot be read, at every kind of check

    [Test]
    public static void An_unreadable_page_count_or_page_size_stops_the_import_as_LibraryFull_at_BEGIN_at_a_periodic_check_and_at_the_final_check()
    {
        var cases = new List<(string Name, EngineInput Input, SpaceCheckKind Kind, Exception Failure)>();
        foreach (var input in new[] { EngineInput.PageCount, EngineInput.PageSize })
        {
            foreach (var kind in new[] { SpaceCheckKind.Begin, SpaceCheckKind.Rows, SpaceCheckKind.Final })
            {
                cases.Add(($"{input} at {kind}: the engine fails", input, kind, EngineIoError()));
            }
        }
        cases.Add(("page_count at a periodic check: a value that is not a number", EngineInput.PageCount, SpaceCheckKind.Rows, new FormatException("not a number")));
        cases.Add(("page_count at the final check: a value of the wrong type", EngineInput.PageCount, SpaceCheckKind.Final, new InvalidCastException("not an integer")));
        cases.Add(("page_size at BEGIN: a value that overflows", EngineInput.PageSize, SpaceCheckKind.Begin, new OverflowException("too large")));

        foreach (var (name, input, kind, failure) in cases)
        {
            var (world, session, sourceId) = WithOlderSnapshot();
            var before = Imports.Footprint.Of(world, session);
            var guard = new Permitting();
            var failed = false;
            var options = new ImportOptions
            {
                SpaceGuard = guard,
                OnEngineInput = (what, at, value) =>
                {
                    if (what == input && at == kind && !failed) { failed = true; throw failure; }
                    return value;
                },
            };
            var snapshot = new SyntheticSnapshot(2500, seed: 2, label: "t");   // BEGIN, several row checks, the final check
            var stopped = Assert.Throws<ImportException>(() => Imports.Synthetic(session, snapshot, "second", new ImportSourceSpec.Existing(sourceId), options));
            Assert.True(failed, name + ": the seam was reached");
            Assert.Equal(CaptureFailureKind.LibraryFull, stopped.Kind, name + ": " + stopped.Message);
            Assert.Equal(failure, stopped.InnerException, name + ": the cause is kept");
            // not asked at the check that could not be judged, and not asked at any later check
            Assert.True(guard.Checks.All(c => (int)c.Kind < (int)kind), name + ": the guard was not consulted at or after the check whose input could not be read");
            // rolled back exactly (the failed import's own attempt, recorded by T0 before it, is still InProgress)
            Assert.Equal(before with { InProgress = before.InProgress + 1 }, Imports.Footprint.Of(world, session), name + ": the Library is exactly as before");
            Assert.True(World.Length(world.Journal) <= 0, name + ": the journal is 0 bytes");
            Assert.Null(session.Read(r => SnapshotVerifier.VerifyPublished(r, 1)), name + ": the older snapshot verifies");
            Assert.False(session.Interlock.IsFaulted, name + ": LibraryFull is class A");
            session.TestOnlyShutdown();
        }
    }

    [Test]
    public static void An_unreadable_engine_input_is_fatal_whenever_something_consults_the_numbers_a_recorder_included_and_ignored_when_nobody_does()
    {
        // a recorder alone (no guard) consults the numbers: the same refusal
        {
            var (world, session, sourceId) = WithOlderSnapshot();
            var options = new ImportOptions { OnSpaceCheck = _ => { }, OnEngineInput = (what, at, value) => what == EngineInput.PageCount && at == SpaceCheckKind.Rows ? throw EngineIoError() : value };
            var stopped = Assert.Throws<ImportException>(() => Imports.Synthetic(session, new SyntheticSnapshot(2500, seed: 2, label: "t"), "second", new ImportSourceSpec.Existing(sourceId), options));
            Assert.Equal(CaptureFailureKind.LibraryFull, stopped.Kind, stopped.Message);
            session.TestOnlyShutdown();
        }
        // nobody consults the numbers (no guard, no recorder): documented and pinned. C5 must always wire a guard (review observation O07).
        {
            var (world, session, sourceId) = WithOlderSnapshot();
            var reached = 0;
            var options = new ImportOptions { OnEngineInput = (what, at, value) => { reached++; throw EngineIoError(); } };
            var result = Imports.Synthetic(session, new SyntheticSnapshot(2500, seed: 2, label: "t"), "second", new ImportSourceSpec.Existing(sourceId), options);
            Assert.True(reached > 0 && result.SnapshotId == 2, "the inputs were read, could not be, and the import published: nobody asked for the numbers");
            session.TestOnlyShutdown();
        }
    }

    [Test]
    public static void A_lease_violation_while_an_input_is_read_is_not_an_unreadable_input_and_is_never_reported_as_LibraryFull()
    {
        var (world, session, sourceId) = WithOlderSnapshot();
        var options = new ImportOptions
        {
            SpaceGuard = new Permitting(),
            OnEngineInput = (what, at, value) => what == EngineInput.PageCount && at == SpaceCheckKind.Rows ? throw new LeaseViolationException("a lease violation in the middle of a check") : value,
        };
        // IsUnreadable excludes it: it passes through as itself (the interlock's own checks fault the interlock; a free-space excuse would hide it)
        var failure = Assert.Throws<LeaseViolationException>(() => Imports.Synthetic(session, new SyntheticSnapshot(2500, seed: 2, label: "t"), "second", new ImportSourceSpec.Existing(sourceId), options));
        Assert.Contains("middle of a check", failure.Message);
        session.TestOnlyShutdown();
    }

    // ------------------------------------------------------------------ the periodic checks' inputs, against independent readings

    [Test]
    public static void The_inputs_of_every_check_equal_independent_readings_and_the_final_check_matches_the_file_COMMIT_wrote()
    {
        var (world, session, sourceId) = WithOlderSnapshot();
        var checks = new List<SpaceCheck>();
        var truth = new List<(long Main, long Journal)>();
        var options = new ImportOptions
        {
            // at the instant of the check nothing writes between the primitive's reading and this callback, so a second handle must agree
            OnSpaceCheck = c => { checks.Add(c); truth.Add((HandleLength(world.Main), HandleLength(world.Journal))); },
        };
        var result = Imports.Synthetic(session, new SyntheticSnapshot(2500, seed: 2, label: "t"), "second", new ImportSourceSpec.Existing(sourceId), options);

        Assert.Equal(SpaceCheckKind.Begin, checks[0].Kind);
        Assert.Equal(SpaceCheckKind.Final, checks[^1].Kind);
        Assert.True(checks.Count >= 6, $"BEGIN, at least four row checks and the final check: {checks.Count}");
        for (var i = 0; i < checks.Count; i++)
        {
            var c = checks[i];
            var label = $"check {i + 1} ({c.Kind})";
            Assert.Equal(truth[i].Main, c.MainFileLength, label + ": the main file's length equals a second handle's");
            Assert.Equal(truth[i].Journal, c.JournalLength, label + ": the journal's length equals a second handle's");
            Assert.Equal(4096L, c.PageSize, label + ": the page size");
            Assert.True(c.PageCount * c.PageSize >= c.MainFileLength, label + ": the file never holds more than the image, so Λ needs no clamp here");
            Assert.Equal(c.PageCount * c.PageSize - c.MainFileLength, c.PendingGrowth, label + ": Λ = page_count × page size − the main file's length");
            if (i > 0)
            {
                Assert.True(c.RowsInserted > checks[i - 1].RowsInserted || c.Kind == SpaceCheckKind.Final, label + ": the rows count grows from check to check");
                Assert.True(c.PageCount >= checks[i - 1].PageCount, label + ": page_count never shrinks in an append-shaped import");
                Assert.True(c.JournalLength >= checks[i - 1].JournalLength, label + ": the journal only grows during the transaction");
            }
        }
        var rowChecks = checks.Where(c => c.Kind == SpaceCheckKind.Rows).ToList();
        Assert.True(checks[0].PageCount < rowChecks[0].PageCount, "page_count was read at the first periodic check, not carried over from BEGIN");
        for (var i = 1; i < rowChecks.Count; i++) Assert.True(rowChecks[i].PageCount > rowChecks[i - 1].PageCount, $"row check {i + 1}: 4,096 more rows hold more pages, so page_count was read again");
        Assert.True(rowChecks.All(c => c.MainFileLength > 0), "a main file of a Library with an older snapshot is never empty");
        Assert.True(rowChecks.All(c => c.JournalLength > 0), "the transaction has journalled pages by its first periodic check");

        // the final check: COMMIT extends the main file to the image the final check saw, and the engine agrees after the commit
        var final = checks[^1];
        Assert.Equal(final.PageCount * final.PageSize, HandleLength(world.Main), "after COMMIT the main file is exactly page_count × page size of the final check");
        Assert.Equal(final.PageCount, Convert.ToInt64(session.Read(r => r.Scalar(OpenSql.GetPageCount))), "a reader's page_count after the commit is the final check's");
        Assert.Equal(final.PendingGrowth, result.Checks!.CommitGrowth, "and COMMIT grew the file by exactly the final check's Λ");
    }

    // ------------------------------------------------------------------ the rule's cases, against a test-only reference of IMP-11

    /// <summary>IMP-11 (3) as the specification states it, in test code: at a periodic check <c>R_k = M_L + Λ_k + I_L</c> with
    /// <c>I_L = 2 × max(16 MiB, D_k)</c> and <c>D_k</c> the drop in free space since the previous check; at the final check
    /// <c>R_f = M_L + Λ_f + R_C</c>; equality continues. The free space comes from a provider the test scripts, which is handed the
    /// requirement WITHOUT a drop (the figure a steady volume must meet) so that "exactly the reserve" and "one byte below" can be stated.
    /// A steady volume is non-decreasing here, so D_k is 0 and the rule's own requirement equals that figure. C5 builds the product rule;
    /// this shows the primitive supplies it everything it needs and rolls back as the rule says.</summary>
    private sealed class ReferenceRule(Func<int, SpaceCheck, long, long> freeSpace) : ISpaceGuard
    {
        internal const long Margin = 256L << 20;
        internal const long CommitReserve = 1L << 20;
        internal const long IntervalFloor = 16L << 20;

        internal List<(SpaceCheck Check, long Free, long Required)> Log { get; } = [];

        public bool Permit(in SpaceCheck check)
        {
            var allowance = check.Kind == SpaceCheckKind.Final ? CommitReserve : 2 * IntervalFloor;
            var steady = Margin + check.PendingGrowth + allowance;
            var free = freeSpace(Log.Count + 1, check, steady);   // may throw: the free-space query failed
            var previous = Log.Count == 0 ? free : Log[^1].Free;
            var required = check.Kind == SpaceCheckKind.Final
                ? steady
                : Margin + check.PendingGrowth + 2 * Math.Max(IntervalFloor, Math.Max(0, previous - free));
            Log.Add((check, free, required));
            return free >= required;
        }
    }

    private static ImportResult RunWithRule(LibrarySession session, long sourceId, ReferenceRule rule) =>
        Imports.Synthetic(session, new SyntheticSnapshot(2500, seed: 2, label: "t"), "second", new ImportSourceSpec.Existing(sourceId), new ImportOptions { SpaceGuard = rule });

    [Test]
    public static void Exactly_the_reserve_continues_and_one_byte_below_it_stops_at_that_check_and_rolls_back()
    {
        // exactly the reserve at every check: equality continues, the import publishes
        int total;
        {
            var (world, session, sourceId) = WithOlderSnapshot();
            var rule = new ReferenceRule((n, c, steady) => steady);
            var result = RunWithRule(session, sourceId, rule);
            Assert.Equal(2L, result.SnapshotId, "free space equal to the required reserve at every check: equality continues");
            Assert.True(rule.Log.Count >= 6 && rule.Log.All(l => l.Free == l.Required), "the rule was consulted at every check, always at exactly the reserve");
            for (var i = 1; i < rule.Log.Count - 1; i++) Assert.True(rule.Log[i].Check.PendingGrowth > rule.Log[i - 1].Check.PendingGrowth, "Λ grows from check to check (no spill), so a steady volume is a non-decreasing one");
            total = rule.Log.Count;
            session.TestOnlyShutdown();
        }
        // one byte below at the k-th check: stopped AT that check, never asked again, rolled back as LibraryFull
        foreach (var k in new[] { 1, 2, 4, total })   // 1 = BEGIN, total = the final check
        {
            var (world, session, sourceId) = WithOlderSnapshot();
            var before = Imports.Footprint.Of(world, session);
            var rule = new ReferenceRule((n, c, steady) => n == k ? steady - 1 : steady);
            var stopped = Assert.Throws<ImportException>(() => RunWithRule(session, sourceId, rule));
            Assert.Equal(CaptureFailureKind.LibraryFull, stopped.Kind, $"one byte below at check {k}: " + stopped.Message);
            Assert.Equal(k, rule.Log.Count, $"the rule was asked at checks 1..{k} and not after the one that stopped");
            Assert.Equal(rule.Log[^1].Required - 1, rule.Log[^1].Free, "free space was exactly one byte below the reserve at the check that stopped");
            Assert.Equal(k == total ? SpaceCheckKind.Final : k == 1 ? SpaceCheckKind.Begin : SpaceCheckKind.Rows, rule.Log[^1].Check.Kind, "the kind of the check that stopped");
            Assert.Equal(before with { InProgress = before.InProgress + 1 }, Imports.Footprint.Of(world, session), $"one byte below at check {k}: rolled back exactly");
            Assert.True(World.Length(world.Journal) <= 0, "the journal is 0 bytes");
            Assert.False(session.Interlock.IsFaulted, "class A");
            session.TestOnlyShutdown();
        }
    }

    [Test]
    public static void Consumption_by_another_program_between_two_checks_is_seen_at_the_next_check_through_the_drop_in_free_space()
    {
        // 100 GiB free at the first checks, then another program takes 90 GiB between check 2 and check 3: the drop D_k = 90 GiB raises the
        // interval allowance to 180 GiB, which the remaining 10 GiB cannot cover, although 10 GiB is far above the 256 MiB margin
        var (world, session, sourceId) = WithOlderSnapshot();
        var before = Imports.Footprint.Of(world, session);
        const long Gib = 1L << 30;
        var rule = new ReferenceRule((n, c, steady) => n < 3 ? 100 * Gib : 10 * Gib);
        var stopped = Assert.Throws<ImportException>(() => RunWithRule(session, sourceId, rule));
        Assert.Equal(CaptureFailureKind.LibraryFull, stopped.Kind, stopped.Message);
        Assert.Equal(3, rule.Log.Count, "consumption between check 2 and check 3 was seen by check 3 and by no check before it");
        Assert.True(rule.Log[2].Free > ReferenceRule.Margin * 20, "free space was still far above the margin: only the drop term could stop the import");
        Assert.Equal(before with { InProgress = before.InProgress + 1 }, Imports.Footprint.Of(world, session), "rolled back exactly");
        session.TestOnlyShutdown();
    }

    [Test]
    public static void A_failing_free_space_query_inside_the_rule_stops_the_import_at_that_check_as_LibraryFull()
    {
        var (world, session, sourceId) = WithOlderSnapshot();
        var before = Imports.Footprint.Of(world, session);
        var rule = new ReferenceRule((n, c, steady) => n == 3 ? throw new IOException("the free space could not be read") : steady);
        var stopped = Assert.Throws<ImportException>(() => RunWithRule(session, sourceId, rule));
        Assert.Equal(CaptureFailureKind.LibraryFull, stopped.Kind, stopped.Message);
        Assert.Equal(2, rule.Log.Count, "the query failed at the third check, which therefore logged nothing");
        Assert.Equal(before with { InProgress = before.InProgress + 1 }, Imports.Footprint.Of(world, session), "rolled back exactly");
        session.TestOnlyShutdown();
    }

    // ------------------------------------------------------------------ the engine's own SQLITE_FULL, despite a guard that permits everything

    [Test]
    public static void A_genuine_SQLITE_FULL_despite_a_guard_that_permits_everything_rolls_back_as_LibraryFull_without_damage()
    {
        var faults = new LibraryFaultInjection { LimitDatabaseToTwoThousandPages = true };
        var (world, session, sourceId) = WithOlderSnapshot(new LibrarySessionOptions { Faults = faults });
        var before = Imports.Footprint.Of(world, session);
        var guard = new Permitting();
        var failure = Assert.Throws<ImportException>(() => Imports.Synthetic(session, new SyntheticSnapshot(40_000, seed: 2, label: "big"), "second", new ImportSourceSpec.Existing(sourceId), new ImportOptions { SpaceGuard = guard }));
        Assert.Equal(CaptureFailureKind.LibraryFull, failure.Kind, "the engine's SQLITE_FULL is LibraryFull: " + failure.Message);
        Assert.True(failure.InnerException?.GetType().Name == "SqliteException" && failure.InnerException.Message.Contains("full", StringComparison.OrdinalIgnoreCase), "and it is the engine's own failure, not the guard's: " + failure.InnerException);
        Assert.True(guard.Checks.Count >= 2 && guard.Checks.All(c => c.Kind != SpaceCheckKind.Final), "the guard was consulted (BEGIN and periodic checks) and permitted everything; the import never reached its final check");
        Assert.Equal(before with { InProgress = before.InProgress + 1 }, Imports.Footprint.Of(world, session), "rolled back exactly");
        Assert.True(World.Length(world.Journal) <= 0, "the journal is 0 bytes");
        Assert.Null(session.Read(r => SnapshotVerifier.VerifyPublished(r, 1)), "the older snapshot verifies");
        session.TestOnlyShutdown();
    }
}

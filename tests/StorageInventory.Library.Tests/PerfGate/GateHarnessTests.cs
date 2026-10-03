using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using StorageInventory.Testing;

namespace StorageInventory.Library.Tests.PerfGate;

/// <summary>
/// The gate harness at small scale (~12,000 files): the run record's fields, the attribution binding, the four cancel points (rolled back,
/// asserted) and the cancellation after the final check (CAN-01e), the kill at the journal's peak with the timed start-up open, the prefill
/// cache key, the plan, and the negative control's artefacts. Nothing here judges a budget.
/// </summary>
public static class GateHarnessTests
{
    private const double Scale = 0.006;       // F(2M, 25%, system) at 12,000 files
    private const string CellId = "F-2M-25-system";

    private static readonly BinaryRecord Binary = new("test", false, new string('0', 64), "test");

    /// <summary>One prefill per process (building it takes seconds), shared by every test of the class.</summary>
    private static readonly Lazy<string> PrefillFile = new(() =>
    {
        var path = Path.Combine(World.RunRoot, "gate-prefill-" + Guid.NewGuid().ToString("N")[..6], "main.sqlite3");
        var code = GateRunner.Prefill(["--cell", CellId, "--scale", Scale.ToString(CultureInfo.InvariantCulture), "--out", path]);
        Assert.Equal(0, code, "the prefill built");
        return path;
    });

    private static RunRecord Run(string mode, string? label = null, string? analysisDir = null)
    {
        GateRunner.RaisePriority = false;
        GateRunner.Quiet = true;
        var spec = GateMatrix.Find(CellId).Scaled(Scale);
        var options = new RunOptions(spec, Scale, mode, PrefillFile.Value, null, analysisDir ?? Path.Combine(World.RunRoot, "analysis"), label ?? ("test-" + mode.Replace(':', '-')), Binary);
        return GateRunner.RunCell(options);
    }

    [Test]
    public static void TimedRunRecordsEveryRequiredQuantity()
    {
        var r = Run("timed");
        Assert.Equal("Published", r.Outcome, "the save published");
        Assert.Equal("timed", r.Kind, "kind");
        Assert.Equal("Representative", r.Cell.Class, "the class is computed from the cell's parameters");
        Assert.True(r.Files > 8_000 && r.FilesPerSecond > 0 && r.ImportSeconds > 0, "rows/s, duration and rows");
        Assert.True(r.Generated is { Distinct: > 0, Files: > 0 }, "the generator's realised statistics before the import");
        Assert.True(r.Library.BeforeBytes > 0 && r.Library.GrowthBytes > 0 && r.Library.PageSize == 4096, "Library size before (through a handle) and growth");
        Assert.True(r.Phases is { FilesMs: > 0, VerificationMs: > 0, CommitMs: > 0 }, "phase times");
        var imp = r.Imp11!;
        Assert.True(imp.SpaceChecks >= 4 && imp.Checks.Count == imp.SpaceChecks, "every IMP-11 check is recorded");
        Assert.True(imp.Checks.All(c => c.Length == 8), "page_count, page size, main and journal lengths, Λ and the free space at each check");
        Assert.Equal((long)SpaceCheckKind.Begin, imp.Checks[0][0], "the first check is BEGIN");
        Assert.Equal((long)SpaceCheckKind.Final, imp.Checks[^1][0], "the last check is the final check");
        Assert.True(imp.CommitGrowthEqualsFinalPending, $"COMMIT's growth ({imp.CommitGrowth}) equals the final check's Λ ({imp.FinalPendingGrowth})");
        Assert.Equal(imp.Checks[^1][5], r.Journal!.LengthAtFinalCheck, "the journal's length through a live handle immediately before COMMIT is the final check's");
        Assert.Equal(r.Journal.LengthAtFinalCheck, r.Journal.PeakJournalBytesReported, "the importer reports the same peak");
        Assert.True(r.Journal.Samples > 0 && r.Journal.LargestSamplingIntervalMs > 0, "the sampled series and its largest interval");
        Assert.True(imp.Checks.All(c => c[7] > 0), "the volume's free space was read at every check");
        Assert.True(r.Token is { Observations: > 0 } && r.Token.MaxGapSeconds < 0.5, "the token was looked at at least every 0.5 s: " + r.Token!.MaxGapSeconds.ToString(CultureInfo.InvariantCulture));
        Assert.True(r.Memory!.PeakWorkingSetBytes > 0, "peak working set");
        Assert.Null(r.Attribution, "a timed run takes no copies (they would perturb its timing)");
        Assert.Equal(0, RecordJson.Validate(RecordJson.Serialize(r)).Count, "the record is fit as raw evidence: " + string.Join("; ", RecordJson.Validate(RecordJson.Serialize(r))));
    }

    [Test]
    public static void AttributionRunBindsTheCopiesToTheRecord()
    {
        var dir = Path.Combine(World.RunRoot, "analysis-" + Guid.NewGuid().ToString("N")[..6]);
        var r = Run("attribution", "test-attribution", dir);
        var a = r.Attribution!;
        Assert.Equal("new", a.Target.Source, "F is a first save into a NEW source");
        Assert.Null(a.Target.SourceId, "a new source has no id");
        Assert.Equal("persource", a.Layout, "the production layout");
        var journal = Path.Combine(dir, a.JournalCopy);
        var database = Path.Combine(dir, a.DatabaseCopy);
        Assert.Equal(new FileInfo(journal).Length, a.JournalCopyLength, "the journal copy's length");
        Assert.Equal(r.Imp11!.Checks[^1][5], a.JournalLengthBeforeCommit, "the journal's length read through the live handle immediately before COMMIT");
        Assert.Equal(a.JournalLengthBeforeCommit, a.JournalCopyLength, "the copy is as long as the journal was");
        Assert.Equal(new FileInfo(database).Length, a.DatabaseCopyLength, "the database copy's length");
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(database))).ToLowerInvariant(), a.DatabaseCopySha256, "the SHA-256 recorded when the copy was made is the copy's");
        Assert.Equal(r.Library.BeforeBytes, a.DatabaseCopyLength, "the copy was taken after T0, before BEGIN: it is the Library as it was before the import");
        Assert.True(a.DatabaseCopySha256.Length == 64, "a hash");
        var json = RecordJson.Serialize(r);
        Assert.Equal(0, RecordJson.Validate(json).Count, "fit: " + string.Join("; ", RecordJson.Validate(json)));
        // every one of the mandatory attribution fields is required by the validation (the checker refuses a record without any of them)
        foreach (var field in new[] { "target", "layout", "journalLengthBeforeCommit", "journalCopy", "journalCopyLength", "databaseCopy", "databaseCopyLength", "databaseCopySha256" })
        {
            var doc = System.Text.Json.Nodes.JsonNode.Parse(json)!;
            doc["attribution"]!.AsObject().Remove(field);
            var problems = RecordJson.Validate(doc.ToJsonString());
            Assert.True(problems.Any(p => p.Contains(field, StringComparison.Ordinal)), $"removing attribution.{field} is refused: " + string.Join("; ", problems));
        }
        var existing = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        existing["attribution"]!["target"] = System.Text.Json.Nodes.JsonNode.Parse("""{"source":"existing"}""");
        Assert.True(RecordJson.Validate(existing.ToJsonString()).Any(p => p.Contains("sourceId", StringComparison.Ordinal)), "an existing target needs its id");
    }

    [Test]
    public static void AttributionFieldsAgreeWithThePythonChecker()
    {
        // the C# record and tests/perf/attribute_run.py name the same mandatory fields (read from the script's text; Python is not run here)
        var script = File.ReadAllText(Path.Combine(WorkloadClassTests.FindRepoRoot(), "tests", "perf", "attribute_run.py"));
        var match = Regex.Match(script, @"ATTRIBUTION_FIELDS = \{(?<names>[^}]*)\}", RegexOptions.Singleline);
        Assert.True(match.Success, "ATTRIBUTION_FIELDS found in attribute_run.py");
        var python = Regex.Matches(match.Groups["names"].Value, "'([A-Za-z0-9]+)'").Select(m => m.Groups[1].Value).Order(StringComparer.Ordinal).ToArray();
        var record = JsonSerializer.Serialize(new AttributionRecord(new TargetRecord("new", null), "persource", 1, "j", 1, "d", 1, new string('a', 64)), RecordJson.Options);
        var csharp = JsonDocument.Parse(record).RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.SequenceEqual(python, csharp, "the attribution fields");
        var target = Regex.Match(script, @"TARGET_FIELDS = \{(?<names>[^}]*)\}", RegexOptions.Singleline).Groups["names"].Value;
        Assert.SequenceEqual(Regex.Matches(target, "'([A-Za-z0-9]+)'").Select(m => m.Groups[1].Value).Order(StringComparer.Ordinal), ["source", "sourceId"], "the target fields");
    }

    [Test]
    public static void RecordValidationRefusesAnUnfitRecord()
    {
        var good = RecordJson.Serialize(Run("timed", "test-validate"));
        Assert.Equal(0, RecordJson.Validate(good).Count, "baseline");
        foreach (var top in new[] { "kind", "label", "binary", "cell", "operationStartUtc", "operationSeconds", "outcome", "library", "imp11", "token", "journal", "memory", "phases", "importSeconds", "filesPerSecond", "generated" })
        {
            var doc = System.Text.Json.Nodes.JsonNode.Parse(good)!;
            doc.AsObject().Remove(top);
            Assert.True(RecordJson.Validate(doc.ToJsonString()).Count > 0, "a timed record without " + top + " is refused");
        }
        Assert.True(RecordJson.Validate("{}").Count > 5, "an empty object");
        Assert.Equal("not JSON", RecordJson.Validate("nope")[0][..8], "text");
        var doc2 = System.Text.Json.Nodes.JsonNode.Parse(good)!;
        doc2["schema"] = 99;
        Assert.True(RecordJson.Validate(doc2.ToJsonString()).Any(p => p.Contains("schema", StringComparison.Ordinal)), "an unknown schema");
        var doc3 = System.Text.Json.Nodes.JsonNode.Parse(good)!;
        doc3["imp11"]!["checks"] = System.Text.Json.Nodes.JsonNode.Parse("[[1,0,1,1,1,1,1,1]]");
        Assert.True(RecordJson.Validate(doc3.ToJsonString()).Any(p => p.Contains("fewer than two", StringComparison.Ordinal)), "a record of a single check");
    }

    [Test]
    public static void CancelPointsRollBackAndAreTimed()
    {
        long before = -1;
        foreach (var point in new[] { 1, 2, 3, 4 })
        {
            var r = Run("cancel:" + point, "test-cancel-" + point);
            var c = r.Cancel!;
            Assert.Equal(point, c.Point, "the point");
            Assert.True(c.Requested, $"point {point}: the cancellation was requested");
            Assert.Equal("Cancelled (rolled back)", c.Outcome, $"point {point} ({c.PointName}): the import observed the cancellation");
            Assert.True(c.CancelToReturnSeconds > 0 && c.CancelToReturnSeconds < 5, $"point {point} timed from the cancellation to the return: {c.CancelToReturnSeconds}");
            var rb = c.Rollback!;
            Assert.True(rb.RolledBack, $"point {point} rolled back: {rb.Problem}");
            Assert.Equal(0L, rb.JournalLengthAfter, "the journal is 0 bytes");
            Assert.Equal(rb.MainLengthBefore, rb.MainLengthAfter, "the main file is back to its length at BEGIN");
            Assert.True(rb.OlderSnapshotsVerify && rb.SnapshotsAfter == 3 && rb.NamesAfter == r.Library.NamesBefore, "no snapshot, no row and no name of the import remains; the older snapshots verify");
            Assert.True(c.JournalAtCancelBytes > 0, "the journal held the transaction's undo records at the cancel");
            if (point == 1) Assert.True(c.RowsAtCancel >= 8_000, "point 1 is late in row insertion: " + c.RowsAtCancel);
            Assert.Equal("Cancelled (rolled back)", r.Outcome, "a cancelled run did not publish");
            if (before >= 0) Assert.Equal(before, r.Library.BeforeBytes, "every run starts from the same Library");
            before = r.Library.BeforeBytes;
            Assert.Equal(0, RecordJson.Validate(RecordJson.Serialize(r)).Count, "the cancel record is fit: " + string.Join("; ", RecordJson.Validate(RecordJson.Serialize(r))));
        }
    }

    // ------------------------------------------------------------------------------------------------------------ C4R-M03: a run's result is never a reason to refuse its record
    // The orchestrator replaces a run only when it is INVALID (the load rule, or an environmental failure of the harness). A cancellation the
    // product did not honour (the save published) and a save that failed early are MEASURED results: the child must write their records and
    // exit 0, so that the orchestrator judges them (MISSED or STOP) and never replaces them. Exit 3 is for a record that cannot be interpreted.

    private static T With<T>(GateRunner.CancelBehaviour behaviour, ISpaceGuard? guard, Func<T> body)
    {
        (GateRunner.TestCancelBehaviour, GateRunner.TestSpaceGuard) = (behaviour, guard);
        try { return body(); }
        finally { (GateRunner.TestCancelBehaviour, GateRunner.TestSpaceGuard) = (GateRunner.CancelBehaviour.Cancel, null); }
    }

    /// <summary>The child as the orchestrator starts it (<c>--benchmark gate run</c>), in this process: its exit code and what it wrote to stderr.</summary>
    private static (int Code, string Error) RunChild(string mode, string label, string? analysisDir = null)
    {
        GateRunner.RaisePriority = false;
        GateRunner.Quiet = true;
        var original = Console.Error;
        var error = new StringWriter();
        Console.SetError(error);
        try
        {
            string[] args = ["run", "--cell", CellId, "--mode", mode, "--scale", Scale.ToString(CultureInfo.InvariantCulture), "--prefill-from", PrefillFile.Value,
                "--analysis-dir", analysisDir ?? Path.Combine(World.RunRoot, "analysis"), "--label", label, "--binary-commit", "test"];
            return (GateBenchmark.Run(args), error.ToString());
        }
        finally
        {
            Console.SetError(original);
        }
    }

    private sealed class RefuseAtBegin : ISpaceGuard
    {
        public bool Permit(in SpaceCheck check) => check.Kind != SpaceCheckKind.Begin;
    }

    [Test]
    public static void ACancellationTheSaveDidNotHonourIsAMeasuredRecordNotAnUnfitOne()
    {
        // the save never sees the cancellation (the harness records the request and does not cancel the token): it publishes
        var (code, error) = With(GateRunner.CancelBehaviour.RequestWithoutCancelling, null, () => RunChild("cancel:3", "test-published-exit"));
        Assert.Equal(0, code, "the child exits 0 for a run whose save published despite the cancellation (exit 3 would have the orchestrator replace it): " + error);
        foreach (var point in new[] { 1, 2, 3, 4 })
        {
            var r = With(GateRunner.CancelBehaviour.RequestWithoutCancelling, null, () => Run("cancel:" + point, "test-published-" + point));
            var c = r.Cancel!;
            Assert.Equal("Published", r.Outcome, $"point {point}: the save published");
            Assert.Equal("Published", c.Outcome, $"point {point}: the cancel record says so");
            Assert.True(c.Requested, $"point {point}: the cancellation WAS requested");
            Assert.Null(c.Rollback, $"point {point}: nothing was rolled back, so there is no rollback to assert");
            Assert.True(c.CancelToReturnSeconds > 0 && c.RowsAtCancel > 0, $"point {point}: the request was timed and its rows counted");
            Assert.False(c.PublishedAfterCancel, "CAN-01e is the property of the after-final run, not of a cancel point");
            var problems = RecordJson.Validate(RecordJson.Serialize(r));
            Assert.Equal(0, problems.Count, $"point {point}: a valid, measured record of a miss is fit as raw evidence: " + string.Join("; ", problems));
        }
    }

    [Test]
    public static void ACancelPointThatWasNeverReachedIsRecordedAsNeverRequested()
    {
        var r = With(GateRunner.CancelBehaviour.DoNotRequest, null, () => Run("cancel:2", "test-never-requested"));
        var c = r.Cancel!;
        Assert.False(c.Requested, "no cancellation was requested");
        Assert.Equal("Published", c.Outcome, "so the save published");
        Assert.Null(c.Rollback, "and there is nothing to roll back");
        Assert.Equal(0.0, c.CancelToReturnSeconds, "no request, no time");
        Assert.Equal(0, RecordJson.Validate(RecordJson.Serialize(r)).Count, "the record is fit: the orchestrator classifies it (a miss: nothing was measured about cancelling)");
    }

    [Test]
    public static void ASaveThatFailsAtBeginIsAMeasuredRecordNotAnUnfitOne()
    {
        // a space guard that refuses at BEGIN: one IMP-11 check is recorded, the save fails, nothing is published
        var (code, error) = With(GateRunner.CancelBehaviour.Cancel, new RefuseAtBegin(), () => RunChild("timed", "test-failed-at-begin-exit"));
        Assert.Equal(0, code, "the child exits 0 for a save that failed at BEGIN: " + error);
        var timed = With(GateRunner.CancelBehaviour.Cancel, new RefuseAtBegin(), () => Run("timed", "test-failed-at-begin"));
        Assert.True(timed.Outcome.StartsWith("Failed:", StringComparison.Ordinal), "the save failed: " + timed.Outcome);
        Assert.Equal(1, timed.Imp11!.SpaceChecks, "only BEGIN's check ran");
        var problems = RecordJson.Validate(RecordJson.Serialize(timed));
        Assert.Equal(0, problems.Count, "a failed save with fewer than two checks is a valid, measured record: " + string.Join("; ", problems));
        var dir = Path.Combine(World.RunRoot, "analysis-failed-" + Guid.NewGuid().ToString("N")[..6]);
        var attribution = With(GateRunner.CancelBehaviour.Cancel, new RefuseAtBegin(), () => Run("attribution", "test-attribution-failed-at-begin", dir));
        Assert.True(attribution.Outcome.StartsWith("Failed:", StringComparison.Ordinal), "the attributed save failed too: " + attribution.Outcome);
        Assert.Equal(0, RecordJson.Validate(RecordJson.Serialize(attribution)).Count, "its record is fit: " + string.Join("; ", RecordJson.Validate(RecordJson.Serialize(attribution))));
    }

    [Test]
    public static void ValidationAcceptsMeasuredFailuresAndRefusesWhatCannotBeInterpreted()
    {
        // the three classes of a cancel run: (i) expected (cancelled, rolled back, asserted), (ii) a miss (the save published, or the point was never reached),
        // (iii) a record that cannot be interpreted. Only (iii) is exit 3, and only (iii) is replaced by the orchestrator.
        var honoured = RecordJson.Serialize(Run("cancel:2", "test-validate-cancel"));
        Assert.Equal(0, RecordJson.Validate(honoured).Count, "(i) an expected cancellation is fit");
        JsonNode Doc(string json) => JsonNode.Parse(json)!;
        bool Refused(JsonNode doc, string text) => RecordJson.Validate(doc.ToJsonString()).Any(p => p.Contains(text, StringComparison.Ordinal));

        var published = Doc(honoured);
        published["outcome"] = "Published";
        published["cancel"]!["outcome"] = "Published";
        published["cancel"]!["rollback"] = null;
        Assert.Equal(0, RecordJson.Validate(published.ToJsonString()).Count, "(ii) a cancel run whose save published carries no rollback and is fit");
        var noRollback = Doc(honoured);
        noRollback["cancel"]!["rollback"] = null;
        Assert.True(Refused(noRollback, "cancel.rollback"), "(iii) a run that says it was cancelled and rolled back must carry its rollback assertion");
        var failedCancel = Doc(honoured);
        failedCancel["cancel"]!["outcome"] = "Failed: LibraryFull";
        failedCancel["cancel"]!["rollback"] = null;
        Assert.True(Refused(failedCancel, "cancel.rollback"), "(iii) a cancel run whose save failed (it did not publish) is asserted rolled back too");
        foreach (var field in new[] { "requested", "outcome", "point", "cancelToReturnSeconds" })
        {
            var doc = Doc(honoured);
            doc["cancel"]!.AsObject().Remove(field);
            Assert.True(Refused(doc, "cancel." + field), $"(iii) a cancel record without cancel.{field} is unfit");
        }

        var timed = RecordJson.Serialize(Run("timed", "test-validate-timed"));
        var earlyFailure = Doc(timed);
        earlyFailure["outcome"] = "Failed: LibraryFull";
        earlyFailure["imp11"]!["checks"] = JsonNode.Parse("[[1,0,1,1,1,1,1,1]]");
        Assert.Equal(0, RecordJson.Validate(earlyFailure.ToJsonString()).Count, "(ii) a save that failed at BEGIN (one check) is fit");
        earlyFailure["imp11"]!["checks"] = JsonNode.Parse("[]");
        Assert.Equal(0, RecordJson.Validate(earlyFailure.ToJsonString()).Count, "(ii) so is one that failed before any check");
        var publishedOneCheck = Doc(timed);
        publishedOneCheck["imp11"]!["checks"] = JsonNode.Parse("[[1,0,1,1,1,1,1,1]]");
        Assert.True(Refused(publishedOneCheck, "fewer than two checks"), "(iii) a PUBLISHED save made BEGIN's check and the final one: one check is unfit");
        var failedWithoutImp11 = Doc(timed);
        failedWithoutImp11["outcome"] = "Failed: LibraryFull";
        failedWithoutImp11.AsObject().Remove("imp11");
        Assert.True(Refused(failedWithoutImp11, "imp11"), "(iii) a failed save still has to say what it measured");

        // the exit codes
        var cancelRecord = RecordJson.Deserialize(honoured)!;
        Assert.Equal(0, GateBenchmark.ExitCodeOf("cancel:2", cancelRecord, out _), "(i) exit 0");
        var publishedRecord = cancelRecord with { Outcome = "Published", Cancel = cancelRecord.Cancel! with { Outcome = "Published", Rollback = null } };
        Assert.Equal(0, GateBenchmark.ExitCodeOf("cancel:2", publishedRecord, out var none), "(ii) exit 0 for a published cancel run: " + string.Join("; ", none));
        var neverRequested = publishedRecord with { Cancel = publishedRecord.Cancel! with { Requested = false, CancelToReturnSeconds = 0 } };
        Assert.Equal(0, GateBenchmark.ExitCodeOf("cancel:2", neverRequested, out _), "(ii) exit 0 for a cancel point that was never reached");
        var unfit = cancelRecord with { Cancel = cancelRecord.Cancel! with { Rollback = null } };
        Assert.Equal(3, GateBenchmark.ExitCodeOf("cancel:2", unfit, out var why), "(iii) exit 3 for a record that cannot be interpreted");
        Assert.True(why.Any(p => p.Contains("cancel.rollback", StringComparison.Ordinal)), "...and it says why");
        var timedRecord = RecordJson.Deserialize(timed)!;
        Assert.Equal(0, GateBenchmark.ExitCodeOf("timed", timedRecord, out _), "a published timed run: exit 0");
        var timedUnfit = timedRecord with { Imp11 = null };
        Assert.Equal(3, GateBenchmark.ExitCodeOf("timed", timedUnfit, out _), "a timed record without its IMP-11 checks cannot be interpreted: exit 3");
        Assert.Equal(0, GateBenchmark.ExitCodeOf("crash", timedUnfit, out _), "a crash run's record is judged by the orchestrator with its recovery");
    }

    [Test]
    public static void CancellationAfterTheFinalCheckPublishes()
    {
        var r = Run("cancel-after-final", "test-after-final");
        Assert.Equal("Published", r.Outcome, "CAN-01e: a cancellation after the final check is not observed and the save publishes");
        Assert.True(r.Cancel!.PublishedAfterCancel, "recorded");
        Assert.True(r.Imp11!.CommitGrowthEqualsFinalPending, "and COMMIT wrote exactly the final check's Λ");
    }

    [Test]
    public static void KillAtTheJournalsPeakAndTimedRecovery()
    {
        // process level: the child kills itself at the start of the verification; the next start-up open (a fresh process) is timed
        var args = new[] { "--benchmark", "gate", "run", "--cell", CellId, "--mode", "crash", "--scale", Scale.ToString(CultureInfo.InvariantCulture), "--prefill-from", PrefillFile.Value, "--label", "test-crash", "--binary-commit", "test" };
        var (code, lines, error) = GateSupport.RunSelf(args);
        Assert.True(code != 0, "the child was killed, not finished: exit " + code + " " + error);
        var json = GateSupport.LastJson(lines);
        Assert.NotNull(json, "the child printed its record before it was killed");
        var record = RecordJson.Deserialize(json!)!;
        Assert.Equal("crash", record.Kind, "kind");
        Assert.Equal("Killed", record.Outcome, "outcome");
        Assert.True(record.JournalAtKillBytes > 0, "the journal held the transaction's undo records at the kill");
        var directory = record.KilledLibraryDirectory!;
        try
        {
            Assert.Equal(record.JournalAtKillBytes, GateSupport.HandleLengthOrZero(Path.Combine(directory, LibraryNames.JournalFile)), "the hot journal is still there");
            var (rcode, rlines, rerror) = GateSupport.RunSelf(["--benchmark", "gate", "recover", "--dir", directory, "--appdata", record.KilledAppData!, "--snapshots", record.Library.SnapshotsBefore.ToString(CultureInfo.InvariantCulture)]);
            Assert.Equal(0, rcode, "the recovery child ran: " + rerror);
            var recovery = JsonSerializer.Deserialize<RecoveryRecord>(GateSupport.LastJson(rlines)!, RecordJson.Options)!;
            Assert.Equal("Available", recovery.State, "the next open recovered the Library");
            Assert.Null(recovery.Problem, "no problem recorded: " + recovery.Problem);
            Assert.True(recovery.OlderSnapshotsVerify && recovery.Snapshots == 3, "the older snapshots verify and the killed import left nothing");
            Assert.Equal(0L, recovery.JournalAfterBytes, "the hot journal was rolled back and truncated");
            Assert.Equal(record.Library.BeforeBytes, recovery.MainAfterBytes, "the main file is back to its length at BEGIN");
            Assert.True(recovery.OpenSeconds > 0 && recovery.OpenSeconds < 30, "the open was timed: " + recovery.OpenSeconds);
        }
        finally
        {
            GateSupport.TryDelete(Path.GetDirectoryName(record.KilledAppData!)!);
        }
    }

    [Test]
    public static void PrefillCacheKeyNamesEveryInput()
    {
        var prefill = new PrefillSpec("obj", 2_000_000, "d=0.25;model=system;beta=13.501;omega=0.25");
        var key = BinaryIdentity.PrefillKey(prefill, Binary);
        Assert.Equal(key, BinaryIdentity.PrefillKey(prefill with { }, Binary with { }), "the same inputs give the same key");
        var variants = new Dictionary<string, string>
        {
            ["family"] = BinaryIdentity.PrefillKey(prefill with { Family = "hash" }, Binary),
            ["size"] = BinaryIdentity.PrefillKey(prefill with { PerSnapshot = 1_000_000 }, Binary),
            ["parameters (d)"] = BinaryIdentity.PrefillKey(prefill with { Params = "d=0.6;model=system;beta=13.501;omega=0.25" }, Binary),
            ["parameters (model)"] = BinaryIdentity.PrefillKey(prefill with { Params = "d=0.25;model=data;beta=2.558;omega=0.25" }, Binary),
            ["parameters (omega)"] = BinaryIdentity.PrefillKey(prefill with { Params = "d=0.25;model=system;beta=13.501;omega=0.5" }, Binary),
            ["binary commit"] = BinaryIdentity.PrefillKey(prefill, Binary with { Commit = "other" }),
            ["binary dirty flag"] = BinaryIdentity.PrefillKey(prefill, Binary with { Dirty = true }),
            ["binary build output"] = BinaryIdentity.PrefillKey(prefill, Binary with { OutputHash = new string('1', 64) }),
        };
        foreach (var (what, other) in variants) Assert.True(other != key, $"a change of the {what} changes the key");
        Assert.True(variants.Values.Distinct().Count() == variants.Count, "and the keys differ from each other");
        Assert.True(key.Contains("g" + GateConstants.GeneratorVersion, StringComparison.Ordinal) && key.Contains("persource", StringComparison.Ordinal), "the generator version and the schema variant are in the key: " + key);
        // the build output hash follows the files of the output
        var dir = Path.Combine(World.RunRoot, "output-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.dll"), "one");
        var h1 = BinaryIdentity.OutputHash(dir);
        File.WriteAllText(Path.Combine(dir, "a.dll"), "two");
        var h2 = BinaryIdentity.OutputHash(dir);
        File.WriteAllText(Path.Combine(dir, "b.json"), "{}");
        Assert.True(h1 != h2 && h2 != BinaryIdentity.OutputHash(dir), "a changed or added file changes the output hash");
        Assert.Equal(BinaryIdentity.OutputHash(AppContext.BaseDirectory), BinaryIdentity.OutputHash(AppContext.BaseDirectory), "the hash is stable");
    }

    [Test]
    public static void PlanListsTheMatrixWithItsClassesAndBudgets()
    {
        var original = Console.Out;
        var text = new StringWriter();
        Console.SetOut(text);
        try { Assert.Equal(0, GateBenchmark.Run(["plan", "--scale", "0.025", "--binary-commit", "abc"]), "plan"); }
        finally { Console.SetOut(original); }
        using var doc = JsonDocument.Parse(text.ToString().Trim());
        var cells = doc.RootElement.GetProperty("cells").EnumerateArray().ToList();
        Assert.Equal(16, cells.Count, "the matrix");
        foreach (var c in cells)
        {
            Assert.Equal(c.GetProperty("statedClass").GetString(), c.GetProperty("class").GetString(), c.GetProperty("id").GetString());
            var kinds = c.GetProperty("kinds").EnumerateArray().Select(k => k.GetString()!).ToList();
            Assert.True(kinds.Contains("timed") && kinds.Contains("attribution"), "every cell is timed and attributed");
            var informational = c.GetProperty("class").GetString() == "Informational";
            Assert.Equal(!informational, kinds.Contains("crash") && kinds.Contains("cancel:4"), "cancellation and recovery in every cell that has a class budget");
        }
        var f2 = cells.Single(c => c.GetProperty("id").GetString() == "F-2M-25-system");
        Assert.Equal(50_000L, f2.GetProperty("targetFiles").GetInt64(), "scaled target");
        Assert.Equal(2_000_000L, f2.GetProperty("nominal").GetProperty("files").GetInt64(), "the nominal cell is not scaled");
        Assert.True(doc.RootElement.GetProperty("binary").GetProperty("outputHash").GetString()!.Length == 64, "the binary's identity");
    }

    [Test]
    public static void NegativeControlBuildsRealJournalArtefacts()
    {
        foreach (var variant in new[] { "new", "existing" })
        {
            var dir = Path.Combine(World.RunRoot, "control-" + variant + "-" + Guid.NewGuid().ToString("N")[..6]);
            var json = NegativeControl.Build(dir, variant, "control-" + variant, namesPerSource: 2_500, targetNames: 1_000);
            using var doc = JsonDocument.Parse(json);
            var a = doc.RootElement.GetProperty("attribution");
            Assert.Equal("control", doc.RootElement.GetProperty("kind").GetString(), "kind");
            Assert.Equal("global", a.GetProperty("layout").GetString(), "the Library-wide layout is declared");
            Assert.Equal(variant == "new" ? "new" : "existing", a.GetProperty("target").GetProperty("source").GetString(), "target");
            var journal = Path.Combine(dir, a.GetProperty("journalCopy").GetString()!);
            var database = Path.Combine(dir, a.GetProperty("databaseCopy").GetString()!);
            Assert.Equal(new FileInfo(journal).Length, a.GetProperty("journalLengthBeforeCommit").GetInt64(), "the live journal was copied whole");
            Assert.True(new FileInfo(journal).Length > 4096 * 10, "a real rollback journal");
            var bytes = File.ReadAllBytes(journal);
            Assert.True(bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 0xd9, 0xd5, 0x05, 0xf9, 0x20, 0xa1, 0x63, 0xd7 }) || bytes.AsSpan(0, 12).IndexOfAnyExcept((byte)0) < 0, "SQLite's magic, or the zero form of a segment not synced yet");
            Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(database))).ToLowerInvariant(), a.GetProperty("databaseCopySha256").GetString(), "the copy's SHA-256");
            Assert.Equal(0x53494E56, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(File.ReadAllBytes(database).AsSpan(68, 4)), "application_id");
        }
    }
}

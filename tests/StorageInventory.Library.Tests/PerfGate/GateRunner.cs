using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using StorageInventory.Core;
using StorageInventory.History.Library;

namespace StorageInventory.Library.Tests.PerfGate;

/// <summary>What the run of one cell needs (the arguments of <c>--benchmark gate run</c>).</summary>
/// <param name="Cell">The cell, at the scale given.</param>
/// <param name="Scale">The scale (1.0 for the real gate).</param>
/// <param name="Mode">timed, attribution, cancel:1 to cancel:4, cancel-after-final, crash or delete.</param>
/// <param name="PrefillFrom">The cached prefill (a Library main file).</param>
/// <param name="StatsFile">The cached generator statistics of the target, or null to compute them in this process.</param>
/// <param name="AnalysisDir">Where attribution runs write their copies.</param>
/// <param name="Label">The run's label.</param>
/// <param name="Binary">The binary's identity.</param>
internal sealed record RunOptions(CellSpec Cell, double Scale, string Mode, string PrefillFrom, string? StatsFile, string AnalysisDir, string Label, BinaryRecord Binary)
{
    internal bool Attribution => Mode == "attribution";

    internal int CancelPoint => Mode.StartsWith("cancel:", StringComparison.Ordinal) ? int.Parse(Mode[7..], CultureInfo.InvariantCulture) : 0;
}

/// <summary>A target the harness imports: its rows, its header and its source.</summary>
internal sealed class TargetBundle
{
    internal required ISnapshotRowSource Rows { get; init; }
    internal required Func<string, ImportSnapshotHeader> Header { get; init; }
    internal required long Files { get; init; }
    internal required ImportSourceSpec Source { get; init; }
    internal required bool NewSource { get; init; }
    internal required Func<GeneratedRecord> Describe { get; init; }
}

/// <summary>
/// The measuring child of TEST-P1 (§15.4): one run of one cell, in a fresh process, over a copy of the cell's prefilled Library, with
/// the shipped importer and its real hooks (<see cref="ImportOptions.OnSpaceCheck"/>, <see cref="ImportOptions.Probe"/>). It builds
/// the run record and nothing else; the orchestrator (tests/perf/perf_session.py) owns the session, the load and the validity.
/// </summary>
internal static class GateRunner
{
    // ------------------------------------------------------------------------------------------------------------ targets and prefills

    internal static TargetBundle BuildTarget(CellSpec spec)
    {
        var existing = new ImportSourceSpec.Existing(1);
        switch (spec.RunFamily)
        {
            case "obj":
            {
                var obj = new ObjSnapshot((int)CellSpec.Folders(spec.TargetFiles), treeSeed: 4, ObjParams.Parse(spec.Params), vocabSeed: 4);
                return new TargetBundle { Rows = obj, Header = obj.Header, Files = obj.Files, Source = SyntheticSnapshot.NewSource(@"\Third", serial: 0x3333ABCD), NewSource = true, Describe = () => DescribeObj(obj) };
            }
            case "objrescan":
            {
                var obj = new ObjSnapshot((int)CellSpec.Folders(spec.PrefillPerSnapshot), treeSeed: 1, ObjParams.Parse(spec.Params), vocabSeed: 1, layer: 4);
                return new TargetBundle { Rows = obj, Header = obj.Header, Files = obj.Files, Source = existing, NewSource = false, Describe = () => DescribeObj(obj) };
            }
            case "rescan":
            {
                var d = new DrSnapshot((int)CellSpec.Folders(spec.PrefillPerSnapshot), seed: 1, label: "target", "rescan", churn: spec.Churn, churnSeed: 4);
                return new TargetBundle { Rows = d, Header = d.Header, Files = d.Files, Source = existing, NewSource = false, Describe = () => DescribeDr(d, d.CountChurned()) };
            }
            case "hash" or "mixed" or "append":
            {
                var d = new DrSnapshot((int)CellSpec.Folders(spec.TargetFiles), seed: 4, label: "target", spec.RunFamily);
                return new TargetBundle { Rows = d, Header = d.Header, Files = d.Files, Source = spec.NewSource ? SyntheticSnapshot.NewSource(@"\Third", serial: 0x3333ABCD) : existing, NewSource = spec.NewSource, Describe = () => DescribeDr(d, 0) };
            }
            default:
                throw new ArgumentException("unknown run family " + spec.RunFamily);
        }
    }

    private static GeneratedRecord DescribeObj(ObjSnapshot obj)
    {
        var s = obj.Describe();
        return new GeneratedRecord(s.Files, s.Distinct, s.MeanLength, s.Once, s.Files == 0 ? 0 : (double)s.Top1 / s.Files, s.Renamed, s.Deleted, s.Added, obj.Vocabulary, "computed before the import from the generator alone");
    }

    private static GeneratedRecord DescribeDr(DrSnapshot d, long churned)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        long files = 0, length = 0;
        for (var i = 0; i < d.FolderCount; i++)
        {
            foreach (var f in d.FilesOfFolder(i))
            {
                var name = Utf16.ToString(f.Name);
                counts[name] = counts.GetValueOrDefault(name) + 1;
                files++;
                length += name.Length;
            }
        }
        var ordered = counts.Values.OrderByDescending(c => c).ToArray();
        var top1 = ordered.Take(Math.Max(1, ordered.Length / 100)).Sum(c => (long)c);
        return new GeneratedRecord(files, counts.Count, files == 0 ? 0 : (double)length / files, ordered.LongCount(c => c == 1), files == 0 ? 0 : (double)top1 / files, churned, 0, 0, 0, "computed before the import from the generator alone");
    }

    /// <summary>Imports one snapshot as a capture does (T0, hand-offs, T-IMPORT). <paramref name="afterT0"/> runs after T0 commits and
    /// before BEGIN (the pre-import copy of the attribution), and the main file is flushed after it, immediately before the operation.</summary>
    internal static (ImportResult? Result, DateTime Start, double Seconds, Exception? Failure) Import(LibrarySession session, ISnapshotRowSource rows, ImportSnapshotHeader header, string runId,
        ImportSourceSpec source, ImportOptions? options, CancellationToken cancellation, string main, Action? afterT0 = null, bool flush = true)
    {
        var captureId = session.NewCaptureId();
        using var prepare = GateSupport.Lease(session, MutationKind.Prepare, captureId);
        var attempt = session.RecordAttemptStartAsync(prepare, new AttemptStart(new byte[16], null, Utf16.ToBytes(@"D:\Media"), null, runId, DateTime.UtcNow.Ticks)).GetAwaiter().GetResult();
        afterT0?.Invoke();
        if (flush) GateSupport.FlushToDisk(main);
        using var save = prepare.HandOffToObservation().HandOffToSave(out _);
        var start = DateTime.UtcNow;
        var watch = Stopwatch.StartNew();
        try
        {
            var result = session.ImportSnapshotAsync(save, attempt, source, header, rows, options, cancellation).GetAwaiter().GetResult();
            return (result, start, watch.Elapsed.TotalSeconds, null);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ImportException)
        {
            return (null, start, watch.Elapsed.TotalSeconds, ex);
        }
    }

    /// <summary>Builds the three prefilled snapshots (§15.4's Library before: p1 and p3 of source A, p2 of source B) into the Library
    /// of <paramref name="session"/>.</summary>
    internal static void DoPrefill(LibrarySession session, string main, PrefillSpec prefill)
    {
        ImportSourceSpec? sourceA = null;
        var folders = (int)CellSpec.Folders(prefill.PerSnapshot);
        for (var i = 1; i <= 3; i++)
        {
            var other = i == 2;
            ISnapshotRowSource rows;
            Func<string, ImportSnapshotHeader> header;
            if (prefill.Family == "obj")
            {
                // p1 = source A's first save (vocabulary seed 1); p2 = source B (seed 2), sharing a fraction Omega of its entries with the target
                // first save's vocabulary (seed 4); p3 = p1 re-scanned with the routine churn (layer 3)
                var p = ObjParams.Parse(prefill.Params);
                var obj = i switch
                {
                    1 => new ObjSnapshot(folders, treeSeed: 1, p, vocabSeed: 1),
                    2 => new ObjSnapshot(folders, treeSeed: 2, p, vocabSeed: 2, sharedSeed: 4),
                    _ => new ObjSnapshot(folders, treeSeed: 1, ObjParams.Parse(prefill.Params + ";" + RoutineChurn), vocabSeed: 1, layer: 3),
                };
                rows = obj;
                header = obj.Header;
            }
            else
            {
                var d = new DrSnapshot(folders, seed: i, label: "p" + i, prefill.Family);
                rows = d;
                header = d.Header;
            }
            var spec = other ? SyntheticSnapshot.NewSource(@"\Other", serial: 0x7777ABCD) : sourceA ?? SyntheticSnapshot.NewSource();
            var (result, _, _, failure) = Import(session, rows, header("prefill-" + i), "prefill-" + i, spec, null, CancellationToken.None, main, flush: false);
            if (result is null) throw new InvalidOperationException("the prefill import " + i + " failed: " + failure?.Message);
            if (i == 1) sourceA = new ImportSourceSpec.Existing(result.SourceId);
        }
    }

    /// <summary>The routine churn of the re-scan family: the prefill's second snapshot of source A is p1 re-scanned with it (§15.4).</summary>
    internal const string RoutineChurn = "rho=0.01;delta=0.005;alpha=0.005";

    /// <summary>Builds a prefill into <paramref name="outMain"/> with a separate process of this binary (the caller starts it).</summary>
    internal static int Prefill(string[] args)
    {
        var scale = double.Parse(GateSupport.Opt(args, "scale", "1"), CultureInfo.InvariantCulture);
        var spec = GateMatrix.Find(GateSupport.Req(args, "cell")).Scaled(scale);
        var outMain = GateSupport.Req(args, "out");
        var root = Path.Combine(GateSupport.BenchRoot(), "SI-Gate-Prefill", Guid.NewGuid().ToString("N")[..10]);
        var appData = Path.Combine(root, "AppData");
        var directory = Path.Combine(appData, "Library");
        Directory.CreateDirectory(appData);
        try
        {
            var watch = Stopwatch.StartNew();
            var session = new LibrarySession(directory, appData);
            session.RunStartupOpen();
            using (var create = GateSupport.Lease(session, MutationKind.Create)) session.CreateLibrary(create);
            var main = Path.Combine(directory, LibraryNames.MainFile);
            DoPrefill(session, main, spec.Prefill);
            session.TestOnlyShutdown();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outMain))!);
            File.Copy(main, outMain, overwrite: true);
            GateSupport.FlushToDisk(outMain);
            var bytes = GateSupport.HandleLength(outMain);
            Console.WriteLine($"prefill {spec.Prefill.Family} 3x{spec.Prefill.PerSnapshot:N0} {spec.Prefill.Params} built in {watch.Elapsed.TotalSeconds:0.0} s, {bytes / 1048576.0:0.0} MiB");
            Console.WriteLine(JsonSerializer.Serialize(new { family = spec.Prefill.Family, perSnapshot = spec.Prefill.PerSnapshot, parameters = spec.Prefill.Params, generator = GateConstants.GeneratorVersion, bytes, seconds = watch.Elapsed.TotalSeconds }));
            return 0;
        }
        finally
        {
            GateSupport.TryDelete(root);
        }
    }

    /// <summary>Computes the target's realised generator statistics in a separate process, so the measuring child holds nothing but the target.</summary>
    internal static int Describe(string[] args)
    {
        var scale = double.Parse(GateSupport.Opt(args, "scale", "1"), CultureInfo.InvariantCulture);
        var spec = GateMatrix.Find(GateSupport.Req(args, "cell")).Scaled(scale);
        var outFile = GateSupport.Req(args, "out");
        var stats = BuildTarget(spec).Describe();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
        File.WriteAllText(outFile, JsonSerializer.Serialize(stats, RecordJson.Options));
        Console.WriteLine(JsonSerializer.Serialize(stats, RecordJson.Options));
        return 0;
    }

    // ------------------------------------------------------------------------------------------------------------ the run

    /// <summary>The measuring child runs at high priority, as the C4 benchmark did (no elevation needed); an in-process test turns it off.</summary>
    internal static bool RaisePriority { get; set; } = true;

    /// <summary>The importer's cancel points by number (§15.4).</summary>
    internal static readonly string[] PointNames = ["", "after a row token check", "verification start", "verification middle", "final statements"];

    /// <summary>One run of one cell. The record is returned; a <c>crash</c> run kills this process at the journal's peak instead, after
    /// printing its record.</summary>
    internal static RunRecord RunCell(RunOptions o)
    {
        var spec = o.Cell;
        if (RaisePriority) Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;   // as the C4 benchmark: no elevation needed
        var root = Path.Combine(GateSupport.BenchRoot(), "SI-Gate-Bench", Guid.NewGuid().ToString("N")[..10]);
        var appData = Path.Combine(root, "AppData");
        var directory = Path.Combine(appData, "Library");
        Directory.CreateDirectory(directory);
        var main = Path.Combine(directory, LibraryNames.MainFile);
        var journal = Path.Combine(directory, LibraryNames.JournalFile);
        var keep = false;
        try
        {
            // the prefill is copied into a fresh directory and flushed to disk (§15.4 "Cache state"); a fresh writer connection opens it
            File.Copy(o.PrefillFrom, main);
            GateSupport.FlushToDisk(main);
            var session = new LibrarySession(directory, appData);
            var status = session.RunStartupOpen();
            if (status.State != LibraryState.Available) throw new InvalidOperationException("prefill copy not available: " + status.State + " " + status.Message);

            var pageSize = session.Read(r => r.Long("PRAGMA page_size"));
            var pageCount = session.Read(r => r.Long("PRAGMA page_count"));
            var freelist = session.Read(r => r.Long("PRAGMA freelist_count"));
            var namesBefore = session.Read(r => r.Long("SELECT count(*) FROM name"));
            var nameBytes = session.Read(r => r.Long("SELECT coalesce(sum(length(utf16)), 0) FROM name"));
            var pathsBefore = session.Read(r => r.Long("SELECT count(*) FROM folder_path"));
            var snapshotsBefore = session.Read(r => r.Long("SELECT count(*) FROM snapshot"));
            var dbBefore = GateSupport.HandleLength(main);
            var before = new Before(dbBefore, pageSize, pageCount, freelist, namesBefore, nameBytes, pathsBefore, snapshotsBefore);

            var target = BuildTarget(spec);
            GeneratedRecord? generated = null;
            if (o.StatsFile is not null && File.Exists(o.StatsFile)) generated = JsonSerializer.Deserialize<GeneratedRecord>(File.ReadAllText(o.StatsFile), RecordJson.Options);
            generated ??= target.Describe();
            var header = target.Header("target");
            var cellRecord = CellRecordOf(o);

            // ---- the attribution copy: after T0 commits, before T-IMPORT's BEGIN, with the writer closed ----
            string? databaseCopyName = null;
            long databaseCopyLength = 0;
            string databaseCopySha = "";
            Action? afterT0 = null;
            if (o.Attribution)
            {
                Directory.CreateDirectory(o.AnalysisDir);
                databaseCopyName = GateSupport.Slug(o.Label) + ".before.sqlite3";
                afterT0 = () => (databaseCopyLength, databaseCopySha) = GateSupport.CopyFlushHash(main, Path.Combine(o.AnalysisDir, databaseCopyName));
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var wsBefore = Process.GetCurrentProcess().WorkingSet64;

            // ---- samplers: the journal through a live handle every millisecond ----
            var sampler = new JournalSampler(journal);
            sampler.Start();

            // ---- IMP-11: every check's exact quantities, and the free space of the volume at each ----
            var checks = new List<long[]>();
            long lastRows = 0;
            string? journalCopyName = null;
            long journalCopyLength = 0, journalLengthBeforeCommit = 0;
            using var cancel = new CancellationTokenSource();
            long cancelStamp = 0;
            var cancelPoint = o.CancelPoint;
            var cancelled = false;
            var cancelRows = 0L;
            long journalAtCancel = 0;
            string cancelPointName = cancelPoint > 0 ? PointNames[cancelPoint] : o.Mode == "cancel-after-final" ? "after the final check" : "";
            var expectedRows = (long)(0.95 * (header.Files + header.Folders));
            long journalAtKill = 0;
            var operationStart = DateTime.UtcNow;
            var operationWatch = Stopwatch.StartNew();
            RunRecord? crashRecord = null;

            void Cancel(string pointName)
            {
                if (cancelled) return;
                cancelled = true;
                cancelPointName = pointName;
                cancelRows = lastRows;
                journalAtCancel = GateSupport.HandleLengthOrZero(journal);
                cancelStamp = Stopwatch.GetTimestamp();
                cancel.Cancel();
            }

            var options = new ImportOptions
            {
                OnSpaceCheck = check =>
                {
                    checks.Add([(long)check.Kind, check.RowsInserted, check.PageCount, check.PageSize, check.MainFileLength, check.JournalLength, check.PendingGrowth, GateSupport.FreeBytes(main)]);
                    lastRows = check.RowsInserted;
                    if (check.Kind == SpaceCheckKind.Final)
                    {
                        journalLengthBeforeCommit = check.JournalLength;   // read through a live handle by the importer, immediately before COMMIT
                        if (o.Attribution)
                        {
                            journalCopyName = GateSupport.Slug(o.Label) + ".precommit.journal";
                            using var src = new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                            using var dst = new FileStream(Path.Combine(o.AnalysisDir, journalCopyName), FileMode.Create, FileAccess.Write);
                            src.CopyTo(dst);
                            journalCopyLength = dst.Length;
                        }
                        if (o.Mode == "cancel-after-final") Cancel("after the final check");
                    }
                },
                Probe = cancelPoint > 0 || o.Mode == "crash" ? point =>
                {
                    if (o.Mode == "crash")
                    {
                        if (point != ImportPoint.VerificationStart) return;
                        keep = true;
                        journalAtKill = GateSupport.HandleLengthOrZero(journal);
                        crashRecord = Build(o, cellRecord, generated, before, new Measured(operationStart, operationWatch.Elapsed.TotalSeconds, "Killed", null, wsBefore, 0, 0, 0, null, null, dbBefore),
                            journalAtKill: journalAtKill, killedDirectory: directory, killedAppData: appData);
                        Console.WriteLine(RecordJson.Serialize(crashRecord));
                        Console.Out.Flush();
                        Process.GetCurrentProcess().Kill();
                        return;
                    }
                    if (cancelPoint == 1 && point == ImportPoint.AfterRowCheck && lastRows >= expectedRows) Cancel(PointNames[1]);
                    else if (cancelPoint is 2 or 3 or 4 && (int)point == cancelPoint) Cancel(PointNames[cancelPoint]);
                } : null,
                AfterRows = cancelPoint == 1 ? () => Cancel("end of row insertion (the 95% row check was not reached)") : null,
            };

            var gcPause = GC.GetTotalPauseDuration();
            var gen2 = GC.CollectionCount(2);
            var imported = Import(session, target.Rows, header, "target", target.Source, options, cancel.Token, main, afterT0);
            var returnStamp = Stopwatch.GetTimestamp();
            var peakWs = Process.GetCurrentProcess().PeakWorkingSet64;
            Thread.Sleep(30);
            var samples = sampler.Stop();
            var dbAfter = GateSupport.HandleLength(main);
            var journalAfter = GateSupport.HandleLengthOrZero(journal);

            var result = imported.Result;
            string outcome = result is not null ? "Published" : imported.Failure is OperationCanceledException ? "Cancelled (rolled back)" : "Failed: " + imported.Failure?.Message;
            AttributionRecord? attribution = null;
            if (o.Attribution)
            {
                attribution = new AttributionRecord(new TargetRecord(target.NewSource ? "new" : "existing", target.NewSource ? null : 1), "persource", journalLengthBeforeCommit,
                    journalCopyName ?? "", journalCopyLength, databaseCopyName ?? "", databaseCopyLength, databaseCopySha);
            }

            CancelRecord? cancelRecord = null;
            if (cancelPoint > 0 || o.Mode == "cancel-after-final")
            {
                var seconds = cancelStamp == 0 ? 0 : Stopwatch.GetElapsedTime(cancelStamp, returnStamp).TotalSeconds;
                RollbackRecord? rollback = null;
                var published = result is not null;
                if (!published) rollback = CheckRolledBack(session, dbBefore, dbAfter, journalAfter, namesBefore, snapshotsBefore);
                cancelRecord = new CancelRecord(cancelPoint, cancelPointName, seconds, cancelRows, journalAtCancel, outcome, rollback, published && o.Mode == "cancel-after-final");
                Console.WriteLine($"cancelled at {cancelPointName} ({journalAtCancel / 1048576.0:0.0} MiB of journal, {cancelRows:N0} rows): returned after {seconds:0.000} s; {outcome}");
            }

            DeleteRecord? deleted = null;
            if (o.Mode == "delete" && result is not null)
            {
                var verify = session.Read(r => SnapshotVerifier.VerifyPublished(r, result.SnapshotId));
                if (verify is not null) throw new InvalidOperationException("verification of the committed snapshot failed: " + verify);
                var deleteSampler = new JournalSampler(journal);
                deleteSampler.Start();
                var watch = Stopwatch.StartNew();
                using (var lease = GateSupport.Lease(session, MutationKind.Delete)) session.DeleteSnapshotAsync(lease, result.SnapshotId).GetAwaiter().GetResult();
                var deleteSeconds = watch.Elapsed.TotalSeconds;
                Thread.Sleep(30);
                deleted = new DeleteRecord(deleteSeconds, deleteSampler.Stop().Peak);
            }

            var record = Build(o, cellRecord, generated, before, new Measured(imported.Start, imported.Seconds, outcome, result, wsBefore, peakWs,
                    GC.GetTotalPauseDuration().TotalMilliseconds - gcPause.TotalMilliseconds, GC.CollectionCount(2) - gen2, checks, samples, dbAfter),
                attribution, cancelRecord, deleted);
            session.TestOnlyShutdown();
            Console.WriteLine(RecordJson.Serialize(record));
            return record;
        }
        finally
        {
            if (!keep) GateSupport.TryDelete(root);
        }
    }

    private static CellRecord CellRecordOf(RunOptions o)
    {
        var c = o.Cell;
        var g = c.Cell;
        return new CellRecord(c.Id, g.Label, WorkloadClassifier.Name(c.Class), g.Kind.ToString(), g.Files, g.D, g.Model, g.Rho, g.Delta, g.Alpha, g.Family?.ToString(), g.FamilyRescan, c.Library.ToString(), c.RunFamily,
            c.Params, o.Scale, c.TargetFiles, c.PrefillPerSnapshot, BinaryIdentity.PrefillKey(c.Prefill, o.Binary));
    }

    private static RollbackRecord CheckRolledBack(LibrarySession session, long mainBefore, long mainAfter, long journalAfter, long namesBefore, long snapshotsBefore)
    {
        string? problem = null;
        long snapshots = -1, names = -1;
        var olderOk = false;
        try
        {
            snapshots = session.Read(r => r.Long("SELECT count(*) FROM snapshot"));
            names = session.Read(r => r.Long("SELECT count(*) FROM name"));
            olderOk = Enumerable.Range(1, (int)Math.Min(snapshots, 3)).All(id => session.Read(r => SnapshotVerifier.VerifyPublished(r, id)) is null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or LibraryUnavailableException)
        {
            problem = "the Library could not be read after the cancellation: " + ex.Message;
        }
        problem ??= snapshots != snapshotsBefore ? $"{snapshots} snapshots after, {snapshotsBefore} before"
            : names != namesBefore ? $"{names} names after, {namesBefore} before"
            : mainAfter != mainBefore ? $"main file {mainAfter} B after, {mainBefore} B before"
            : journalAfter != 0 ? $"journal {journalAfter} B after"
            : !olderOk ? "an older snapshot does not verify"
            : null;
        return new RollbackRecord(problem is null, snapshots, names, mainBefore, mainAfter, journalAfter, olderOk, problem);
    }

    /// <summary>The Library's state before the import.</summary>
    private sealed record Before(long DbBytes, long PageSize, long PageCount, long Freelist, long Names, long NameBytes, long Paths, long Snapshots);

    /// <summary>What a run measured, besides the import's own result.</summary>
    private sealed record Measured(DateTime Start, double OperationSeconds, string Outcome, ImportResult? Result, long WorkingSetBefore, long PeakWorkingSet, double GcPauseMs, int Gen2,
        List<long[]>? Checks, JournalSamples? Samples, long DbAfter);

    private static RunRecord Build(RunOptions o, CellRecord cell, GeneratedRecord? generated, Before before, Measured m, AttributionRecord? attribution = null, CancelRecord? cancel = null,
        DeleteRecord? delete = null, long journalAtKill = 0, string? killedDirectory = null, string? killedAppData = null)
    {
        var kind = o.Mode switch { "timed" => "timed", "attribution" => "attribution", "crash" => "crash", "delete" => "delete", "cancel-after-final" => "cancel-after-final", _ => "cancel" };
        var r = m.Result;
        var phases = r?.Phases;
        var checks = m.Checks;
        return new RunRecord
        {
            Kind = kind,
            Label = o.Label,
            Binary = o.Binary,
            Cell = cell,
            OperationStartUtc = GateSupport.Iso(m.Start),
            OperationSeconds = m.OperationSeconds,
            Outcome = m.Outcome,
            Generated = generated,
            Library = new LibraryRecord(before.DbBytes, m.DbAfter, m.DbAfter - before.DbBytes, before.PageSize, before.PageCount, before.Freelist, before.Names, before.NameBytes, before.Paths, before.Snapshots),
            Files = r?.Files ?? 0,
            Folders = r?.Folders ?? 0,
            NewNames = r?.NewNames ?? 0,
            ImportSeconds = r?.Elapsed.TotalSeconds ?? 0,
            FilesPerSecond = r is null ? 0 : r.Files / r.Elapsed.TotalSeconds,
            Phases = phases is null ? (kind is "timed" or "attribution" ? new PhaseRecord(0, 0, 0, 0, 0) : null) : new PhaseRecord(phases.Folders.TotalMilliseconds, phases.Files.TotalMilliseconds, phases.Errors.TotalMilliseconds, phases.Verification.TotalMilliseconds, phases.Commit.TotalMilliseconds),
            Journal = m.Samples is null || checks is null ? null : new JournalRecord(checks.LastOrDefault(c => c[0] == (long)SpaceCheckKind.Final)?[5] ?? 0, m.Samples.Peak, m.Samples.Count, m.Samples.LargestIntervalMs, r?.PeakJournalBytes ?? 0),
            Imp11 = checks is null ? null : Imp11Of(checks, r?.Checks),
            Token = r?.Checks is { } k ? new TokenRecord(k.TokenObservations, k.MaxTokenGap.TotalSeconds) : checks is null ? null : new TokenRecord(0, 0),
            Memory = new MemoryRecord(m.WorkingSetBefore, m.PeakWorkingSet, m.GcPauseMs, m.Gen2),
            Attribution = attribution,
            Cancel = cancel,
            Delete = delete,
            JournalAtKillBytes = journalAtKill,
            KilledLibraryDirectory = killedDirectory,
            KilledAppData = killedAppData,
        };
    }

    /// <summary>IMP-11's quantities, checked against the model of §11.8 interval by interval: the growth of main file plus journal
    /// between two consecutive checks against the allowance <c>Λ_k + I_L</c> (<c>I_L = 2 × max(16 MiB, D_k)</c>, <c>D_k</c> the drop in free
    /// space since the previous check), and at the final check <c>Λ_f + R_C</c> (<c>R_C = 1 MiB</c>); and what COMMIT wrote against the
    /// final check's Λ.</summary>
    internal static Imp11Record Imp11Of(List<long[]> checks, ImportChecks? importerChecks)
    {
        const long Mi = 1 << 20;
        long over = 0, largestGrowth = 0, largestIndex = -1;
        double largestRatio = 0, largestRows = 0;
        for (var i = 1; i < checks.Count; i++)
        {
            var (prev, cur) = (checks[i - 1], checks[i]);
            var growth = cur[4] + cur[5] - prev[4] - prev[5];
            var drop = prev[7] >= 0 && cur[7] >= 0 ? Math.Max(0, prev[7] - cur[7]) : 0;
            var allowance = cur[6] + (cur[0] == (long)SpaceCheckKind.Final ? Mi : 2 * Math.Max(16 * Mi, drop));
            var ratio = allowance <= 0 ? double.PositiveInfinity : (double)growth / allowance;
            if (growth > allowance) over++;
            if (growth > largestGrowth) { largestGrowth = growth; largestIndex = i; largestRows = cur[1] - prev[1]; }
            if (!double.IsInfinity(ratio) && ratio > largestRatio) largestRatio = ratio;
        }
        var finalPending = importerChecks?.FinalPendingGrowth ?? -1;
        var commitGrowth = importerChecks?.CommitGrowth ?? -1;
        return new Imp11Record(checks, checks.Count, finalPending, commitGrowth, commitGrowth >= 0 && commitGrowth == finalPending, over, largestRatio, largestGrowth, largestIndex, largestRows);
    }

    // ------------------------------------------------------------------------------------------------------------ recovery

    /// <summary>Times the start-up open of a Library left with a hot journal by a killed child (recovery cost, PERF-15 (c)).</summary>
    internal static int Recover(string[] args)
    {
        var directory = GateSupport.Req(args, "dir");
        var appData = GateSupport.Req(args, "appdata");
        var expectedSnapshots = long.Parse(GateSupport.Opt(args, "snapshots", "-1"), CultureInfo.InvariantCulture);
        var main = Path.Combine(directory, LibraryNames.MainFile);
        var journal = Path.Combine(directory, LibraryNames.JournalFile);
        var journalBefore = GateSupport.HandleLengthOrZero(journal);
        var mainBefore = GateSupport.HandleLengthOrZero(main);
        var watch = Stopwatch.StartNew();
        var session = new LibrarySession(directory, appData);
        var status = session.RunStartupOpen();
        var openSeconds = watch.Elapsed.TotalSeconds;
        var snapshots = status.State == LibraryState.Available ? session.Read(r => r.Long("SELECT count(*) FROM snapshot")) : -1;
        var olderOk = false;
        string? problem = null;
        if (status.State == LibraryState.Available)
            olderOk = Enumerable.Range(1, (int)Math.Min(snapshots, 3)).All(id => session.Read(r => SnapshotVerifier.VerifyPublished(r, id)) is null);
        else
            problem = "state " + status.State + ": older snapshots NOT verified";
        session.TestOnlyShutdown();
        var mainAfter = GateSupport.HandleLengthOrZero(main);
        var journalAfter = GateSupport.HandleLengthOrZero(journal);
        if (problem is null && expectedSnapshots >= 0 && snapshots != expectedSnapshots) problem = $"{snapshots} snapshots after the recovery, {expectedSnapshots} expected";
        if (problem is null && !olderOk) problem = "an older snapshot does not verify";
        if (problem is null && journalAfter != 0) problem = $"journal {journalAfter} B after the recovery";
        var record = new RecoveryRecord(openSeconds, status.State.ToString(), journalBefore, mainBefore, mainAfter, journalAfter, snapshots, olderOk, problem);
        Console.WriteLine($"recovery open: {openSeconds:0.000} s, hot journal {journalBefore / 1048576.0:0.0} MiB, main {mainBefore / 1048576.0:0.0} -> {mainAfter / 1048576.0:0.0} MiB, state {status.State}, snapshots {snapshots}, older snapshots {(olderOk ? "verify" : "DO NOT verify")}");
        Console.WriteLine(JsonSerializer.Serialize(record, RecordJson.Options));
        return 0;
    }
}

/// <summary>The samples of a journal sampler.</summary>
internal sealed record JournalSamples(long Peak, long Count, double LargestIntervalMs);

/// <summary>Samples the journal's length through a live handle every millisecond (§15.4: "the sampled series with its actual largest
/// sampling interval as a cross-check"; the reported peak is the length immediately before COMMIT).</summary>
internal sealed class JournalSampler(string journal)
{
    private readonly CancellationTokenSource _stop = new();
    private Thread? _thread;
    private long _peak, _count;
    private double _largest;

    internal void Start()
    {
        _thread = new Thread(() =>
        {
            var last = Stopwatch.GetTimestamp();
            while (!_stop.IsCancellationRequested)
            {
                var length = GateSupport.HandleLengthOrZero(journal);
                if (length > _peak) _peak = length;
                _count++;
                var now = Stopwatch.GetTimestamp();
                var interval = Stopwatch.GetElapsedTime(last, now).TotalMilliseconds;
                if (_count > 1 && interval > _largest) _largest = interval;
                last = now;
                Thread.Sleep(1);
            }
        }) { IsBackground = true, Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    internal JournalSamples Stop()
    {
        _stop.Cancel();
        _thread?.Join();
        return new JournalSamples(_peak, _count, _largest);
    }
}

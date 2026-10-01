using System.Diagnostics;
using StorageInventory.Core.Paths;
using StorageInventory.Core.Reports;
using StorageInventory.Core.Scanning;

namespace StorageInventory.Core;

/// <summary>A report file could not be created or written. Always a failure of the OUTPUT, never of the scanned tree.</summary>
internal sealed class ReportWriteException(Exception inner) : Exception("A report file could not be written: " + inner.Message, inner);

/// <summary>The outcome of a scan run through <see cref="InventoryScanner.ScanObserved"/>: v1's result, unchanged, plus
/// what happened to the extra (isolated) observers.</summary>
/// <param name="Scan">Exactly the result the public <see cref="InventoryScanner.Scan"/> returns for the same run.</param>
/// <param name="Faults">Isolated observers that failed with a class A exception, in order; the scan continued without
/// them.</param>
/// <param name="Catastrophic">A class C exception (CAT-03) ended the run: v1's top-level handler turned it into
/// <c>Failed (Unexpected)</c>, which is never Finished. Later gates use this to stop trusting the process.</param>
internal sealed record ObservedScanOutcome(StorageScanResult Scan, IReadOnlyList<ObserverFault> Faults, bool Catastrophic);

/// <summary>
/// The standard <see cref="IStorageInventoryScanner"/>: validate (again) → name the run → enumerate while streaming
/// the Files and ScanErrors reports → aggregate and self-check → finalise folders → Folders report → sorted Files
/// report → remove the run's temporary file. Every outcome is returned as a <see cref="StorageScanResult"/>.
/// </summary>
/// <remarks>One pipeline serves every caller. The traversal feeds an <see cref="ObserverFanOut"/> whose critical
/// observer is v1's CSV report sink behind <see cref="OutputGuard"/>; the public entry points attach no other observer,
/// and <see cref="ScanObserved"/> (internal) attaches isolated ones. The filesystem is enumerated once per scan.</remarks>
public sealed class InventoryScanner : IStorageInventoryScanner
{
    private const int TopLevelFolderLimit = 100;

    public Task<StorageScanResult> ScanAsync(StorageScanOptions options, IProgress<StorageScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Task.Factory.StartNew(() => Scan(options, progress, cancellationToken), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    /// <summary>Synchronous form, for callers that manage their own threads (CLI, tests).</summary>
    public StorageScanResult Scan(StorageScanOptions options, IProgress<StorageScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Run(options, [], progress, cancellationToken).Scan;
    }

    /// <summary>
    /// The same scan as <see cref="Scan"/>, with additional isolated observers attached to the one traversal (C1: for
    /// tests; later gates build their capture entry point on it). The returned <see cref="ObservedScanOutcome.Scan"/>
    /// is v1's result: an isolated observer can never change it, except that cancellation and class C exceptions are
    /// never isolated (CAT-02, CAT-03).
    /// </summary>
    internal ObservedScanOutcome ScanObserved(StorageScanOptions options, IReadOnlyList<IScanObserver> isolatedObservers,
        IProgress<StorageScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(isolatedObservers);
        return Run(options, isolatedObservers, progress, cancellationToken);
    }

    private static ObservedScanOutcome Run(StorageScanOptions options, IReadOnlyList<IScanObserver> isolatedObservers,
        IProgress<StorageScanProgress>? progress, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var reporter = new ProgressReporter(progress, options.ProgressInterval, clock);
        var timings = new PhaseTimings();
        var phaseClock = Stopwatch.StartNew();

        reporter.Report(ScanPhase.Validating, null, "", force: true);
        var validation = PathPolicy.Validate(options.RootPath, options.OutputPath);
        timings = timings with { Validation = phaseClock.Elapsed };
        phaseClock.Restart();
        if (options.ReparsePoints != ReparsePointPolicy.NeverFollow || !validation.CanScan)
        {
            var reasons = validation.Issues.Where(i => i.Severity == IssueSeverity.Blocked).Select(i => i.Message).ToList();
            if (options.ReparsePoints != ReparsePointPolicy.NeverFollow) reasons.Add("Only the NeverFollow reparse-point policy exists.");
            return NotStarted(StorageScanResult.ForFailed(new ScanFailure(ScanFailureKind.InvalidPaths, string.Join(" ", reasons)),
                "", options.RootPath, options.OutputPath, new ScanTotals(), new Dictionary<ScanErrorType, long>(), [], timings with { Total = clock.Elapsed }));
        }

        var root = validation.RootFullPath!;
        var output = validation.OutputFullPath!;
        ReportRun run;
        try
        {
            run = ReportRun.Prepare(output, DateTime.Now);
            run.EnsureOutputFolder();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return NotStarted(StorageScanResult.ForFailed(new ScanFailure(ScanFailureKind.OutputError, "The report folder could not be prepared: " + ex.Message),
                "", root, output, new ScanTotals(), new Dictionary<ScanErrorType, long>(), [], timings with { Total = clock.Elapsed }));
        }

        // The run is named: from here on every observer is started and ended (SINK-03 #1).
        var start = new ScanStartInfo(run.RunId, root, PathPolicy.TryGetCanonicalPath(root), run.OwnFileNames);
        return RunNamed(start, output, run, options.SortFiles, isolatedObservers, reporter, timings, clock, phaseClock, cancellationToken);
    }

    private static ObservedScanOutcome NotStarted(StorageScanResult result) => new(result, [], false);

    private static ObservedScanOutcome RunNamed(ScanStartInfo start, string output, ReportRun run, bool sortFiles,
        IReadOnlyList<IScanObserver> isolatedObservers, ProgressReporter reporter, PhaseTimings timings, Stopwatch clock,
        Stopwatch phaseClock, CancellationToken cancellationToken)
    {
        TimeSpan Lap() { var t = phaseClock.Elapsed; phaseClock.Restart(); return t; }
        var root = start.RootFullPath;

        var sink = new CsvReportSink(run, sortFiles);
        var fanOut = new ObserverFanOut(new OutputGuard(sink), isolatedObservers, cancellationToken);
        ScanEngine? engine = null;
        StorageScanResult? finished = null;
        Exception? failure = null;
        try
        {
            fanOut.OnScanStarted(start);   // creates the ScanErrors and Files (or temporary) reports, as v1 did here

            engine = new ScanEngine(root, fanOut, run.OwnFileNames, rel => reporter.Report(ScanPhase.Enumerating, engine, rel));
            engine.Run(cancellationToken);
            timings = timings with { Enumeration = Lap() };
            Output(sink.CloseFileRows);

            reporter.Report(ScanPhase.Aggregating, engine, "", force: true);
            var completeness = FolderAggregator.Aggregate(engine.Folders, engine.FileCount, engine.TotalBytes);
            var order = ReportWriters.FolderOrder(engine.Folders);

            // Every folder is final now (Aggregate ran Verify): emit each once, ascending, before the Folders report.
            for (var i = 0; i < engine.Folders.Count; i++)
            {
                fanOut.OnFolderFinalised(i, engine.Folders[i].ParentIndex, FolderAggregator.ToRecord(engine.Folders, i, root));
            }
            timings = timings with { Aggregation = Lap() };

            Output(() => ReportWriters.WriteFolders(run, engine.Folders, order, root,
                (done, total) => reporter.Report(ScanPhase.WritingFoldersReport, engine, "", done, total), cancellationToken));
            timings = timings with { FoldersReport = Lap() };

            if (sortFiles)
            {
                reporter.Report(ScanPhase.SortingFiles, engine, "", force: true);
                Output(() => ReportWriters.WriteSortedFiles(run, sink.SortIndex, sink.LongestRowBytes,
                    (done, total) => reporter.Report(ScanPhase.WritingFilesReport, engine, "", done, total), cancellationToken));
                Output(() => run.DeleteOwnTemporaryFile(run.FilesTemporary));
                sink.SortIndex.Clear();
                sink.SortIndex.TrimExcess();
                timings = timings with { Sorting = Lap() };
            }
            Output(sink.CloseErrors);
            timings = timings with { FilesReport = Lap() };

            var totals = Totals(engine, completeness);
            var topLevel = order.Where(i => engine.Folders[i].Depth == 1).Take(TopLevelFolderLimit)
                .Select(i => FolderAggregator.ToRecord(engine.Folders, i, root)).ToList();
            var result = StorageScanResult.ForFinishedScan(run.RunId, root, output, totals, Snapshot(engine),
                FolderAggregator.ToRecord(engine.Folders, 0, root), topLevel,
                new ReportSet(run.FilesCsv, run.FoldersCsv, run.ErrorsCsv, sortFiles), timings with { Total = clock.Elapsed });

            // v1's reports are complete; only now is any observer told that the scan finished.
            fanOut.OnScanEnded(ScanEndInfo.Finished(totals));
            finished = result;
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            sink.Dispose();   // every report stream is closed on success, failure and cancellation
        }

        if (finished is not null)
        {
            reporter.Report(ScanPhase.Finished, engine, "", force: true);
            return new ObservedScanOutcome(finished, fanOut.Faults, false);
        }

        var cancelled = failure is OperationCanceledException && cancellationToken.IsCancellationRequested;
        var catastrophic = ExceptionClasses.IsCatastrophic(failure!);
        // Nothing of this run can become valid: end every observer that has not been ended, each in its own guard.
        catastrophic |= fanOut.EndRemaining(cancelled ? ScanEndInfo.Cancelled() : ScanEndInfo.Failed());
        reporter.Report(ScanPhase.Finished, engine, "", force: true);

        var totalsSoFar = engine is null ? new ScanTotals() : Totals(engine, null);
        var errors = engine is null ? new Dictionary<ScanErrorType, long>() : Snapshot(engine);
        var artifacts = run.CreatedFiles.ToList();
        var finalTimings = timings with { Total = clock.Elapsed };
        if (cancelled)
        {
            return new ObservedScanOutcome(StorageScanResult.ForCancelled(run.RunId, root, output, totalsSoFar, errors, artifacts, finalTimings),
                fanOut.Faults, catastrophic);
        }

        var scanFailure = failure switch
        {
            OutputInsideScannedTreeException e => new ScanFailure(ScanFailureKind.OutputInsideScannedTree, e.Message),
            ScanConsistencyException e => new ScanFailure(ScanFailureKind.InternalConsistency, e.Message),
            ReportWriteException e => new ScanFailure(ScanFailureKind.OutputError, e.Message),
            _ => new ScanFailure(ScanFailureKind.Unexpected, "The scan stopped unexpectedly: " + failure!.Message),
        };
        return new ObservedScanOutcome(StorageScanResult.ForFailed(scanFailure, run.RunId, root, output, totalsSoFar, errors, artifacts, finalTimings),
            fanOut.Faults, catastrophic);
    }

    /// <summary>Runs an output step; I/O failures become <see cref="ReportWriteException"/>.</summary>
    private static void Output(Action step)
    {
        try
        {
            step();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ReportWriteException(ex);
        }
    }

    private static ScanTotals Totals(ScanEngine engine, CompletenessSummary? completeness) => new()
    {
        Files = engine.FileCount,
        Folders = engine.Folders.Count,
        Bytes = engine.TotalBytes,
        ScanErrors = engine.ScanErrorCount,
        ReparsePointsSkipped = engine.ReparsePointsSkipped,
        FileReparsePoints = engine.FileReparsePoints,
        LocallyIncompleteFolders = completeness?.LocallyIncompleteFolders ?? 0,
        AffectedAncestorFolders = completeness?.AffectedAncestorFolders ?? 0,
    };

    private static Dictionary<ScanErrorType, long> Snapshot(ScanEngine engine) => new(engine.ErrorCounts);
}

/// <summary>Throttled progress. Never invents a total: enumeration reports counters only.</summary>
internal sealed class ProgressReporter(IProgress<StorageScanProgress>? target, TimeSpan interval, Stopwatch clock)
{
    private TimeSpan _next = TimeSpan.Zero;

    public void Report(ScanPhase phase, ScanEngine? engine, string currentRelativePath, long done = 0, long total = 0, bool force = false)
    {
        if (target is null) return;
        var now = clock.Elapsed;
        if (!force && now < _next) return;
        _next = now + interval;
        target.Report(new StorageScanProgress(
            phase,
            engine?.FileCount ?? 0,
            engine?.Folders.Count ?? 0,
            engine?.TotalBytes ?? 0,
            engine?.ScanErrorCount ?? 0,
            engine?.ReparsePointsSkipped ?? 0,
            engine?.FileReparsePoints ?? 0,
            currentRelativePath,
            now,
            done,
            total));
    }
}

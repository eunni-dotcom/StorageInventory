using System.Diagnostics;
using StorageInventory.Core.Paths;
using StorageInventory.Core.Reports;
using StorageInventory.Core.Scanning;

namespace StorageInventory.Core;

/// <summary>A report file could not be created or written. Always a failure of the OUTPUT, never of the scanned tree.</summary>
internal sealed class ReportWriteException(Exception inner) : Exception("A report file could not be written: " + inner.Message, inner);

/// <summary>
/// The standard <see cref="IStorageInventoryScanner"/>: validate (again) → name the run → enumerate while streaming
/// the Files and ScanErrors reports → aggregate and self-check → Folders report → sorted Files report → remove the
/// run's temporary file. Every outcome is returned as a <see cref="StorageScanResult"/>.
/// </summary>
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
        var clock = Stopwatch.StartNew();
        var reporter = new ProgressReporter(progress, options.ProgressInterval, clock);
        var timings = new PhaseTimings();
        var phaseClock = Stopwatch.StartNew();
        TimeSpan Lap() { var t = phaseClock.Elapsed; phaseClock.Restart(); return t; }

        reporter.Report(ScanPhase.Validating, null, "", force: true);
        var validation = PathPolicy.Validate(options.RootPath, options.OutputPath);
        timings = timings with { Validation = Lap() };
        if (options.ReparsePoints != ReparsePointPolicy.NeverFollow || !validation.CanScan)
        {
            var reasons = validation.Issues.Where(i => i.Severity == IssueSeverity.Blocked).Select(i => i.Message).ToList();
            if (options.ReparsePoints != ReparsePointPolicy.NeverFollow) reasons.Add("Only the NeverFollow reparse-point policy exists.");
            return StorageScanResult.ForFailed(new ScanFailure(ScanFailureKind.InvalidPaths, string.Join(" ", reasons)),
                "", options.RootPath, options.OutputPath, new ScanTotals(), new Dictionary<ScanErrorType, long>(), [], timings with { Total = clock.Elapsed });
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
            return StorageScanResult.ForFailed(new ScanFailure(ScanFailureKind.OutputError, "The report folder could not be prepared: " + ex.Message),
                "", root, output, new ScanTotals(), new Dictionary<ScanErrorType, long>(), [], timings with { Total = clock.Elapsed });
        }

        CsvReportSink? sink = null;
        ScanEngine? engine = null;
        StorageScanResult? finished = null;
        Exception? failure = null;
        try
        {
            try
            {
                sink = new CsvReportSink(run, options.SortFiles);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ReportWriteException(ex);
            }

            var guardedSink = new OutputGuardSink(sink);
            engine = new ScanEngine(root, guardedSink, run.OwnFileNames, rel => reporter.Report(ScanPhase.Enumerating, engine, rel));
            engine.Run(cancellationToken);
            timings = timings with { Enumeration = Lap() };
            Output(sink.CloseFileRows);

            reporter.Report(ScanPhase.Aggregating, engine, "", force: true);
            var completeness = FolderAggregator.Aggregate(engine.Folders, engine.FileCount, engine.TotalBytes);
            var order = ReportWriters.FolderOrder(engine.Folders);
            timings = timings with { Aggregation = Lap() };

            Output(() => ReportWriters.WriteFolders(run, engine.Folders, order, root,
                (done, total) => reporter.Report(ScanPhase.WritingFoldersReport, engine, "", done, total), cancellationToken));
            timings = timings with { FoldersReport = Lap() };

            if (options.SortFiles)
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

            var topLevel = order.Where(i => engine.Folders[i].Depth == 1).Take(TopLevelFolderLimit)
                .Select(i => FolderAggregator.ToRecord(engine.Folders, i, root)).ToList();
            finished = StorageScanResult.ForFinishedScan(run.RunId, root, output, Totals(engine, completeness), Snapshot(engine),
                FolderAggregator.ToRecord(engine.Folders, 0, root), topLevel,
                new ReportSet(run.FilesCsv, run.FoldersCsv, run.ErrorsCsv, options.SortFiles), timings with { Total = clock.Elapsed });
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            sink?.Dispose();   // every report stream is closed on success, failure and cancellation
        }

        reporter.Report(ScanPhase.Finished, engine, "", force: true);
        if (finished is not null) return finished;

        var totals = engine is null ? new ScanTotals() : Totals(engine, null);
        var errors = engine is null ? new Dictionary<ScanErrorType, long>() : Snapshot(engine);
        var artifacts = run.CreatedFiles.ToList();
        var finalTimings = timings with { Total = clock.Elapsed };
        if (failure is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            return StorageScanResult.ForCancelled(run.RunId, root, output, totals, errors, artifacts, finalTimings);
        }

        var scanFailure = failure switch
        {
            OutputInsideScannedTreeException e => new ScanFailure(ScanFailureKind.OutputInsideScannedTree, e.Message),
            ScanConsistencyException e => new ScanFailure(ScanFailureKind.InternalConsistency, e.Message),
            ReportWriteException e => new ScanFailure(ScanFailureKind.OutputError, e.Message),
            _ => new ScanFailure(ScanFailureKind.Unexpected, "The scan stopped unexpectedly: " + failure!.Message),
        };
        return StorageScanResult.ForFailed(scanFailure, run.RunId, root, output, totals, errors, artifacts, finalTimings);
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

    /// <summary>Marks sink I/O failures as report-write failures, so they are never confused with scan problems.</summary>
    private sealed class OutputGuardSink(IScanSink inner) : IScanSink
    {
        public void OnFile(in FileInventoryRecord file, int folderIndex)
        {
            try { inner.OnFile(file, folderIndex); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new ReportWriteException(ex); }
        }

        public void OnError(ScanErrorRecord error)
        {
            try { inner.OnError(error); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new ReportWriteException(ex); }
        }
    }
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

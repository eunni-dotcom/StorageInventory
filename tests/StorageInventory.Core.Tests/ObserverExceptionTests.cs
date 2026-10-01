using StorageInventory.Core.Scanning;
using StorageInventory.Core.Spool;
using StorageInventory.Testing;

namespace StorageInventory.Core.Tests;

/// <summary>An observer that records its calls and throws a chosen exception at a chosen call.</summary>
internal sealed class ThrowingObserver(string callback, int atCall, Func<Exception> exception) : IScanObserver
{
    private int _calls;

    public RecordingObserver Seen { get; } = new();

    private void Maybe(string name)
    {
        if (name == callback && _calls++ == atCall) throw exception();
    }

    public void OnScanStarted(in ScanStartInfo start) { Seen.OnScanStarted(start); Maybe(nameof(OnScanStarted)); }
    public void OnFile(in FileInventoryRecord file, int folderIndex) { Seen.OnFile(file, folderIndex); Maybe(nameof(OnFile)); }
    public void OnError(ScanErrorRecord error) { Seen.OnError(error); Maybe(nameof(OnError)); }
    public void OnFolderFinalised(int index, int parentIndex, FolderInventoryRecord folder) { Seen.OnFolderFinalised(index, parentIndex, folder); Maybe(nameof(OnFolderFinalised)); }
    public void OnScanEnded(in ScanEndInfo end) { Seen.OnScanEnded(end); Maybe(nameof(OnScanEnded)); }
}

/// <summary>
/// TEST-O4: the exception classes of CAT-01 to CAT-03 at the fan-out and through the scan pipeline. An isolated
/// observer's <see cref="OperationCanceledException"/> is class A without the run's token and class B with it;
/// <see cref="OutOfMemoryException"/> and <see cref="InsufficientExecutionStackException"/> are rethrown, never isolated,
/// and end the scan as v1's <c>Failed (Unexpected)</c>; a critical <see cref="IOException"/> becomes
/// <c>OutputError</c> through <see cref="OutputGuard"/>, which never wraps an isolated observer.
/// </summary>
public static class ObserverExceptionTests
{
    private static readonly ScanStartInfo Start = new("20261001_120000_abcdef", @"C:\x", null, new HashSet<string>());
    private static readonly FileInventoryRecord File1 = new("a.txt", ".txt", "a.txt", ".", @"C:\x\a.txt", 1, null, null, null, FileAttributes.Normal);

    // ---- The fan-out on its own ----

    [Test]
    public static void An_isolated_observers_class_A_exception_faults_only_that_observer()
    {
        var critical = new RecordingObserver();
        var bad = new ThrowingObserver(nameof(IScanObserver.OnFile), 0, () => new IOException("isolated disk full"));
        var good = new RecordingObserver();
        var fan = new ObserverFanOut(new OutputGuard(critical), [bad, good], CancellationToken.None);
        fan.OnScanStarted(Start);
        fan.OnFile(File1, 0);
        fan.OnFile(File1, 0);
        fan.OnError(new ScanErrorRecord(@"C:\x\e", ScanErrorType.AccessDenied, "denied"));
        fan.OnScanEnded(ScanEndInfo.Failed());

        var fault = fan.Faults.Single();
        Assert.True(ReferenceEquals(bad, fault.Observer));
        Assert.Equal(nameof(IScanObserver.OnFile), fault.Callback);
        Assert.True(fault.Exception.GetType() == typeof(IOException), "OutputGuard is not involved: " + fault.Exception.GetType().Name);
        Assert.SequenceEqual(["ObservedStart", "ObservedFile", "ObservedEnd"], bad.Seen.Calls.Select(c => c.GetType().Name), "a faulted observer gets only OnScanEnded afterwards");
        Assert.Equal(5, critical.Calls.Count, "the critical observer saw everything");
        Assert.Equal(5, good.Calls.Count, "other isolated observers saw everything");
    }

    [Test]
    public static void OperationCanceledException_is_class_A_without_the_token_and_class_B_with_it()
    {
        using var cts = new CancellationTokenSource();
        var bad = new ThrowingObserver(nameof(IScanObserver.OnFile), 0, () => new OperationCanceledException("not the run's"));
        var fan = new ObserverFanOut(new RecordingObserver(), [bad], cts.Token);
        fan.OnScanStarted(Start);
        fan.OnFile(File1, 0);   // isolated: the token is not cancelled
        Assert.Equal(1, fan.Faults.Count);

        var cancelling = new ThrowingObserver(nameof(IScanObserver.OnFile), 0, () => new OperationCanceledException(cts.Token));
        var fan2 = new ObserverFanOut(new RecordingObserver(), [cancelling], cts.Token);
        fan2.OnScanStarted(Start);
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => fan2.OnFile(File1, 0));
        Assert.Equal(0, fan2.Faults.Count, "cancellation is never isolated");
    }

    [Test]
    public static void Class_C_exceptions_are_never_isolated()
    {
        foreach (var make in new Func<Exception>[] { () => new OutOfMemoryException(), () => new InsufficientExecutionStackException(), () => new InsufficientMemoryException() })
        {
            foreach (var callback in new[] { nameof(IScanObserver.OnScanStarted), nameof(IScanObserver.OnFile), nameof(IScanObserver.OnError), nameof(IScanObserver.OnFolderFinalised), nameof(IScanObserver.OnScanEnded) })
            {
                var bad = new ThrowingObserver(callback, 0, make);
                var fan = new ObserverFanOut(new RecordingObserver(), [bad], CancellationToken.None);
                var thrown = CaptureException(() =>
                {
                    fan.OnScanStarted(Start);
                    fan.OnFile(File1, 0);
                    fan.OnError(new ScanErrorRecord("p", ScanErrorType.IOError, "m"));
                    fan.OnFolderFinalised(0, -1, Folder());
                    fan.OnScanEnded(ScanEndInfo.Finished(new ScanTotals()));
                });
                Assert.True(thrown?.GetType() == make().GetType(), $"{make().GetType().Name} in {callback}: rethrown unchanged, got {thrown?.GetType().Name}");
                Assert.Equal(0, fan.Faults.Count, $"{callback}: not recorded as an isolated fault");
            }
        }
    }

    [Test]
    public static void The_critical_observers_IO_errors_become_report_write_failures()
    {
        var critical = new ThrowingObserver(nameof(IScanObserver.OnFile), 0, () => new IOException("report disk full"));
        var fan = new ObserverFanOut(new OutputGuard(critical), [new RecordingObserver()], CancellationToken.None);
        fan.OnScanStarted(Start);
        var ex = Assert.Throws<ReportWriteException>(() => fan.OnFile(File1, 0));
        Assert.True(ex.InnerException is IOException, "the original I/O error is kept");
        var denied = new ThrowingObserver(nameof(IScanObserver.OnScanStarted), 0, () => new UnauthorizedAccessException("denied"));
        Assert.Throws<ReportWriteException>(() => new ObserverFanOut(new OutputGuard(denied), [], CancellationToken.None).OnScanStarted(Start));
    }

    [Test]
    public static void Every_started_observer_is_ended_exactly_once_on_every_path()
    {
        // The critical observer fails while creating its reports: isolated observers were started first and are ended.
        var critical = new ThrowingObserver(nameof(IScanObserver.OnScanStarted), 0, () => new IOException("cannot create"));
        var a = new RecordingObserver();
        var b = new ThrowingObserver(nameof(IScanObserver.OnScanEnded), 0, () => new InvalidOperationException("end failed"));
        var fan = new ObserverFanOut(new OutputGuard(critical), [a, b], CancellationToken.None);
        Assert.Throws<ReportWriteException>(() => fan.OnScanStarted(Start));
        Assert.False(fan.EndRemaining(ScanEndInfo.Failed()), "no class C");
        Assert.SequenceEqual(["ObservedStart", "ObservedEnd"], a.Calls.Select(c => c.GetType().Name));
        Assert.Equal("Failed", ((ObservedEnd)a.Calls[^1]).Outcome);
        Assert.Equal(1, b.Seen.Calls.Count(c => c is ObservedEnd), "ended once even though its end threw");
        Assert.Equal(1, critical.Seen.Calls.Count(c => c is ObservedEnd), "the critical observer is ended too");
        Assert.False(fan.EndRemaining(ScanEndInfo.Failed()), "a second end is a no-op");
        Assert.Equal(2, a.Calls.Count, "never ended twice");

        // A class C exception while ending stops the notifications and is reported.
        var oom = new ThrowingObserver(nameof(IScanObserver.OnScanEnded), 0, () => new OutOfMemoryException());
        var fan2 = new ObserverFanOut(new RecordingObserver(), [oom], CancellationToken.None);
        fan2.OnScanStarted(Start);
        Assert.True(fan2.EndRemaining(ScanEndInfo.Cancelled()), "class C while ending is reported");
    }

    [Test]
    public static void The_observer_contract_has_no_discovery_callback()
    {
        // SINK-08: the only folder emission is the finalised record.
        var methods = typeof(IScanObserver).GetMethods().Select(m => m.Name).Order(StringComparer.Ordinal);
        Assert.SequenceEqual(["OnError", "OnFile", "OnFolderFinalised", "OnScanEnded", "OnScanStarted"], methods);
        Assert.False(typeof(IScanObserver).IsPublic, "internal (SINK-09)");
    }

    // ---- Through the pipeline (InventoryScanner.ScanObserved) on a small real tree ----

    private sealed class Tree : IDisposable
    {
        public Tree()
        {
            Base = Path.Combine(Path.GetTempPath(), "StorageInventoryTests", "o4_" + Guid.NewGuid().ToString("N")[..8]);
            Root = Path.Combine(Base, "src");
            Output = Path.Combine(Base, "out");
            for (var d = 0; d < 5; d++)
            {
                var dir = Path.Combine(Root, $"d{d}");
                Directory.CreateDirectory(dir);
                for (var f = 0; f < 20; f++) File.WriteAllBytes(Path.Combine(dir, $"f{f}.bin"), new byte[f]);
            }
        }

        public string Base { get; }
        public string Root { get; }
        public string Output { get; }

        public ObservedScanOutcome Scan(params IScanObserver[] observers) => Scan(CancellationToken.None, observers);

        public ObservedScanOutcome Scan(CancellationToken token, params IScanObserver[] observers) =>
            new InventoryScanner().ScanObserved(new StorageScanOptions { RootPath = Root, OutputPath = Output }, observers, null, token);

        public void Dispose()
        {
            if (Directory.Exists(Base)) Directory.Delete(Base, recursive: true);
        }
    }

    [Test]
    public static void An_isolated_class_A_failure_leaves_the_scan_complete()
    {
        using var tree = new Tree();
        var bad = new ThrowingObserver(nameof(IScanObserver.OnFile), 17, () => new OperationCanceledException("not the run's token"));
        var outcome = tree.Scan(bad);
        Assert.Equal(ScanCompletionState.Complete, outcome.Scan.State);
        Assert.False(outcome.Catastrophic);
        Assert.Equal(1, outcome.Faults.Count);
        Assert.Equal(3, Directory.GetFiles(tree.Output).Length, "v1's three reports");
    }

    [Test]
    public static void Class_C_from_an_isolated_observer_ends_the_scan_as_Failed_Unexpected()
    {
        // OnError is covered at the fan-out above: this small tree produces no error rows.
        foreach (var (callback, make) in new (string, Func<Exception>)[]
                 {
                     (nameof(IScanObserver.OnScanStarted), () => new InsufficientExecutionStackException("simulated")),
                     (nameof(IScanObserver.OnFile), () => new OutOfMemoryException("simulated")),
                     (nameof(IScanObserver.OnFolderFinalised), () => new InsufficientExecutionStackException("simulated")),
                     (nameof(IScanObserver.OnScanEnded), () => new OutOfMemoryException("simulated")),
                 })
        {
            using var tree = new Tree();
            var bad = new ThrowingObserver(callback, 0, make);
            var outcome = tree.Scan(bad);
            Assert.Equal(ScanCompletionState.Failed, outcome.Scan.State, callback);
            Assert.Equal(ScanFailureKind.Unexpected, outcome.Scan.Failure!.Kind, callback);
            Assert.Contains("The scan stopped unexpectedly", outcome.Scan.Failure.Message);
            Assert.True(outcome.Catastrophic, callback + ": reported as class C");
            Assert.Equal(0, outcome.Faults.Count, callback + ": never isolated");
            Assert.Null(outcome.Scan.Reports, callback + ": never Finished");
            Assert.Equal(1, bad.Seen.Calls.Count(c => c is ObservedEnd), callback + ": ended exactly once");
        }
    }

    [Test]
    public static void A_cancellation_thrown_by_an_isolated_observer_with_the_token_set_cancels_the_scan()
    {
        using var tree = new Tree();
        using var cts = new CancellationTokenSource();
        var cancelling = new ThrowingObserver(nameof(IScanObserver.OnFile), 5, () => { cts.Cancel(); return new OperationCanceledException(cts.Token); });
        var outcome = tree.Scan(cts.Token, cancelling);
        Assert.Equal(ScanCompletionState.Cancelled, outcome.Scan.State);
        Assert.Equal(0, outcome.Faults.Count);
        Assert.Equal("Cancelled", ((ObservedEnd)cancelling.Seen.Calls[^1]).Outcome);
    }

    [Test]
    public static void A_critical_IO_failure_is_OutputError_and_isolated_observers_are_still_ended()
    {
        // The isolated observer is started first; it takes the name of the critical observer's first report, so the
        // critical observer's create-new fails with an IOException, exactly as a report-name collision does in v1.
        using var tree = new Tree();
        var squatter = new SquattingObserver(tree.Output);
        var outcome = tree.Scan(squatter);
        Assert.Equal(ScanCompletionState.Failed, outcome.Scan.State);
        Assert.Equal(ScanFailureKind.OutputError, outcome.Scan.Failure!.Kind);
        Assert.Contains("report file could not be written", outcome.Scan.Failure.Message);
        Assert.False(outcome.Catastrophic);
        Assert.SequenceEqual(["ObservedStart", "ObservedEnd"], squatter.Seen.Calls.Select(c => c.GetType().Name));
        Assert.Equal("Failed", ((ObservedEnd)squatter.Seen.Calls[^1]).Outcome);
        Assert.Equal("SQUATTER", File.ReadAllText(squatter.SquattedPath!), "never overwritten");
        Assert.False(outcome.Scan.IncompleteArtifacts.Contains(squatter.SquattedPath!), "the squatter's file is not claimed");
    }

    private sealed class SquattingObserver(string output) : IScanObserver
    {
        public RecordingObserver Seen { get; } = new();
        public string? SquattedPath { get; private set; }

        public void OnScanStarted(in ScanStartInfo start)
        {
            Seen.OnScanStarted(start);
            SquattedPath = Path.Combine(output, $"ScanErrors_{start.RunId}.csv");
            File.WriteAllText(SquattedPath, "SQUATTER");
        }

        public void OnFile(in FileInventoryRecord file, int folderIndex) => Seen.OnFile(file, folderIndex);
        public void OnError(ScanErrorRecord error) => Seen.OnError(error);
        public void OnFolderFinalised(int index, int parentIndex, FolderInventoryRecord folder) => Seen.OnFolderFinalised(index, parentIndex, folder);
        public void OnScanEnded(in ScanEndInfo end) => Seen.OnScanEnded(end);
    }

    [Test]
    public static void A_spool_writer_that_fails_mid_scan_never_changes_the_scan()
    {
        using var tree = new Tree();
        using var failing = new FailingStream(failAfterBytes: 0);
        using var writer = new SpoolWriter(failing, SpoolTestKit.Token);
        var outcome = tree.Scan(writer);
        Assert.Equal(ScanCompletionState.Complete, outcome.Scan.State, "SpoolWriteFailed is the capture's outcome, never the scan's");
        Assert.Equal(SpoolWriterState.Faulted, writer.State);
        Assert.True(outcome.Faults.Single().Exception is IOException, "recorded as the isolated observer's fault");
    }

    private static Exception? CaptureException(Action action)
    {
        try { action(); return null; }
        catch (Exception ex) { return ex; }
    }

    private static FolderInventoryRecord Folder() => new()
    {
        Name = "x", RelativePath = ".", ParentRelativePath = "", FullPath = @"C:\x", Depth = 0, DirectSizeBytes = 1, TotalSizeBytes = 1,
        DirectFileCount = 1, TotalFileCount = 1, DirectSubfolderCount = 0, TotalSubfolderCount = 0, PercentOfRoot = 100,
        Attributes = FileAttributes.Directory, Status = FolderScanStatus.Ok, SubtreeComplete = true,
    };
}

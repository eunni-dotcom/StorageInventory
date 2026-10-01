using System.Security.Cryptography;
using StorageInventory.Core;
using StorageInventory.Core.Reports;
using StorageInventory.Core.Scanning;
using StorageInventory.Core.Spool;
using StorageInventory.Testing;

namespace StorageInventory.IntegrationTests;

/// <summary>Records every observer call with what the report folder held at that moment (test code only).</summary>
internal sealed class PipelineRecorder(string? outputFolder = null) : IScanObserver
{
    public abstract record Call;
    public sealed record Started(ScanStartInfo Info) : Call;
    public sealed record FileCall(FileInventoryRecord File, int FolderIndex) : Call;
    public sealed record ErrorCall(ScanErrorRecord Error) : Call;
    public sealed record FolderCall(int Index, int ParentIndex, FolderInventoryRecord Folder, bool FoldersReportExisted) : Call;
    public sealed record Ended(ScanEndOutcome Outcome, ScanTotals? Totals) : Call;

    public List<Call> Calls { get; } = [];
    public Action<FileInventoryRecord>? OnFileHook { get; init; }
    public Action<int>? OnFolderHook { get; init; }
    private string? _runId;

    public void OnScanStarted(in ScanStartInfo start)
    {
        _runId = start.RunId;
        Calls.Add(new Started(start));
    }

    public void OnFile(in FileInventoryRecord file, int folderIndex)
    {
        Calls.Add(new FileCall(file, folderIndex));
        OnFileHook?.Invoke(file);
    }

    public void OnError(ScanErrorRecord error) => Calls.Add(new ErrorCall(error));

    public void OnFolderFinalised(int index, int parentIndex, FolderInventoryRecord folder)
    {
        var existed = outputFolder is not null && File.Exists(Path.Combine(outputFolder, $"Folders_{_runId}.csv"));
        Calls.Add(new FolderCall(index, parentIndex, folder, existed));
        OnFolderHook?.Invoke(index);
    }

    public void OnScanEnded(in ScanEndInfo end) => Calls.Add(new Ended(end.Outcome, end.Totals));

    public IEnumerable<FileCall> Files => Calls.OfType<FileCall>();
    public IEnumerable<ErrorCall> Errors => Calls.OfType<ErrorCall>();
    public IEnumerable<FolderCall> Folders => Calls.OfType<FolderCall>();
}

/// <summary>
/// C1 integration tests of the observer pipeline on real trees: TEST-O1 (SINK-03 on every fixture, final-only folder
/// emission), TEST-O2 (report bytes unchanged by extra observers), TEST-O3 (isolated faults), TEST-O6 (cancellation),
/// TEST-SP1 on real scans, exact names, and the single traversal.
/// </summary>
public static class ObserverPipelineTests
{
    private static PhaseAFixture Fx => PhaseAFixture.Shared;
    private static readonly byte[] Token = RandomNumberGenerator.GetBytes(16);

    private sealed record Run(StorageScanResult Result, ObservedScanOutcome? Outcome, string Output);

    private static Run Plain(string root, bool sort, string label = "plain", string? outputBase = null)
    {
        var output = Path.Combine(outputBase ?? Fx.Base, $"c1_{label}_{Guid.NewGuid().ToString("N")[..6]}");
        var result = new InventoryScanner().Scan(new StorageScanOptions { RootPath = root, OutputPath = output, SortFiles = sort });
        return new Run(result, null, output);
    }

    /// <param name="outputBase">Where the report folder is created: the shared fixture's folder by default; tests on
    /// trees of their own pass a work folder they remove.</param>
    private static Run Observed(string root, bool sort, Func<string, IScanObserver[]> observers, CancellationToken token = default,
        IProgress<StorageScanProgress>? progress = null, string label = "observed", string? outputBase = null)
    {
        var output = Path.Combine(outputBase ?? Fx.Base, $"c1_{label}_{Guid.NewGuid().ToString("N")[..6]}");
        var outcome = new InventoryScanner().ScanObserved(new StorageScanOptions { RootPath = root, OutputPath = output, SortFiles = sort, ProgressInterval = TimeSpan.Zero },
            observers(output), progress, token);
        return new Run(outcome.Scan, outcome, output);
    }

    private static string ContentHash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static void AssertSameReports(StorageScanResult expected, StorageScanResult actual, string context)
    {
        Assert.Equal(expected.State, actual.State, context + ": state");
        Assert.Equal(expected.Totals, actual.Totals, context + ": totals");
        var e = Assert.NotNull(expected.Reports);
        var a = Assert.NotNull(actual.Reports);
        Assert.Equal(ContentHash(e.FilesCsv), ContentHash(a.FilesCsv), context + ": Files report bytes");
        Assert.Equal(ContentHash(e.FoldersCsv), ContentHash(a.FoldersCsv), context + ": Folders report bytes");
        Assert.Equal(ContentHash(e.ErrorsCsv), ContentHash(a.ErrorsCsv), context + ": ScanErrors report bytes");
        Assert.Equal(3, Directory.GetFiles(Path.GetDirectoryName(a.FilesCsv)!).Length, context + ": exactly v1's three files");
    }

    /// <summary>SINK-03 #1 to #4 on one recorded scan.</summary>
    private static void AssertSinkOrder(PipelineRecorder r, StorageScanResult result, string context)
    {
        var calls = r.Calls;
        Assert.True(calls[0] is PipelineRecorder.Started, context + ": OnScanStarted first");
        Assert.Equal(1, calls.Count(c => c is PipelineRecorder.Started), context + ": started once");
        Assert.True(calls[^1] is PipelineRecorder.Ended, context + ": OnScanEnded last");
        Assert.Equal(1, calls.Count(c => c is PipelineRecorder.Ended), context + ": ended once");
        var end = (PipelineRecorder.Ended)calls[^1];
        var expectedOutcome = result.State switch
        {
            ScanCompletionState.Complete or ScanCompletionState.Incomplete => ScanEndOutcome.Finished,
            ScanCompletionState.Cancelled => ScanEndOutcome.Cancelled,
            _ => ScanEndOutcome.Failed,
        };
        Assert.Equal(expectedOutcome, end.Outcome, context + ": end outcome");
        if (result.Finished) Assert.Equal(result.Totals, end.Totals, context + ": end totals are the result's");
        else Assert.Null(end.Totals, context + ": no totals unless finished");

        // #2: files and errors only during enumeration, before any folder; each folder's files form one run.
        var firstFolder = calls.FindIndex(c => c is PipelineRecorder.FolderCall);
        if (firstFolder >= 0)
        {
            Assert.False(calls.Skip(firstFolder).Any(c => c is PipelineRecorder.FileCall or PipelineRecorder.ErrorCall), context + ": no file or error after finalisation began");
        }
        var runStarts = new HashSet<int>();
        var current = -1;
        foreach (var f in r.Files)
        {
            if (f.FolderIndex == current) continue;
            Assert.True(runStarts.Add(f.FolderIndex), $"{context}: the files of folder {f.FolderIndex} are not contiguous");
            current = f.FolderIndex;
        }

        // #4: every folder once, ascending, parent below, root 0/-1, before the Folders report exists; only if finished
        // enumeration and aggregation (a scan cancelled during enumeration emits none).
        var folders = r.Folders.ToList();
        if (folders.Count > 0 || result.Finished)
        {
            Assert.SequenceEqual(Enumerable.Range(0, folders.Count), folders.Select(f => f.Index), context + ": ascending, each once");
            Assert.Equal(-1, folders[0].ParentIndex, context + ": root parent");
            Assert.True(folders.Skip(1).All(f => f.ParentIndex >= 0 && f.ParentIndex < f.Index), context + ": parent below child");
            Assert.False(folders.Any(f => f.FoldersReportExisted), context + ": emitted before the Folders report is written");
            Assert.True(r.Files.All(f => f.FolderIndex < folders.Count), context + ": every file's folder is finalised");
        }
        if (result.Finished)
        {
            Assert.Equal(result.Totals.Folders, (long)folders.Count, context + ": one record per folder");
            Assert.Equal(result.Totals.Files, (long)r.Files.Count(), context + ": one call per file");
            Assert.Equal(result.Root, folders[0].Folder, context + ": the root record is the result's");
        }
    }

    /// <summary>Final-only (SINK-08): every finalised record equals the Folders report's row of the same folder.</summary>
    private static void AssertFoldersAreFinal(PipelineRecorder r, StorageScanResult result, string context)
    {
        var rows = ReportCsvReader.ReadAll(result.Reports!.FoldersCsv).ToDictionary(x => Unguard(x["RelativePath"]), StringComparer.Ordinal);
        foreach (var f in r.Folders)
        {
            var row = rows[f.Folder.RelativePath];
            Assert.Equal(row["ScanStatus"], f.Folder.StatusText, $"{context}: {f.Folder.RelativePath} status");
            Assert.Equal(row["SubtreeComplete"], f.Folder.SubtreeComplete ? "True" : "False", $"{context}: {f.Folder.RelativePath} completeness");
            Assert.Equal(row["TotalSizeBytes"], f.Folder.TotalSizeBytes.ToString(), $"{context}: {f.Folder.RelativePath} total bytes");
            Assert.Equal(row["TotalFileCount"], f.Folder.TotalFileCount.ToString(), $"{context}: {f.Folder.RelativePath} total files");
            Assert.Equal(row["TotalSubfolderCount"], f.Folder.TotalSubfolderCount.ToString(), $"{context}: {f.Folder.RelativePath} total subfolders");
            Assert.Equal(row["DirectFileCount"], f.Folder.DirectFileCount.ToString(), $"{context}: {f.Folder.RelativePath} direct files");
        }
        Assert.Equal(rows.Count, r.Folders.Count(), context + ": every row was emitted");
    }

    private static string Unguard(string v) => v.Length >= 2 && v[0] == '\'' && "=+-@".Contains(v[1]) ? v[1..] : v;

    /// <summary>SINK-03 #5: OnFile and OnError are exactly v1's engine calls (the engine on its own, v1's sink seam).</summary>
    private static void AssertSameAsEngineAlone(PipelineRecorder r, string root, string context)
    {
        var alone = new CollectingSink();
        new ScanEngine(root, alone, new HashSet<string>()).Run(CancellationToken.None);
        Assert.SequenceEqual(alone.Files.Select(f => $"{f.FolderIndex}|{f.File}"), r.Files.Select(f => $"{f.FolderIndex}|{f.File}"), context + ": OnFile sequence");
        Assert.SequenceEqual(alone.Errors.Select(e => e.ToString()), r.Errors.Select(e => e.Error.ToString()), context + ": OnError sequence");
    }

    private static IEnumerable<(string Name, string Root)> Fixtures() =>
    [
        ("Phase A fixture", Fx.Root),
        ("readable subtree", Path.Combine(Fx.Root, "Kpop", "TWICE")),
        ("Unicode and bracket subtree", Directory.GetDirectories(Fx.Root, "Weird*")[0]),
    ];

    [Test]
    public static void TEST_O1_order_invariants_hold_on_every_fixture()
    {
        Snapshot.Settle(Fx.Root);
        foreach (var (name, root) in Fixtures())
        {
            foreach (var sort in new[] { true, false })
            {
                var context = $"{name}, {(sort ? "sorted" : "unsorted")}";
                PipelineRecorder? recorder = null;
                var run = Observed(root, sort, output => [recorder = new PipelineRecorder(output)]);
                Assert.True(run.Result.Finished, context);
                AssertSinkOrder(recorder!, run.Result, context);
                AssertFoldersAreFinal(recorder!, run.Result, context);
                AssertSameAsEngineAlone(recorder!, root, context);
            }
        }
    }

    [Test]
    public static void TEST_O1_order_invariants_hold_on_empty_deep_and_wide_trees()
    {
        var root = TestEnvironment.NewWorkFolder("c1_shapes");
        try
        {
            var empty = Path.Combine(root, "empty");
            Directory.CreateDirectory(empty);
            var deep = Path.Combine(root, "deep");
            Directory.CreateDirectory(Path.Combine([deep, .. Enumerable.Repeat("d", 120)]));
            File.WriteAllBytes(Path.Combine([deep, .. Enumerable.Repeat("d", 120), "bottom.bin"]), new byte[3]);
            var wide = Path.Combine(root, "wide");
            for (var i = 0; i < 300; i++)
            {
                Directory.CreateDirectory(Path.Combine(wide, $"w{i:D3}"));
                File.WriteAllBytes(Path.Combine(wide, $"w{i:D3}", "f.bin"), new byte[i % 7]);
            }
            foreach (var tree in new[] { empty, deep, wide })
            {
                PipelineRecorder? recorder = null;
                var run = Observed(tree, true, output => [recorder = new PipelineRecorder(output)], label: "shape", outputBase: root);
                AssertSinkOrder(recorder!, run.Result, Path.GetFileName(tree));
                AssertFoldersAreFinal(recorder!, run.Result, Path.GetFileName(tree));
            }
        }
        finally { TestEnvironment.RemoveTree(root); }
    }

    [Test]
    public static void TEST_O1_a_folder_is_emitted_only_with_its_final_status()
    {
        // SINK-08: while enumeration runs, a folder's state is provisional. Here two folders are listed as ordinary
        // folders, then change before they are entered: one becomes a junction, one disappears. Each is emitted once,
        // after aggregation, with its final status; no observer ever sees it as "Ok".
        var root = TestEnvironment.NewWorkFolder("c1_final");
        var outside = TestEnvironment.NewWorkFolder("c1_final_outside");
        var reports = TestEnvironment.NewWorkFolder("c1_final_reports");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Swap"));
            File.WriteAllBytes(Path.Combine(root, "Swap", "inside.bin"), new byte[5]);
            Directory.CreateDirectory(Path.Combine(root, "Vanish", "deeper"));
            File.WriteAllBytes(Path.Combine(root, "z.txt"), new byte[1]);   // listed after both folders
            var changed = false;
            PipelineRecorder? recorder = null;
            var run = Observed(root, true, output => [recorder = new PipelineRecorder(output)
            {
                OnFileHook = f =>
                {
                    if (f.FileName != "z.txt" || changed) return;
                    Directory.Delete(Path.Combine(root, "Swap"), recursive: true);   // the test's own folders
                    changed = TestEnvironment.CreateJunction(Path.Combine(root, "Swap"), outside);
                    Directory.Delete(Path.Combine(root, "Vanish"), recursive: true);
                },
            }], outputBase: reports);
            Assert.True(changed, "the test could not create the junction");
            AssertSinkOrder(recorder!, run.Result, "changed folders");
            var swap = recorder!.Folders.Single(f => f.Folder.RelativePath == "Swap");
            Assert.Equal(FolderScanStatus.ReparsePointSkipped, swap.Folder.Status, "the junction's only emission is final");
            var vanish = recorder.Folders.Single(f => f.Folder.RelativePath == "Vanish");
            Assert.Equal(FolderScanStatus.Unreadable, vanish.Folder.Status, "the vanished folder's only emission is final");
            Assert.Equal(ScanErrorType.NotFound, vanish.Folder.StatusReason);
            Assert.False(vanish.Folder.SubtreeComplete);
            Assert.False(recorder.Folders.First().Folder.SubtreeComplete, "the root's final completeness reflects it");
            Assert.Equal(ScanCompletionState.Incomplete, run.Result.State);
            var lastFile = recorder.Calls.FindLastIndex(c => c is PipelineRecorder.FileCall);
            var firstFolder = recorder.Calls.FindIndex(c => c is PipelineRecorder.FolderCall);
            Assert.True(firstFolder > lastFile, "no folder is announced while enumeration can still change it");
        }
        finally
        {
            TestEnvironment.RemoveTree(root);
            TestEnvironment.RemoveTree(outside);
            TestEnvironment.RemoveTree(reports);
        }
    }

    [Test]
    public static void TEST_O2_report_bytes_are_identical_with_and_without_extra_observers()
    {
        Snapshot.Settle(Fx.Root);
        foreach (var (name, root) in Fixtures())
        {
            foreach (var sort in new[] { true, false })
            {
                var context = $"{name}, {(sort ? "sorted" : "unsorted")}";
                var plain = Plain(root, sort);
                using var spool = new MemoryStream();
                using var writer = new SpoolWriter(spool, Token);
                var observed = Observed(root, sort, _ => [new PipelineRecorder(), writer, new PipelineRecorder()]);
                AssertSameReports(plain.Result, observed.Result, context);
                Assert.Equal(0, observed.Outcome!.Faults.Count, context + ": no observer failed");
                Assert.Equal(SpoolWriterState.Sealed, writer.State, context);
            }
        }
    }

    [Test]
    public static void TEST_O3_an_isolated_class_A_failure_at_any_point_leaves_the_reports_identical()
    {
        Snapshot.Settle(Fx.Root);
        var plain = Plain(Fx.Root, true);
        var rng = new Random(1_100_000);
        var points = new List<(string Callback, int At)>
        {
            (nameof(IScanObserver.OnScanStarted), 0), (nameof(IScanObserver.OnScanEnded), 0), (nameof(IScanObserver.OnFolderFinalised), 0),
            (nameof(IScanObserver.OnError), 0), (nameof(IScanObserver.OnFile), 0),
        };
        for (var i = 0; i < 6; i++) points.Add((nameof(IScanObserver.OnFile), rng.Next((int)plain.Result.Totals.Files)));
        for (var i = 0; i < 3; i++) points.Add((nameof(IScanObserver.OnFolderFinalised), rng.Next((int)plain.Result.Totals.Folders)));
        points.Add((nameof(IScanObserver.OnError), rng.Next(2)));

        foreach (var (callback, at) in points)
        {
            var context = $"IOException in {callback} call {at}";
            var thrower = new FaultingObserver(callback, at);
            var witness = new PipelineRecorder();
            var observed = Observed(Fx.Root, true, _ => [thrower, witness]);
            AssertSameReports(plain.Result, observed.Result, context);
            var fault = observed.Outcome!.Faults.Single();
            Assert.True(ReferenceEquals(thrower, fault.Observer), context);
            Assert.Equal(callback, fault.Callback, context);
            Assert.True(fault.Exception.GetType() == typeof(IOException), $"{context}: OutputGuard is not involved ({fault.Exception.GetType().Name})");
            Assert.Equal(callback == nameof(IScanObserver.OnScanEnded) ? 0 : 1, thrower.CallsAfterFault, context + ": only OnScanEnded after the fault");
            Assert.Equal(plain.Result.Totals.Files, (long)witness.Files.Count(), context + ": the other observer saw everything");
        }
    }

    private sealed class FaultingObserver(string callback, int at) : IScanObserver
    {
        private int _count;
        private bool _faulted;
        public int CallsAfterFault { get; private set; }

        private void Call(string name)
        {
            if (_faulted) { CallsAfterFault++; return; }
            if (name == callback && _count++ == at)
            {
                _faulted = true;
                throw new IOException("simulated isolated write failure");
            }
        }

        public void OnScanStarted(in ScanStartInfo start) => Call(nameof(OnScanStarted));
        public void OnFile(in FileInventoryRecord file, int folderIndex) => Call(nameof(OnFile));
        public void OnError(ScanErrorRecord error) => Call(nameof(OnError));
        public void OnFolderFinalised(int index, int parentIndex, FolderInventoryRecord folder) => Call(nameof(OnFolderFinalised));

        public void OnScanEnded(in ScanEndInfo end)
        {
            if (_faulted) CallsAfterFault++;
            else Call(nameof(OnScanEnded));
        }
    }

    [Test]
    public static void TEST_O6_cancellation_latency_with_the_spool_writer_is_within_v1s_bound()
    {
        // v1's bound (ScanEngineTests): the token is checked between folders and every 256 entries.
        var root = TestEnvironment.NewWorkFolder("c1_cancel");
        var reports = TestEnvironment.NewWorkFolder("c1_cancel_reports");
        try
        {
            for (var i = 0; i < 3000; i++) File.WriteAllBytes(Path.Combine(root, $"f{i:D4}.bin"), []);
            using var cts = new CancellationTokenSource();
            var afterCancel = 0;
            using var spool = new MemoryStream();
            using var writer = new SpoolWriter(spool, Token);
            var recorder = new PipelineRecorder
            {
                OnFileHook = f => { if (cts.IsCancellationRequested) afterCancel++; else if (f.FileName == "f0100.bin") cts.Cancel(); },
            };
            var run = Observed(root, true, _ => [writer, recorder], cts.Token, outputBase: reports);
            Assert.Equal(ScanCompletionState.Cancelled, run.Result.State);
            Assert.True(afterCancel <= 256, $"{afterCancel} files delivered after cancellation");
            Assert.Equal(SpoolWriterState.Abandoned, writer.State, "a cancelled scan never seals the spool");
            Assert.False(SpoolReader.Verify(spool, Token, null).Passed, "and the stream can never pass V");
            AssertSinkOrder(recorder, run.Result, "cancelled during enumeration");
            Assert.Equal(0, recorder.Folders.Count(), "no folder is finalised for a scan cancelled during enumeration");
        }
        finally
        {
            TestEnvironment.RemoveTree(root);
            TestEnvironment.RemoveTree(reports);
        }
    }

    /// <summary>More than 1,024 folders and 16,384 files: v1 checks the token every 1,024 rows of the Folders report and
    /// every 16,384 rows of the sorted copy, so both checks are reached.</summary>
    private static readonly Lazy<string> StageTree = new(() =>
    {
        var root = TestEnvironment.NewWorkFolder("c1_stages");
        for (var d = 0; d < 1050; d++)
        {
            var dir = Path.Combine(root, $"d{d:D4}");
            Directory.CreateDirectory(dir);
            for (var f = 0; f < 17; f++) File.WriteAllBytes(Path.Combine(dir, $"f{f:D2}.bin"), new byte[(d + f) % 5]);
        }
        Snapshot.Settle(root);
        return root;
    });

    [Test]
    public static void Cancellation_at_every_stage_never_leaves_a_sealed_spool_and_keeps_v1s_artefacts()
    {
        var reports = TestEnvironment.NewWorkFolder("c1_stage_reports");
        // CAN-01a/b (enumeration; the spool writes inside callbacks), folder finalisation, CAN-01c (Folders report and
        // sorted copy). In every case the result is v1's Cancelled, only v1's report files exist (C1 creates no spool
        // file), and the spool never passes V.
        try
        {
            var stages = new (string Stage, Func<CancellationTokenSource, PipelineRecorder> Recorder, Func<CancellationTokenSource, IProgress<StorageScanProgress>?> Progress)[]
            {
                ("enumeration", cts => { var n = 0; return new PipelineRecorder { OnFileHook = _ => { if (++n == 500) cts.Cancel(); } }; }, _ => null),
                ("folder finalisation", cts => new PipelineRecorder { OnFolderHook = i => { if (i == 3) cts.Cancel(); } }, _ => null),
                ("Folders report", _ => new PipelineRecorder(), cts => new InlineProgress(p => { if (p.Phase == ScanPhase.WritingFoldersReport) cts.Cancel(); })),
                ("sorted Files report", _ => new PipelineRecorder(), cts => new InlineProgress(p => { if (p.Phase == ScanPhase.WritingFilesReport) cts.Cancel(); })),
            };
            foreach (var (stage, makeRecorder, makeProgress) in stages)
            {
                using var cts = new CancellationTokenSource();
                using var spool = new MemoryStream();
                using var writer = new SpoolWriter(spool, Token);
                var recorder = makeRecorder(cts);
                var run = Observed(StageTree.Value, true, _ => [writer, recorder], cts.Token, makeProgress(cts), label: "stage", outputBase: reports);
                Assert.Equal(ScanCompletionState.Cancelled, run.Result.State, stage);
                Assert.Null(run.Result.Reports, stage + ": no reports are presented as valid");
                Assert.True(run.Result.IncompleteArtifacts.Count >= 2, stage + ": created reports are listed as incomplete, as in v1");
                Assert.SequenceEqual(run.Result.IncompleteArtifacts.Order(StringComparer.OrdinalIgnoreCase), Directory.GetFiles(run.Output).Order(StringComparer.OrdinalIgnoreCase),
                    stage + ": the report folder holds exactly v1's incomplete reports (no spool file in C1)");
                Assert.Equal(SpoolWriterState.Abandoned, writer.State, stage);
                Assert.False(SpoolReader.Verify(spool, Token, null).Passed, stage + ": the spool never passes V");
                AssertSinkOrder(recorder, run.Result, stage);
                Assert.Equal(ScanEndOutcome.Cancelled, ((PipelineRecorder.Ended)recorder.Calls[^1]).Outcome, stage);
            }
        }
        finally
        {
            TestEnvironment.RemoveTree(StageTree.Value);
            TestEnvironment.RemoveTree(reports);
        }
    }

    [Test]
    public static void TEST_SP1_a_real_scans_spool_reads_back_exactly_what_the_observers_saw()
    {
        Snapshot.Settle(Fx.Root);
        foreach (var sort in new[] { true, false })
        {
            using var spool = new MemoryStream();
            using var writer = new SpoolWriter(spool, Token);
            var recorder = new PipelineRecorder();
            var run = Observed(Fx.Root, sort, _ => [recorder, writer]);
            Assert.True(run.Result.Finished);
            Assert.Equal(SpoolWriterState.Sealed, writer.State);
            var v = SpoolReader.Verify(spool, Token, run.Result.Totals);
            Assert.True(v.Passed, $"{v.Defect} {v.Detail}");
            var s = Assert.NotNull(v.Spool);
            Assert.Equal(run.Result.RunId, s.Header.RunId);

            var expected = recorder.Calls.Where(c => c is PipelineRecorder.FileCall or PipelineRecorder.ErrorCall).Select(c => c switch
            {
                PipelineRecorder.FileCall f => $"F|{f.FolderIndex}|{f.File.FileName}|{f.File.SizeBytes}|{f.File.CreatedUtc?.Ticks}|{f.File.ModifiedUtc?.Ticks}|{f.File.LastAccessUtc?.Ticks}|{f.File.Attributes}",
                PipelineRecorder.ErrorCall e => $"E|{e.Error.Type}|{e.Error.Path}|{e.Error.Message}",
                _ => "",
            });
            var actual = s.ReadRecordRegion().Select(r => r switch
            {
                SpoolFileRecord f => $"F|{f.FolderIndex}|{f.Name}|{f.SizeBytes}|{f.CreatedUtc?.Ticks}|{f.ModifiedUtc?.Ticks}|{f.LastAccessUtc?.Ticks}|{f.Attributes}",
                SpoolErrorRecord e => $"E|{e.Type}|{e.Path}|{e.Message}",
                _ => "",
            });
            Assert.SequenceEqual(expected, actual, "records, physical order");
            Assert.SequenceEqual(recorder.Folders.Select(f => $"{f.Index}|{f.ParentIndex}|{f.Folder.Name}|{f.Folder.Status}|{f.Folder.StatusReason}|{f.Folder.SubtreeComplete}|{f.Folder.TotalSizeBytes}|{f.Folder.TotalFileCount}"),
                s.ReadFolders().Select(f => $"{f.Index}|{f.ParentIndex}|{f.Name}|{f.Status}|{f.StatusReason}|{f.SubtreeComplete}|{f.TotalSizeBytes}|{f.TotalFileCount}"), "folders");
            Console.WriteLine($"      {(sort ? "sorted" : "unsorted")}: spool {spool.Length:N0} bytes for {run.Result.Totals.Files} files, {run.Result.Totals.Folders} folders");
        }
    }

    [Test]
    public static void Exact_filesystem_names_survive_the_spool_unnormalised()
    {
        // Legal NTFS names that a careless codec would change: an unpaired surrogate on each side, NFC and NFD
        // spellings of one word as two siblings, trailing dots and spaces (created through \\?\), leading formula
        // characters, and a 255-unit name. Created by the test only; their contents are never read.
        var root = TestEnvironment.NewWorkFolder("c1_names");
        var reports = TestEnvironment.NewWorkFolder("c1_names_reports");
        try
        {
            var names = new[]
            {
                "a\uD800b.bin", "\uDC00tail.bin", "caf\u00E9.txt", "cafe\u0301.txt", "trailing dot.", "trailing space ", "=SUM(1).csv",
                "\uD83D\uDE00 emoji.png", "한국어.mp3", new string('n', 251) + ".bin",
            };
            var created = new List<string>();
            foreach (var n in names)
            {
                try
                {
                    using var fs = new FileStream(@"\\?\" + Path.Combine(root, n), FileMode.CreateNew, FileAccess.Write);
                    created.Add(n);
                }
                catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { }
            }
            Assert.True(created.Count >= 8, "the volume accepted too few test names: " + created.Count);
            var listed = Directory.EnumerateFileSystemEntries(@"\\?\" + root).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();

            using var spool = new MemoryStream();
            using var writer = new SpoolWriter(spool, Token);
            var recorder = new PipelineRecorder();
            var run = Observed(root, true, _ => [recorder, writer], label: "names", outputBase: reports);
            Assert.True(run.Result.Finished);
            var v = SpoolReader.Verify(spool, Token, run.Result.Totals);
            Assert.True(v.Passed, $"{v.Defect} {v.Detail}");
            var fromSpool = v.Spool!.ReadRecordRegion().OfType<SpoolFileRecord>().Select(f => f.Name).Order(StringComparer.Ordinal).ToList();
            var fromObserver = recorder.Files.Select(f => f.File.FileName).Order(StringComparer.Ordinal).ToList();
            Assert.SequenceEqual(listed, fromObserver, "observer names equal the independent listing (ordinal)");
            Assert.SequenceEqual(fromObserver, fromSpool, "spool names equal the observed names (ordinal, code unit for code unit)");
            Assert.True(fromSpool.Contains("caf\u00E9.txt") && fromSpool.Contains("cafe\u0301.txt"), "NFC and NFD siblings stay two names");
            AssertSameReports(Plain(root, true, "names_plain", reports).Result, run.Result, "names");
        }
        finally
        {
            // Names with trailing dots or spaces can be removed only through \\?\ paths.
            foreach (var f in Directory.Exists(root) ? Directory.EnumerateFiles(@"\\?\" + root).ToList() : []) File.Delete(f);
            TestEnvironment.RemoveTree(root);
            TestEnvironment.RemoveTree(reports);
        }
    }

    [Test]
    public static void One_traversal_feeds_every_observer()
    {
        // SINK-01: the observers receive the same objects from one emission, not equal values from separate walks.
        Snapshot.Settle(Fx.Root);
        var a = new PipelineRecorder();
        var b = new PipelineRecorder();
        var run = Observed(Fx.Root, true, _ => [a, b]);
        Assert.True(run.Result.Finished);
        var aErrors = a.Errors.ToList();
        var bErrors = b.Errors.ToList();
        Assert.True(aErrors.Count > 0, "fixture has error rows");
        Assert.Equal(aErrors.Count, bErrors.Count);
        for (var i = 0; i < aErrors.Count; i++) Assert.True(ReferenceEquals(aErrors[i].Error, bErrors[i].Error), $"error {i}: one record object, fanned out");
        var aFiles = a.Files.ToList();
        var bFiles = b.Files.ToList();
        Assert.Equal(run.Result.Totals.Files, (long)aFiles.Count);
        for (var i = 0; i < aFiles.Count; i++)
        {
            Assert.True(ReferenceEquals(aFiles[i].File.FullPath, bFiles[i].File.FullPath), $"file {i}: one record, fanned out");
        }
        var aFolders = a.Folders.ToList();
        var bFolders = b.Folders.ToList();
        for (var i = 0; i < aFolders.Count; i++) Assert.True(ReferenceEquals(aFolders[i].Folder, bFolders[i].Folder), $"folder {i}: one record, fanned out");
    }

    [Test]
    public static void An_observed_scan_with_the_spool_does_not_change_the_tree()
    {
        _ = Plain(Fx.Root, true);   // settle NTFS timestamps first, as the v1 test does
        Snapshot.Settle(Fx.Root);
        var listing = Snapshot.Take(Fx.Root);
        var records = Snapshot.Take(Fx.Root, trueValues: true);
        using var spool = new MemoryStream();
        using var writer = new SpoolWriter(spool, Token);
        var run = Observed(Fx.Root, true, _ => [writer, new PipelineRecorder()]);
        Assert.True(run.Result.Finished);
        Assert.Equal(listing, Snapshot.Take(Fx.Root), "listing view");
        Assert.Equal(records, Snapshot.Take(Fx.Root, trueValues: true), "per-item records");
    }
}

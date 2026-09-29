using System.Windows.Controls;
using System.Windows.Media;
using StorageInventory.App.Services;
using StorageInventory.App.ViewModels;
using StorageInventory.Core;
using StorageInventory.Core.Reports;
using StorageInventory.Testing;

namespace StorageInventory.IntegrationTests;

public static class ExplorationTests
{
    private static PhaseAFixture Fx => PhaseAFixture.Shared;

    private static StorageScanResult Scan(string root, bool sort)
    {
        var r = new InventoryScanner().Scan(new StorageScanOptions
        {
            RootPath = root, OutputPath = Path.Combine(Fx.Base, "explore_" + Guid.NewGuid().ToString("N")[..6]), SortFiles = sort,
        });
        Assert.True(r.Finished);
        return r;
    }

    private static string TenThousandTree
    {
        get
        {
            var t = Path.Combine(Path.GetTempPath(), "StorageInventoryPerf", "tree_10000");
            if (!Directory.Exists(t)) Assert.Skip("needs %TEMP%\\StorageInventoryPerf\\tree_10000 (many equal file sizes)");
            return t;
        }
    }

    [Test]
    public static void Largest_files_from_an_unsorted_scan_equal_the_sorted_reports_first_rows_including_ties()
    {
        var tree = TenThousandTree;
        var sorted = ReportExplorer.LargestFiles(Scan(tree, sort: true), 50);
        var unsorted = ReportExplorer.LargestFiles(Scan(tree, sort: false), 50);
        Assert.Equal(50, sorted.Count);
        Assert.SequenceEqual(sorted.Select(f => f.RelativePath + "|" + f.SizeBytes), unsorted.Select(f => f.RelativePath + "|" + f.SizeBytes),
            "top-N selection must reproduce the specified order (size descending, discovery order for ties)");
        Assert.True(sorted.Select(f => f.SizeBytes).Distinct().Count() < 50, "the sample really contains ties");
    }

    [Test]
    public static void File_type_totals_add_up_to_the_scan_totals()
    {
        var scan = Scan(Fx.Root, sort: true);
        var types = ReportExplorer.FileTypes(scan, 500);
        Assert.Equal(scan.Totals.Files, types.Categories.Sum(c => c.Files));
        Assert.Equal(scan.Totals.Bytes, types.Categories.Sum(c => c.Bytes));
        Assert.Equal(scan.Totals.Files, types.Extensions.Sum(c => c.Files), "every extension listed for a small tree");
        Assert.Equal("Video", types.Categories[0].Name, "largest category first (clip.mkv, 1 MB)");
        Assert.True(types.Extensions.Any(e => e.Name == "(none)" && e.Category == "No Extension"));
    }

    [Test]
    public static void Errors_are_summarised_by_type_over_the_whole_report()
    {
        var scan = Scan(Fx.Root, sort: true);
        var errors = ReportExplorer.Errors(scan, 2);
        Assert.Equal(2, errors.Rows.Count, "row limit respected");
        Assert.Equal(scan.ErrorCounts.Values.Sum(), errors.TotalRows);
        Assert.Equal(2L, errors.CountsByType["AccessDenied"]);
        Assert.Equal(2L, errors.CountsByType["ReparsePointSkipped"]);
    }

    [Test]
    public static void Names_are_shown_exactly_as_on_disk_not_as_guarded_report_text()
    {
        var scan = Scan(Fx.Root, sort: true);
        var files = ReportExplorer.LargestFiles(scan, 1000);
        Assert.True(files.Any(f => f.RelativePath == "=HYPERLINK(1).txt"), "formula-like name shown unguarded in the app");
        Assert.True(files.Any(f => f.RelativePath == "'apostrophe-start.txt"), "a real leading apostrophe is kept");
        Assert.True(files.Any(f => f.RelativePath == @"-Dash Folder\@inner.txt"));
        var folders = ReportExplorer.LargestFolders(scan, 1000);
        Assert.Equal(".", folders[0].RelativePath);
        Assert.Equal(scan.Totals.Folders, (long)folders.Count);
    }

    [Test]
    public static void Only_finished_scans_can_be_explored()
    {
        var cancelled = StorageScanResult.ForCancelled("20260101_000000_abcdef", Fx.Root, Fx.Base, new ScanTotals(), new Dictionary<ScanErrorType, long>(), [], new PhaseTimings());
        Assert.Throws<InvalidOperationException>(() => ReportExplorer.LargestFiles(cancelled, 10));
    }

    [Test]
    public static void The_launcher_only_accepts_this_runs_reports_and_folder()
    {
        var scan = Scan(Path.Combine(Fx.Root, "Kpop"), sort: true);
        var r = scan.Reports!;
        foreach (var ok in new[] { r.FilesCsv, r.FoldersCsv, r.ErrorsCsv, scan.OutputPath, r.FilesCsv.ToUpperInvariant() })
            Assert.True(ReportOpener.IsAllowed(scan, null, ok), ok);
        foreach (var bad in new[] { Path.Combine(Fx.Root, "a.txt"), Fx.Root, @"C:\Windows\System32\cmd.exe", r.FilesCsv + ".exe", "calc", "" })
            Assert.False(ReportOpener.IsAllowed(scan, null, bad), bad);
        Assert.Throws<InvalidOperationException>(() => new ReportOpener(scan, null).Open(@"C:\Windows\System32\cmd.exe"));
        var cancelled = StorageScanResult.ForCancelled(scan.RunId, scan.RootPath, scan.OutputPath, new ScanTotals(), new Dictionary<ScanErrorType, long>(), [r.FilesCsv], new PhaseTimings());
        Assert.False(ReportOpener.IsAllowed(cancelled, null, r.FilesCsv), "incomplete runs expose nothing to open");
    }

    private sealed class RecordingOpener : IReportOpener
    {
        public List<string> Opened { get; } = [];
        public void Open(string path) => Opened.Add(path);
    }

    [Test]
    public static void Result_actions_open_exactly_the_matching_report_and_only_when_clicked()
    {
        var scan = Scan(Path.Combine(Fx.Root, "Kpop"), sort: true);
        var opener = new RecordingOpener();
        var vm = UiHost.Invoke(() => new ResultsViewModel(scan, null, TimeSpan.FromSeconds(3), opener));
        Assert.Equal(0, opener.Opened.Count, "nothing is opened automatically");
        UiHost.Invoke(() =>
        {
            vm.OpenFilesReportCommand.Execute(null);
            vm.OpenFoldersReportCommand.Execute(null);
            vm.OpenErrorsReportCommand.Execute(null);
            vm.OpenReportFolderCommand.Execute(null);
        });
        Assert.SequenceEqual([scan.Reports!.FilesCsv, scan.Reports.FoldersCsv, scan.Reports.ErrorsCsv, scan.OutputPath], opener.Opened);
        Assert.False(UiHost.Invoke(() => vm.OpenWorkbookCommand.CanExecute(null)), "no workbook, no workbook action");
    }

    [Test]
    public static void Results_screen_loads_the_exploration_tabs()
    {
        var shots = Environment.GetEnvironmentVariable("SI_SCREENSHOTS") is { Length: > 0 } d ? d : Path.Combine(Fx.Base, "screenshots");
        var (w, vm) = UiSetupTests.OpenWindow();
        try
        {
            UiHost.Invoke(() => { vm.SourcePath = Fx.Root; vm.OutputPath = Path.Combine(Fx.Base, "explore-ui-" + Guid.NewGuid().ToString("N")[..6]); });
            Assert.True(UiHost.WaitUntil(() => vm.StartScanCommand.CanExecute(null), TimeSpan.FromSeconds(10)));
            UiHost.Invoke(() => vm.StartScanCommand.Execute(null));
            Assert.True(UiHost.WaitUntil(() => vm.Results is { Exploration.IsLoading: false }, TimeSpan.FromSeconds(60)), "details loaded");
            var ex = UiHost.Invoke(() => vm.Results!.Exploration);
            Assert.Equal("", ex.Status, "no load error");
            Assert.True(ex.Files.Count > 0 && ex.Folders.Count > 0 && ex.Categories.Count > 0 && ex.Errors.Count > 0);
            Assert.Contains("AccessDenied: 2", ex.ErrorsNote);
            UiHost.Render(w, Path.Combine(shots, "results-overview-light.png"));
            UiHost.Invoke(() => FindTabs(w)!.SelectedIndex = 2);
            UiHost.Render(w, Path.Combine(shots, "results-largest-files-light.png"));
        }
        finally { UiSetupTests.Close(w); }
    }

    private static TabControl? FindTabs(System.Windows.DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TabControl t) return t;
            if (FindTabs(child) is { } found) return found;
        }
        return null;
    }
}

using System.Windows;
using StorageInventory.App.ViewModels;
using StorageInventory.Core;
using StorageInventory.Testing;

namespace StorageInventory.IntegrationTests;

/// <summary>The live scan experience, driven through the real window and view model.</summary>
public static class UiScanTests
{
    private static PhaseAFixture Fx => PhaseAFixture.Shared;

    private static string ScreenshotFolder =>
        Environment.GetEnvironmentVariable("SI_SCREENSHOTS") is { Length: > 0 } dir ? dir : Path.Combine(Fx.Base, "screenshots");   // removed with the shared fixture

    /// <summary>A tree big enough to take a few seconds: the kept 60k benchmark tree, else the fixture.</summary>
    private static string LargeTree
    {
        get
        {
            var tree = Path.Combine(Path.GetTempPath(), "StorageInventoryPerf", "tree_60000");
            if (!Directory.Exists(tree)) Assert.Skip("needs %TEMP%\\StorageInventoryPerf\\tree_60000 for a scan long enough to interrupt");
            return tree;
        }
    }

    private static string NewOutput() => Path.Combine(Fx.Base, "ui-scan-" + Guid.NewGuid().ToString("N")[..6]);

    private static void StartScan(MainViewModel vm, string source, string output)
    {
        UiHost.Invoke(() => { vm.SourcePath = source; vm.OutputPath = output; vm.CreateWorkbook = false; });
        Assert.True(UiHost.WaitUntil(() => vm.StartScanCommand.CanExecute(null), TimeSpan.FromSeconds(10)), "pre-flight ready");
        UiHost.Invoke(() => vm.StartScanCommand.Execute(null));
    }

    private static void WaitForResults(MainViewModel vm, int seconds = 120) =>
        Assert.True(UiHost.WaitUntil(() => vm.Stage == AppStage.Results, TimeSpan.FromSeconds(seconds)), "results shown");

    private static void AssertAllClosed(IEnumerable<string> files)
    {
        foreach (var f in files)
        {
            using var exclusive = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.None);   // throws if still open
        }
    }

    [Test]
    public static void A_complete_scan_shows_SCAN_COMPLETE_and_the_top_level_folders()
    {
        var (w, vm) = UiSetupTests.OpenWindow();
        try
        {
            StartScan(vm, Path.Combine(Fx.Root, "Kpop", "TWICE"), NewOutput());
            WaitForResults(vm);
            var results = UiHost.Invoke(() => vm.Results!);
            Assert.Equal(ScanCompletionState.Complete, results.State);
            Assert.Equal("SCAN COMPLETE", results.Headline);
            Assert.Equal("Complete", results.Completeness);
            Assert.False(results.HasIncompleteArtifacts);
        }
        finally { UiSetupTests.Close(w); }
    }

    [Test]
    public static void A_scan_with_unreadable_folders_is_never_shown_as_complete()
    {
        var (w, vm) = UiSetupTests.OpenWindow();
        try
        {
            StartScan(vm, Fx.Root, NewOutput());
            WaitForResults(vm);
            var results = UiHost.Invoke(() => vm.Results!);
            Assert.Equal(ScanCompletionState.Incomplete, results.State);
            Assert.Equal("SCAN FINISHED, BUT INCOMPLETE", results.Headline);
            Assert.Contains("lower bounds", results.Explanation);
            Assert.Equal("Kpop", results.TopFolders[0].Folder);
            Assert.Equal("No", results.TopFolders.Single(f => f.Folder == "Denied").Complete);
            Assert.Equal("Link (not followed)", results.TopFolders.Single(f => f.Folder == "Loop").Complete);
            UiHost.Render(w, Path.Combine(ScreenshotFolder, "results-incomplete-light.png"));
        }
        finally { UiSetupTests.Close(w); }
    }

    [Test]
    public static void Progress_is_indeterminate_while_enumerating_and_cancel_stops_safely()
    {
        var tree = LargeTree;
        var (w, vm) = UiSetupTests.OpenWindow();
        var output = NewOutput();
        try
        {
            StartScan(vm, tree, output);
            Assert.True(UiHost.WaitUntil(() => vm.Stage == AppStage.Scanning && vm.Progress.Files != "0", TimeSpan.FromSeconds(30)), "counters move");
            Assert.True(UiHost.Invoke(() => vm.Progress.IsIndeterminate), "no invented percentage while the total is unknown");
            UiHost.Render(w, Path.Combine(ScreenshotFolder, "scanning-light.png"));

            UiHost.Invoke(() => vm.CancelScanCommand.Execute(null));
            Assert.Contains("Stopping safely", UiHost.Invoke(() => vm.Progress.Phase));
            WaitForResults(vm);
            var results = UiHost.Invoke(() => vm.Results!);
            Assert.Equal(ScanCompletionState.Cancelled, results.State);
            Assert.Equal("SCAN CANCELLED", results.Headline);
            Assert.True(results.HasIncompleteArtifacts, "incomplete files are listed");
            AssertAllClosed(results.IncompleteArtifacts);
            Assert.SequenceEqual(Directory.GetFiles(output).Order(StringComparer.OrdinalIgnoreCase), results.IncompleteArtifacts.Order(StringComparer.OrdinalIgnoreCase),
                "every file the run created is identified as incomplete");
            UiHost.Render(w, Path.Combine(ScreenshotFolder, "results-cancelled-light.png"));

            UiHost.Invoke(() => vm.NewScanCommand.Execute(null));
            Assert.Equal(AppStage.Setup, UiHost.Invoke(() => vm.Stage));
            Assert.True(UiHost.WaitUntil(() => vm.StartScanCommand.CanExecute(null), TimeSpan.FromSeconds(10)), "the app is usable again");
        }
        finally { UiSetupTests.Close(w); }
    }

    [Test]
    public static void Closing_the_window_during_a_scan_waits_until_the_reports_are_closed()
    {
        var tree = LargeTree;
        var (w, vm) = UiSetupTests.OpenWindow();
        var output = NewOutput();
        var closed = false;
        UiHost.Invoke(() =>
        {
            w.ConfirmStopAndClose = () => true;
            w.Closed += (_, _) => closed = true;
        });
        StartScan(vm, tree, output);
        Assert.True(UiHost.WaitUntil(() => vm.Stage == AppStage.Scanning && vm.Progress.Files != "0", TimeSpan.FromSeconds(30)), "scan running");

        UiHost.Invoke(w.Close);
        Assert.True(UiHost.Invoke(() => w.IsVisible || closed), "window still open or already closed after the scan stopped");
        Assert.True(UiHost.WaitUntil(() => closed, TimeSpan.FromSeconds(60)), "window closes once the scan has stopped");
        Assert.False(UiHost.Invoke(() => vm.IsBusy), "no scan left running");
        AssertAllClosed(Directory.GetFiles(output));
        Assert.Equal(0, Directory.GetFiles(output, "Folders_*").Length, "the cancelled run produced no Folders report");
    }

    [Test]
    public static void Declining_to_close_keeps_the_scan_running()
    {
        var tree = LargeTree;
        var (w, vm) = UiSetupTests.OpenWindow();
        try
        {
            UiHost.Invoke(() => w.ConfirmStopAndClose = () => false);
            StartScan(vm, tree, NewOutput());
            Assert.True(UiHost.WaitUntil(() => vm.Stage == AppStage.Scanning && vm.Progress.Files != "0", TimeSpan.FromSeconds(30)), "scan running");
            UiHost.Invoke(w.Close);
            Assert.True(UiHost.Invoke(() => w.IsVisible), "window stays open");
            Assert.False(UiHost.Invoke(() => vm.Progress.IsStopping), "scan not cancelled");
            WaitForResults(vm, seconds: 300);
            Assert.Equal(ScanCompletionState.Complete, UiHost.Invoke(() => vm.Results!.State));
        }
        finally { UiSetupTests.Close(w); }
    }
}

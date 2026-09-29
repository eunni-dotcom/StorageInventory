using System.IO;
using System.Windows;
using StorageInventory.App;
using StorageInventory.App.ViewModels;
using StorageInventory.Core.Paths;
using StorageInventory.Testing;

namespace StorageInventory.IntegrationTests;

/// <summary>The real MainWindow and view model, driven in-process. Screenshots go to the test work folder, or to the
/// folder given by SI_SCREENSHOTS.</summary>
public static class UiSetupTests
{
    private static PhaseAFixture Fx => PhaseAFixture.Shared;

    private static string ScreenshotFolder =>
        Environment.GetEnvironmentVariable("SI_SCREENSHOTS") is { Length: > 0 } dir ? dir : Path.Combine(TestEnvironment.WorkRoot, "screenshots");

    internal static (MainWindow Window, MainViewModel Vm) OpenWindow()
    {
        return UiHost.Invoke(() =>
        {
            var w = new MainWindow { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0 };
            w.Show();
            return (w, (MainViewModel)w.DataContext);
        });
    }

    internal static void Close(Window w) => UiHost.Invoke(w.Close);

    private static void RenderDark(string source, string output, PreflightState expected, string file)
    {
        UiHost.SetTheme(ThemeMode.Dark);
        var (w, vm) = OpenWindow();
        try
        {
            UiHost.Invoke(() => { vm.SourcePath = source; vm.OutputPath = output; });
            Assert.True(WaitForPreflight(vm, expected), "dark window reaches the same state");
            UiHost.Render(w, Path.Combine(ScreenshotFolder, file));
        }
        finally
        {
            Close(w);
            UiHost.SetTheme(ThemeMode.System);
        }
    }

    private static bool WaitForPreflight(MainViewModel vm, params PreflightState[] states) =>
        UiHost.WaitUntil(() => states.Contains(vm.Preflight.State), TimeSpan.FromSeconds(10));

    [Test]
    public static void Initially_nothing_is_chosen_and_scan_is_unavailable()
    {
        var (w, vm) = OpenWindow();
        try
        {
            Assert.True(WaitForPreflight(vm, PreflightState.Waiting), "waiting state");
            Assert.False(UiHost.Invoke(() => vm.StartScanCommand.CanExecute(null)), "Start scan disabled");
            Assert.Equal("NOT READY", UiHost.Invoke(() => vm.Preflight.StatusLabel));
            Assert.True(UiHost.Invoke(() => vm.OutputPath.EndsWith("StorageInventory", StringComparison.OrdinalIgnoreCase)), "sensible default report folder");
            UiHost.Render(w, Path.Combine(ScreenshotFolder, "setup-initial-light.png"));
        }
        finally { Close(w); }
    }

    [Test]
    public static void A_valid_pair_is_ready_and_scan_becomes_available()
    {
        var (w, vm) = OpenWindow();
        try
        {
            UiHost.Invoke(() => { vm.SourcePath = Path.Combine(Fx.Root, "Kpop"); vm.OutputPath = Path.Combine(Fx.Base, "ui-out"); });
            Assert.True(WaitForPreflight(vm, PreflightState.Ready), $"ready, was {UiHost.Invoke(() => vm.Preflight.State)}");
            Assert.True(UiHost.Invoke(() => vm.StartScanCommand.CanExecute(null)), "Start scan enabled");
            Assert.True(UiHost.Invoke(() => vm.Preflight.Issues.Any(i => i.Severity == IssueSeverity.Info)), "the 'will be created' note is shown");
            Assert.False(Directory.Exists(Path.Combine(Fx.Base, "ui-out")), "choosing a folder never creates it");
            UiHost.Render(w, Path.Combine(ScreenshotFolder, "setup-ready-light.png"));
            RenderDark(vm.SourcePath, vm.OutputPath, PreflightState.Ready, "setup-ready-dark.png");
        }
        finally
        {
            UiHost.SetTheme(ThemeMode.System);
            Close(w);
        }
    }

    [Test]
    public static void Output_inside_the_source_is_blocked_with_a_plain_reason()
    {
        var (w, vm) = OpenWindow();
        try
        {
            UiHost.Invoke(() => { vm.SourcePath = Fx.Root; vm.OutputPath = Path.Combine(Fx.Root, "Reports"); });
            Assert.True(WaitForPreflight(vm, PreflightState.Blocked), "blocked");
            Assert.False(UiHost.Invoke(() => vm.StartScanCommand.CanExecute(null)), "a blocked condition must prevent the scan");
            var issue = UiHost.Invoke(() => vm.Preflight.Issues.First(i => i.Severity == IssueSeverity.Blocked));
            Assert.Equal("BLOCKED", issue.Label, "severity is spelled out, not colour-only");
            Assert.Contains("outside the folder being scanned", issue.Message);
            UiHost.Render(w, Path.Combine(ScreenshotFolder, "setup-blocked-light.png"));
            RenderDark(vm.SourcePath, vm.OutputPath, PreflightState.Blocked, "setup-blocked-dark.png");
        }
        finally
        {
            UiHost.SetTheme(ThemeMode.System);
            Close(w);
        }
    }

    [Test]
    public static void Junction_source_is_blocked_and_the_UI_does_not_decide_safety_itself()
    {
        var (w, vm) = OpenWindow();
        try
        {
            var loop = Path.Combine(Fx.Root, "Loop");
            UiHost.Invoke(() => { vm.SourcePath = loop; vm.OutputPath = Path.Combine(Fx.Base, "ui-out2"); });
            Assert.True(WaitForPreflight(vm, PreflightState.Blocked), "blocked");
            var codes = PathPolicy.Validate(loop, Path.Combine(Fx.Base, "ui-out2")).Issues.Select(i => i.Message).ToList();
            var shown = UiHost.Invoke(() => vm.Preflight.Issues.Select(i => i.Message).ToList());
            Assert.SequenceEqual(codes.Order(), shown.Order(), "the pre-flight shows exactly Core's findings");
        }
        finally { Close(w); }
    }
}

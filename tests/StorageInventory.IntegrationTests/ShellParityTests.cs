using System.Text;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using StorageInventory.App;
using StorageInventory.App.Services;
using StorageInventory.App.ViewModels;
using StorageInventory.Core;
using StorageInventory.Testing;

namespace StorageInventory.IntegrationTests;

/// <summary>
/// C2 (v1.1) against v1 itself. TEST-U4: the shell's screens differ from v1's only in the shell chrome, by an image diff
/// masked to the content region, light and dark, on the synthetic tree. The reference is v1's own MainWindow.xaml from
/// the frozen base (byte for byte, checked by its git object id), shown over the same view model at the same moment,
/// so a difference can only come from the decomposition. The same pairing checks the accessibility tree, the Tab
/// order and the binding diagnostics.
/// </summary>
public static class ShellParityTests
{
    private const int NavigationWidth = 200;

    private static string ScreenshotFolder =>
        Path.Combine(Environment.GetEnvironmentVariable("SI_SCREENSHOTS") is { Length: > 0 } dir ? dir : Path.Combine(PhaseAFixture.Shared.Base, "screenshots"), "test-u4");

    private static void Wait(Func<bool> condition, string what, int seconds = 30) =>
        Assert.True(UiHost.WaitUntil(condition, TimeSpan.FromSeconds(seconds)), what);

    // ---------------------------------------------------------------- the reference

    [Test]
    public static void The_v1_reference_is_v1s_MainWindow_xaml_byte_for_byte()
    {
        Assert.Equal(V1Reference.BlobId, V1Reference.GitBlobId(V1Reference.Bytes()), $"git object id of {V1Reference.FilePath} (bf3fa36:src/StorageInventory.App/MainWindow.xaml)");
        var w = UiHost.Invoke(() => V1Reference.Load());
        UiHost.Invoke(() =>
        {
            Assert.Equal(880.0, w.Width);
            Assert.True(w.Content is ScrollViewer, "v1's window content is its page scroller");
            Assert.True(w.FindName("SourceBox") is TextBox, "v1's named elements resolve");
        });
    }

    [Test]
    public static void The_stage_views_carry_v1s_markup_unchanged_apart_from_indentation()
    {
        var v1 = File.ReadAllText(V1Reference.FilePath).Split('\n');
        var views = Path.Combine(TestEnvironment.RepoRoot, "src", "StorageInventory.App", "Views");
        // (view, first and last v1 line of the stage's content): the stage panels of v1 MainWindow.xaml.
        foreach (var (view, first, last) in new[] { ("SetupView", 26, 166), ("ScanProgressView", 170, 213), ("ResultsView", 218, 384) })
        {
            var lines = File.ReadAllText(Path.Combine(views, view + ".xaml")).Split('\n');
            var open = Array.FindIndex(lines, l => l.StartsWith("    <StackPanel", StringComparison.Ordinal));
            var close = Array.FindLastIndex(lines, l => l == "    </StackPanel>");
            var body = lines[(open + 1)..close].Select(l => l.Trim());
            Assert.SequenceEqual(v1[(first - 1)..last].Select(l => l.Trim()), body, view);
        }
        // v1's stage panels kept their own attributes, apart from Grid.Row and Visibility, which the Scan page sets.
        Assert.Contains("<StackPanel DataContext=\"{Binding}\">", File.ReadAllText(Path.Combine(views, "ScanProgressView.xaml")));
        var page = File.ReadAllText(Path.Combine(views, "ScanPage.xaml"));
        foreach (var stage in new[] { "SetupView Grid.Row=\"1\" Visibility=\"{Binding IsSetup", "ScanProgressView Grid.Row=\"1\" Visibility=\"{Binding IsScanning", "ResultsView Grid.Row=\"1\" Visibility=\"{Binding IsResults" })
        {
            Assert.Contains(stage, page);
        }
    }

    [Test]
    public static void The_shell_window_is_v1s_window_plus_the_navigation_column()
    {
        var w = ShellHost.Open(new ScanSession(new GatedScanner()));
        try
        {
            UiHost.Invoke(() =>
            {
                var v1 = V1Reference.Load();
                var column = ShellHost.Root(w).ColumnDefinitions[0].Width;
                Assert.True(column.IsAbsolute && column.Value == NavigationWidth, "a fixed navigation column");
                Assert.Equal(v1.Width + NavigationWidth, w.Width, "default width");
                Assert.Equal(v1.MinWidth + NavigationWidth, w.MinWidth, "minimum width");
                Assert.Equal(v1.Height, w.Height, "height");
                Assert.Equal(v1.MinHeight, w.MinHeight, "minimum height");
                Assert.Equal(v1.Title, w.Title, "title");
                Assert.Equal(System.Windows.Automation.AutomationProperties.GetName(v1), System.Windows.Automation.AutomationProperties.GetName(w), "window automation name");
                Assert.Equal(WindowStartupLocation.CenterScreen, v1.WindowStartupLocation, "v1 starts centred");
                var unshown = new MainWindow(new ScanSession());
                Assert.Equal(v1.WindowStartupLocation, unshown.WindowStartupLocation, "so does the shell");
                unshown.Close();
            });
        }
        finally { UiHost.Invoke(w.Close); }
    }

    // ---------------------------------------------------------------- driving both windows

    /// <summary>The frames TEST-U4 compares: the three stages, with the states each stage can show.</summary>
    public static readonly string[] Frames =
        ["setup-initial", "setup-blocked", "setup-ready", "scanning", "scanning-writing", "scanning-stopping", "results-cancelled", "results-complete"];

    /// <summary>Drives one Scan page view model through every frame, on the synthetic tree, calling back at each.</summary>
    public static void Drive(MainViewModel vm, GatedScanner scanner, Action<string> atFrame)
    {
        var tree = SyntheticTree.Root;
        Wait(() => vm.Preflight.State == PreflightState.Waiting, "initial pre-flight");
        atFrame("setup-initial");

        UiHost.Invoke(() => { vm.SourcePath = tree; vm.OutputPath = Path.Combine(tree, "reports"); vm.CreateWorkbook = false; });
        Wait(() => vm.Preflight.State == PreflightState.Blocked, "reports inside the source are blocked");
        atFrame("setup-blocked");

        UiHost.Invoke(() => vm.OutputPath = SyntheticTree.NewReportFolder());
        Wait(() => vm.Preflight.State == PreflightState.Ready && vm.StartScanCommand.CanExecute(null), "ready");
        atFrame("setup-ready");

        scanner.Close();
        scanner.Script = new StorageScanProgress(ScanPhase.Enumerating, 931, 14, 345_678_901, 0, 2, 0, @"Photos\2025", TimeSpan.FromSeconds(1));
        UiHost.Invoke(() => vm.StartScanCommand.Execute(null));
        Wait(() => vm.Stage == AppStage.Scanning && vm.Progress.Files == Format.Count(931), "scanning, enumerating");
        atFrame("scanning");

        UiHost.Invoke(() => scanner.LastProgress!.Report(new StorageScanProgress(ScanPhase.WritingFilesReport, 931, 14, 345_678_901, 0, 2, 0, "", TimeSpan.FromSeconds(2), 400, 931)));
        Wait(() => !vm.Progress.IsIndeterminate, "scanning, writing the Files report");
        atFrame("scanning-writing");

        UiHost.Invoke(() => vm.CancelScanCommand.Execute(null));
        Wait(() => vm.Progress.IsStopping, "stopping");
        atFrame("scanning-stopping");

        scanner.Script = null;
        scanner.Open();
        Wait(() => vm.Stage == AppStage.Results, "cancelled results", 60);
        Assert.Equal(ScanCompletionState.Cancelled, UiHost.Invoke(() => vm.Results!.State));
        atFrame("results-cancelled");

        UiHost.Invoke(() => vm.NewScanCommand.Execute(null));
        UiHost.Invoke(() => vm.OutputPath = SyntheticTree.NewReportFolder());
        Wait(() => vm.Stage == AppStage.Setup && vm.Preflight.State == PreflightState.Ready && vm.StartScanCommand.CanExecute(null), "ready again");
        UiHost.Invoke(() => vm.StartScanCommand.Execute(null));
        Wait(() => vm.Stage == AppStage.Results && !vm.Results!.Exploration.IsLoading, "complete results", 120);
        Assert.Equal(ScanCompletionState.Complete, UiHost.Invoke(() => vm.Results!.State));
        atFrame("results-complete");
    }

    /// <summary>
    /// The shell and v1's window, side by side over ONE view model, created together before the first scan (so even
    /// an animated progress bar starts in both at the same moment), driven through every frame. v1's window gets v1's
    /// size; the shell gets that plus the navigation column. On a screen too small for that, both shrink by the same
    /// amount (Windows limits a window to the screen), which keeps the content regions equal.
    /// </summary>
    public static void DrivePair(ThemeMode theme, Action<string, MainWindow, Window> atFrame, string? replace = null, string? with = null)
    {
        UiHost.SetTheme(theme);
        var scanner = new GatedScanner();
        MainWindow? c2 = null;
        Window? v1 = null;
        try
        {
            var (width, height) = UiHost.Invoke(() =>
                (Math.Min(880, Math.Floor(SystemParameters.MaximumWindowTrackWidth) - NavigationWidth), Math.Min(820, Math.Floor(SystemParameters.MaximumWindowTrackHeight))));
            c2 = UiHost.Invoke(() =>
            {
                var w = new MainWindow(new ScanSession(scanner)) { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0 };
                w.Width = width + NavigationWidth;
                w.Height = height;
                w.Show();
                return w;
            });
            var shell = c2;
            v1 = UiHost.Invoke(() => V1Reference.Open(shell.ViewModel, replace, with, width, height));
            var reference = v1;
            UiHost.Settle();
            Drive(UiHost.Invoke(() => shell.ViewModel), scanner, frame => atFrame(frame, shell, reference));
        }
        finally
        {
            scanner.Open();
            if (v1 is not null) UiHost.Invoke(v1.Close);
            if (c2 is not null)
            {
                var shell = c2;
                UiHost.Invoke(() => shell.ConfirmStopAndClose = () => true);
                UiHost.Invoke(shell.Close);
                UiHost.WaitUntil(() => !shell.Session.IsBusy && !shell.IsVisible, TimeSpan.FromSeconds(60));
            }
            UiHost.SetTheme(ThemeMode.System);
        }
    }

    // ---------------------------------------------------------------- TEST-U4

    private sealed record Shot(RenderedImage Shell, RenderedImage V1Window, RenderedImage ShellPage, RenderedImage V1Page, Int32Rect Content);

    private static Shot Take(MainWindow c2, Window v1)
    {
        UiHost.Settle();
        return UiHost.Invoke(() =>
        {
            // One dispatcher operation: both windows show the same state (the elapsed clock cannot tick in between).
            c2.UpdateLayout();
            v1.UpdateLayout();
            var root = ShellHost.Root(c2);
            var host = ShellHost.PageHost(c2);
            var at = host.TransformToAncestor(root).Transform(new Point(0, 0));
            var content = new Int32Rect((int)Math.Round(at.X), (int)Math.Round(at.Y), (int)Math.Round(host.ActualWidth), (int)Math.Round(host.ActualHeight));
            return new Shot(
                RenderedImage.Of(root, c2, withMargin: false),
                RenderedImage.Of((FrameworkElement)v1.Content, v1, withMargin: false),
                RenderedImage.Of(ShellHost.Page(ShellHost.ScanPage(c2)), c2, withMargin: true),
                RenderedImage.Of(V1Reference.Page(v1), v1, withMargin: true),
                content);
        });
    }

    /// <summary>Compares one frame; returns a problem, or null. Saves the images for the evidence.</summary>
    private static string? Compare(string name, Shot shot)
    {
        var problems = new List<string>();
        if (shot.Content.X != NavigationWidth || shot.Content.Y != 0) problems.Add($"content region at {shot.Content.X},{shot.Content.Y}, expected {NavigationWidth},0");
        if (shot.Content.Width != shot.V1Window.Width || shot.Content.Height != shot.V1Window.Height)
            problems.Add($"content region {shot.Content.Width}x{shot.Content.Height} but v1's window content is {shot.V1Window.Width}x{shot.V1Window.Height}");
        if (shot.Shell.Width != shot.Content.X + shot.Content.Width || shot.Shell.Height != shot.Content.Height)
            problems.Add($"shell {shot.Shell.Width}x{shot.Shell.Height} is not the navigation column plus the content region");

        (int Count, Int32Rect Box) window = problems.Count == 0 ? shot.Shell.Crop(shot.Content.X, shot.Content.Y, shot.Content.Width, shot.Content.Height).Differences(shot.V1Window) : (-1, Int32Rect.Empty);
        var page = shot.ShellPage.Differences(shot.V1Page);
        Console.WriteLine($"      TEST-U4 {name}: shell {shot.Shell.Width}x{shot.Shell.Height}, chrome {shot.Content.X} px, content {shot.Content.Width}x{shot.Content.Height}: " +
                          $"{(window.Count < 0 ? "not compared" : window.Count + " pixels differ")}; whole page {shot.V1Page.Width}x{shot.V1Page.Height}: {(page.Count == int.MaxValue ? "size differs" : page.Count + " pixels differ")}");

        shot.Shell.Save(Path.Combine(ScreenshotFolder, $"{name}-c2.png"));
        shot.V1Window.Save(Path.Combine(ScreenshotFolder, $"{name}-v1.png"));
        if (window.Count > 0)
        {
            problems.Add($"{window.Count} pixels differ inside the content region (box {window.Box.X},{window.Box.Y} {window.Box.Width}x{window.Box.Height})");
            shot.Shell.Crop(shot.Content.X, shot.Content.Y, shot.Content.Width, shot.Content.Height).DiffImage(shot.V1Window).Save(Path.Combine(ScreenshotFolder, $"{name}-diff.png"));
        }
        if (page.Count == int.MaxValue) problems.Add($"whole page {shot.ShellPage.Width}x{shot.ShellPage.Height} vs v1 {shot.V1Page.Width}x{shot.V1Page.Height}");
        else if (page.Count > 0)
        {
            problems.Add($"{page.Count} pixels of the whole page differ (box {page.Box.X},{page.Box.Y} {page.Box.Width}x{page.Box.Height})");
            shot.ShellPage.DiffImage(shot.V1Page).Save(Path.Combine(ScreenshotFolder, $"{name}-page-diff.png"));
        }
        return problems.Count == 0 ? null : $"{name}: {string.Join("; ", problems)}";
    }

    private static void ScreensDifferOnlyInTheChrome(ThemeMode theme, string label)
    {
        var problems = new List<string>();
        var compared = new List<string>();
        DrivePair(theme, (frame, c2, v1) =>
        {
            if (frame == Frames[0])
            {
                // The look of the content comes from the same window-level values in both windows.
                UiHost.Invoke(() =>
                {
                    var a = ShellHost.Page(ShellHost.ScanPage(c2));
                    var b = V1Reference.Page(v1);
                    Assert.Equal(TextElement.GetFontFamily(b), TextElement.GetFontFamily(a), "font family");
                    Assert.Equal(TextElement.GetFontSize(b), TextElement.GetFontSize(a), "font size");
                    Assert.Equal(TextElement.GetForeground(b)?.ToString(), TextElement.GetForeground(a)?.ToString(), "foreground");
                    Assert.True(ReferenceEquals(v1.Style, c2.Style), "both windows have the same window style");
                });
            }
            compared.Add(frame);
            if (Compare($"{label}-{frame}", Take(c2, v1)) is { } problem) problems.Add(problem);
        });
        Assert.SequenceEqual(Frames, compared, "frames compared");
        Assert.True(problems.Count == 0, string.Join(" | ", problems));
    }

    [Test]
    public static void TEST_U4_light_screens_differ_from_v1_only_in_the_shell_chrome() => ScreensDifferOnlyInTheChrome(ThemeMode.Light, "light");

    [Test]
    public static void TEST_U4_dark_screens_differ_from_v1_only_in_the_shell_chrome() => ScreensDifferOnlyInTheChrome(ThemeMode.Dark, "dark");

    [Test]
    public static void TEST_U4_the_masked_comparison_notices_a_one_word_change_in_the_page()
    {
        // Negative control: the same comparison, with one word of v1's Setup stage changed in the reference only.
        var counts = new List<int>();
        DrivePair(ThemeMode.Light, (frame, c2, v1) =>
        {
            if (frame != "setup-ready") return;
            var shot = Take(c2, v1);
            counts.Add(shot.Shell.Crop(shot.Content.X, shot.Content.Y, shot.Content.Width, shot.Content.Height).Differences(shot.V1Window).Count);
            counts.Add(shot.ShellPage.Differences(shot.V1Page).Count);
        }, replace: "Reports are always new files.", with: "Reports are always new file.");
        Console.WriteLine($"      negative control: {counts[0]} window pixels and {counts[1]} page pixels differ");
        Assert.True(counts.Count == 2 && counts[0] > 0 && counts[1] > 0, "a changed word in the page must be detected");
    }

    // ---------------------------------------------------------------- accessibility, keyboard, bindings

    [Test]
    public static void Every_frame_has_v1s_accessibility_tree_and_tab_order_inside_the_page()
    {
        var problems = new List<string>();
        var stops = new List<string>();
        DrivePair(ThemeMode.Light, (frame, c2, v1) =>
        {
            UiHost.Settle();
            UiHost.Invoke(() =>
            {
                c2.UpdateLayout();
                v1.UpdateLayout();
                var navigation = ShellHost.NavigationList(c2);

                // UI Automation's control view: the shell's is v1's plus the navigation list, and nothing else.
                var shellTree = Accessibility.ControlView(c2, p => p is UIElementAutomationPeer u && ReferenceEquals(u.Owner, navigation));
                var v1Tree = Accessibility.ControlView(v1, _ => false);
                if (!shellTree.SequenceEqual(v1Tree)) problems.Add($"{frame}: automation tree differs: {FirstDifference(v1Tree, shellTree)}");
                var navigationNode = Accessibility.ControlView(c2, _ => false).Except(shellTree).ToList();
                if (navigationNode.Count == 0 || !navigationNode.Any(l => l.Contains("List name='Pages'", StringComparison.Ordinal)) || !navigationNode.Any(l => l.Contains("ListItem name='Scan'", StringComparison.Ordinal)))
                    problems.Add($"{frame}: the navigation list is missing from the automation tree: {string.Join(" / ", navigationNode)}");

                // Tab order: the shell's is v1's, with the navigation list's stop added.
                var shellTabs = Accessibility.TabOrder(c2, e => Accessibility.IsInside(e, navigation));
                var v1Tabs = Accessibility.TabOrder(v1, _ => false);
                if (!shellTabs.SequenceEqual(v1Tabs)) problems.Add($"{frame}: tab order differs: {FirstDifference(v1Tabs, shellTabs)}");
                if (!Accessibility.TabOrder(c2, _ => false).Except(shellTabs).Any()) problems.Add($"{frame}: the navigation list is not a tab stop");
                stops.Add($"{frame}: {v1Tabs.Count} stops, {v1Tree.Count} automation elements");
            });
        });
        Console.WriteLine("      " + string.Join("; ", stops));
        Assert.True(problems.Count == 0, string.Join(" | ", problems));
    }

    private static string FirstDifference(IReadOnlyList<string> v1, IReadOnlyList<string> c2)
    {
        for (var i = 0; i < Math.Max(v1.Count, c2.Count); i++)
        {
            var a = i < v1.Count ? v1[i] : "<none>";
            var b = i < c2.Count ? c2[i] : "<none>";
            if (a != b) return $"#{i}: v1 <{a}> shell <{b}> (counts {v1.Count} vs {c2.Count})";
        }
        return "none";
    }

    [Test]
    public static void The_binding_diagnostics_are_live()
    {
        // Self-test: the recorder must see a deliberately broken binding, or "no binding errors" would prove nothing.
        var recorder = BindingErrorRecorder.Start();
        try
        {
            UiHost.Invoke(() =>
            {
                var probe = new TextBlock();
                probe.SetBinding(TextBlock.TextProperty, new Binding("NoSuchPropertyC2Probe"));
                probe.DataContext = new object();
                probe.Measure(new Size(100, 100));
            });
            UiHost.Settle();
            Assert.True(recorder.Messages.Any(m => m.Contains("NoSuchPropertyC2Probe", StringComparison.Ordinal)),
                "a broken binding is reported: " + string.Join(" / ", recorder.Messages));
        }
        finally { recorder.Stop(); }
    }

    [Test]
    public static void The_shell_adds_no_binding_error_to_v1s()
    {
        // v1's window alone, then the shell alone, each through every frame; the shell may show only v1's messages.
        var recorder = BindingErrorRecorder.Start();
        try
        {
            var v1Scanner = new GatedScanner();
            var v1Vm = UiHost.Invoke(() => new MainViewModel(new ScanSession(v1Scanner), new FolderPicker()));
            var v1 = UiHost.Invoke(() => V1Reference.Open(v1Vm));
            try { Drive(v1Vm, v1Scanner, _ => UiHost.Settle()); }
            finally
            {
                v1Scanner.Open();
                UiHost.Invoke(v1.Close);
                UiHost.WaitUntil(() => !v1Vm.IsBusy, TimeSpan.FromSeconds(60));
            }
            UiHost.Settle();
            var v1Messages = recorder.Normalised;
            recorder.Clear();

            var scanner = new GatedScanner();
            var c2 = ShellHost.Open(new ScanSession(scanner));
            try
            {
                Drive(UiHost.Invoke(() => c2.ViewModel), scanner, _ => UiHost.Settle());
                // ...and once more after the page view is replaced (re-hosting).
                UiHost.Invoke(() =>
                {
                    var host = ShellHost.PageHost(c2);
                    host.Content = null;
                    host.SetBinding(ContentControl.ContentProperty, new Binding("Current.ViewModel"));
                });
                UiHost.Settle();
            }
            finally
            {
                scanner.Open();
                UiHost.Invoke(() => c2.ConfirmStopAndClose = () => true);
                UiHost.Invoke(c2.Close);
                UiHost.WaitUntil(() => !c2.Session.IsBusy, TimeSpan.FromSeconds(60));
            }
            UiHost.Settle();
            var shellMessages = recorder.Normalised;
            var added = shellMessages.Except(v1Messages).ToList();
            Console.WriteLine($"      binding diagnostics: v1 window {v1Messages.Count} distinct, shell {shellMessages.Count} distinct, {added.Count} not in v1's");
            foreach (var m in v1Messages) Console.WriteLine("        v1:    " + m);
            foreach (var m in shellMessages) Console.WriteLine("        shell: " + m);
            Assert.True(added.Count == 0, "new binding errors: " + string.Join(" / ", added));
        }
        finally { recorder.Stop(); }
    }
}

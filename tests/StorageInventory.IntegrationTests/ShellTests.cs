using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using StorageInventory.App;
using StorageInventory.App.Services;
using StorageInventory.App.ViewModels;
using StorageInventory.App.Views;
using StorageInventory.Core;
using StorageInventory.Testing;

namespace StorageInventory.IntegrationTests;

/// <summary>
/// C2 (v1.1): the shell, its one Scan page, the three stage views, and the app-lifetime <see cref="ScanSession"/>
/// (UI-12, §14.6). v1's own UI tests (UiSetupTests, UiScanTests) are unchanged; these add what C2 introduces. Scans
/// that must stay running use <see cref="GatedScanner"/>, so every test here also runs without the large trees.
/// </summary>
public static class ShellTests
{
    private static string NewOutput() => SyntheticTree.NewReportFolder();

    private static void Wait(Func<bool> condition, string what, int seconds = 30) =>
        Assert.True(UiHost.WaitUntil(condition, TimeSpan.FromSeconds(seconds)), what);

    private static void StartGatedScan(MainViewModel vm, GatedScanner scanner, string output)
    {
        UiHost.Invoke(() => { vm.SourcePath = SyntheticTree.Root; vm.OutputPath = output; vm.CreateWorkbook = false; });
        Wait(() => vm.StartScanCommand.CanExecute(null), "pre-flight ready");
        scanner.Close();
        UiHost.Invoke(() => vm.StartScanCommand.Execute(null));
        Wait(() => vm.Stage == AppStage.Scanning && scanner.Calls > 0, "scan running");
    }

    private static void CloseShell(MainWindow w, GatedScanner scanner)
    {
        scanner.Open();
        if (UiHost.Invoke(() => PresentationSource.FromVisual(w) is null)) return;   // already closed
        UiHost.Invoke(() => w.ConfirmStopAndClose = () => true);
        UiHost.Invoke(w.Close);
        Wait(() => !w.Session.IsBusy && !w.IsVisible, "shell closed", 60);
    }

    [Test]
    public static void The_shell_hosts_one_page_Scan_and_no_other()
    {
        var scanner = new GatedScanner();
        var w = ShellHost.Open(new ScanSession(scanner));
        try
        {
            UiHost.Settle();
            UiHost.Invoke(() =>
            {
                Assert.Equal(1, w.Shell.Pages.Count, "pages");
                var scan = w.Shell.Pages[0];
                Assert.Equal("Scan", scan.Title);
                Assert.True(ReferenceEquals(w.ViewModel, scan.ViewModel), "the Scan page's view model is the window's v1 DataContext");
                Assert.True(ReferenceEquals(scan, w.Shell.Current), "Scan is shown");

                var list = ShellHost.NavigationList(w);
                Assert.Equal("Pages", AutomationProperties.GetName(list));
                Assert.Equal(1, list.Items.Count, "navigation entries");
                Assert.True(ReferenceEquals(scan, list.SelectedItem), "Scan is selected in the list");
                Assert.True(list.IsEnabled && list.IsVisible, "the list is usable");
                var item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
                Assert.True(item.Focusable && KeyboardNavigationIsTabStop(item), "the entry is keyboard-accessible");
                var entry = UIElementAutomationPeer.CreatePeerForElement(list).GetChildren().Single();
                Assert.Equal(AutomationControlType.ListItem, entry.GetAutomationControlType());
                Assert.Equal("Scan", entry.GetName(), "the entry's automation name");
                Assert.False(scan.Title.Contains('_', StringComparison.Ordinal), "no access key: v1's access keys stay as they were");

                var page = ShellHost.ScanPage(w);
                Assert.True(ReferenceEquals(w.ViewModel, page.DataContext), "the Scan page shows the scan's view model");

                // Clearing the list's selection never empties the shell.
                w.Shell.Current = null;
                Assert.True(ReferenceEquals(scan, w.Shell.Current), "a page is always shown");
                Assert.True(ReferenceEquals(page, ShellHost.ScanPage(w)), "the page is untouched");
            });

            // No page beyond Scan exists in the App: its windows and views are exactly the shell, the page and the stages.
            var views = typeof(StorageInventory.App.App).Assembly.GetTypes()
                .Where(t => typeof(Window).IsAssignableFrom(t) || typeof(UserControl).IsAssignableFrom(t) || typeof(Page).IsAssignableFrom(t))
                .Select(t => t.Name).Order(StringComparer.Ordinal).ToList();
            Assert.SequenceEqual(["MainWindow", "ResultsView", "ScanPage", "ScanProgressView", "SetupView"], views, "App views");
            Assert.Null(UiHost.Invoke(() => Application.Current.StartupUri), "no StartupUri: App.OnStartup creates the session, then the shell");
        }
        finally { CloseShell(w, scanner); }
    }

    private static bool KeyboardNavigationIsTabStop(DependencyObject d) => System.Windows.Input.KeyboardNavigation.GetIsTabStop(d);

    [Test]
    public static void One_session_serves_the_window_the_page_and_every_stage_without_recreating_views()
    {
        var scanner = new GatedScanner();
        var session = new ScanSession(scanner);
        var w = ShellHost.Open(session);
        try
        {
            var vm = UiHost.Invoke(() => w.ViewModel);
            UiHost.Settle();
            var (page, setup, progress, results) = UiHost.Invoke(() =>
            {
                var p = ShellHost.ScanPage(w);
                return (p, ShellHost.Single<SetupView>(p), ShellHost.Single<ScanProgressView>(p), ShellHost.Single<ResultsView>(p));
            });
            UiHost.Invoke(() =>
            {
                Assert.True(ReferenceEquals(session, w.Session) && ReferenceEquals(session, vm.Session), "one session");
                foreach (var view in new FrameworkElement[] { page, setup, progress, results })
                {
                    Assert.True(ReferenceEquals(vm, view.DataContext), view.GetType().Name + " is bound to the Scan page's view model");
                }
                Assert.True(setup.IsVisible && !progress.IsVisible && !results.IsVisible, "Setup shown first");
            });

            StartGatedScan(vm, scanner, NewOutput());
            UiHost.Settle();
            UiHost.Invoke(() =>
            {
                Assert.True(!setup.IsVisible && progress.IsVisible && !results.IsVisible, "Scanning shown");
                var progressCard = ShellHost.All<Border>(progress).First(b => b.DataContext is ScanProgressViewModel);
                Assert.True(ReferenceEquals(session.Progress, progressCard.DataContext), "the progress card shows the session's progress");
            });
            scanner.Open();
            Wait(() => vm.Stage == AppStage.Results && !vm.Results!.Exploration.IsLoading, "results", 60);
            UiHost.Settle();
            UiHost.Invoke(() =>
            {
                Assert.True(!setup.IsVisible && !progress.IsVisible && results.IsVisible, "Results shown");
                var card = ShellHost.All<Border>(results).First(b => b.DataContext is ResultsViewModel);
                Assert.True(ReferenceEquals(session.Results, card.DataContext), "the results card shows the session's result");
                Assert.Equal(ScanCompletionState.Complete, session.Results!.State);
            });

            UiHost.Invoke(() => vm.NewScanCommand.Execute(null));
            UiHost.Settle();
            UiHost.Invoke(() =>
            {
                Assert.True(setup.IsVisible && !progress.IsVisible && !results.IsVisible, "Setup again");
                Assert.True(ReferenceEquals(page, ShellHost.ScanPage(w)), "stage changes never recreate the page");
                Assert.True(ReferenceEquals(setup, ShellHost.Single<SetupView>(page)) && ReferenceEquals(progress, ShellHost.Single<ScanProgressView>(page))
                    && ReferenceEquals(results, ShellHost.Single<ResultsView>(page)), "...or its stage views");
                Assert.True(ReferenceEquals(session, w.Session) && ReferenceEquals(session, vm.Session), "...or the session");
            });
            Assert.Equal(1, scanner.Calls, "one scan, one call to Core");
        }
        finally { CloseShell(w, scanner); }
    }

    [Test]
    public static void The_extracted_views_carry_the_page_commands()
    {
        var scanner = new GatedScanner();
        var w = ShellHost.Open(new ScanSession(scanner));
        try
        {
            var vm = UiHost.Invoke(() => w.ViewModel);
            UiHost.Settle();
            UiHost.Invoke(() =>
            {
                var page = ShellHost.ScanPage(w);
                Assert.True(ReferenceEquals(vm.BrowseSourceCommand, ShellHost.Button(page, "Browse for the folder or drive to scan").Command), "Browse (source)");
                Assert.True(ReferenceEquals(vm.BrowseOutputCommand, ShellHost.Button(page, "Browse for the folder to save the reports in").Command), "Browse (reports)");
                Assert.True(ReferenceEquals(vm.StartScanCommand, ShellHost.Button(page, "Start scan").Command), "Start scan");
                Assert.True(ReferenceEquals(vm.CancelScanCommand, ShellHost.Button(page, "Cancel scan").Command), "Cancel scan");
                Assert.True(ReferenceEquals(vm.NewScanCommand, ShellHost.Button(page, "New scan").Command), "New scan");
                Assert.True(ShellHost.Button(page, "Start scan").IsDefault, "Start scan is still the default button");
            });

            // Start and Cancel through the buttons themselves reach the session.
            UiHost.Invoke(() => { vm.SourcePath = SyntheticTree.Root; vm.OutputPath = NewOutput(); vm.CreateWorkbook = false; });
            Wait(() => ShellHost.Button(ShellHost.ScanPage(w), "Start scan").IsEnabled, "Start scan enabled");
            scanner.Close();
            UiHost.Invoke(() => { var b = ShellHost.Button(ShellHost.ScanPage(w), "Start scan"); b.Command.Execute(b.CommandParameter); });
            Wait(() => w.Session.IsBusy && w.Session.Stage == AppStage.Scanning, "the button started the session's scan");
            UiHost.Settle();
            Assert.True(UiHost.Invoke(() => ShellHost.Button(ShellHost.ScanPage(w), "Cancel scan").IsEnabled), "Cancel scan enabled while scanning");
            UiHost.Invoke(() => { var b = ShellHost.Button(ShellHost.ScanPage(w), "Cancel scan"); b.Command.Execute(b.CommandParameter); });
            Assert.True(UiHost.Invoke(() => w.Session.Progress.IsStopping && scanner.LastToken.IsCancellationRequested), "the button cancelled the session's scan");
            scanner.Open();
            Wait(() => vm.Stage == AppStage.Results, "cancelled results", 60);
            UiHost.Settle();
            UiHost.Invoke(() =>
            {
                var page = ShellHost.ScanPage(w);
                Assert.True(ShellHost.Button(page, "New scan").IsEnabled, "New scan enabled in Results");
                var b = ShellHost.Button(page, "New scan");
                b.Command.Execute(b.CommandParameter);
                Assert.Equal(AppStage.Setup, w.Session.Stage);
            });
        }
        finally { CloseShell(w, scanner); }
    }

    [Test]
    public static void The_result_actions_in_ResultsView_are_the_results_commands()
    {
        var scanner = new GatedScanner();
        var w = ShellHost.Open(new ScanSession(scanner));
        try
        {
            var vm = UiHost.Invoke(() => w.ViewModel);
            UiHost.Invoke(() => { vm.SourcePath = SyntheticTree.Root; vm.OutputPath = NewOutput(); vm.CreateWorkbook = false; });
            Wait(() => vm.StartScanCommand.CanExecute(null), "pre-flight ready");
            UiHost.Invoke(() => vm.StartScanCommand.Execute(null));
            Wait(() => vm.Stage == AppStage.Results && !vm.Results!.Exploration.IsLoading, "results", 60);
            UiHost.Settle();
            UiHost.Invoke(() =>
            {
                var page = ShellHost.ScanPage(w);
                var r = vm.Results!;
                Assert.True(ReferenceEquals(r.OpenFilesReportCommand, ShellHost.Button(page, "Open Files report").Command), "Open Files report");
                Assert.True(ReferenceEquals(r.OpenFoldersReportCommand, ShellHost.Button(page, "Open Folders report").Command), "Open Folders report");
                Assert.True(ReferenceEquals(r.OpenErrorsReportCommand, ShellHost.Button(page, "Open Errors report").Command), "Open Errors report");
                Assert.True(ReferenceEquals(r.OpenReportFolderCommand, ShellHost.Button(page, "Open report folder").Command), "Open report folder");
                Assert.True(ReferenceEquals(r.OpenWorkbookCommand, ShellHost.Button(page, "Open Excel workbook").Command), "Open Excel workbook");
                var tabs = ShellHost.Logical<TabControl>(page).Single();
                Assert.True(ReferenceEquals(r, tabs.DataContext) && tabs.IsVisible, "the results tabs show the session's result");
                Assert.Equal(5, tabs.Items.Count, "results tabs");
            });
        }
        finally { CloseShell(w, scanner); }
    }

    [Test]
    public static void Replacing_the_Scan_page_view_during_a_scan_neither_stops_nor_restarts_it()
    {
        var scanner = new GatedScanner();
        var session = new ScanSession(scanner);
        var w = ShellHost.Open(session);
        try
        {
            var vm = UiHost.Invoke(() => w.ViewModel);
            var focusWasInOldPage = false;
            StartGatedScan(vm, scanner, NewOutput());
            var oldPage = RemovePage(w);   // the page's views are gone; nothing holds them but this weak reference

            UiHost.Settle();
            UiHost.Invoke(() =>
            {
                Assert.True(session.IsBusy, "the scan keeps running without any view");
                Assert.False(scanner.LastToken.IsCancellationRequested, "nothing cancelled it");
                Assert.False(session.Progress.IsStopping, "nothing asked it to stop");
                Assert.Equal(AppStage.Scanning, session.Stage);
            });

            // Re-host: the page host shows the page's view model again, through a new view.
            UiHost.Invoke(() => ShellHost.PageHost(w).SetBinding(ContentControl.ContentProperty, new Binding("Current.ViewModel")));
            UiHost.Settle();
            UiHost.Invoke(() =>
            {
                var page = ShellHost.ScanPage(w);
                Assert.False(ReferenceEquals(page, oldPage.Target), "a new view");
                Assert.True(ShellHost.Single<ScanProgressView>(page).IsVisible, "the new view shows the running scan");
                var card = ShellHost.All<Border>(page).First(b => b.DataContext is ScanProgressViewModel);
                Assert.True(ReferenceEquals(session.Progress, card.DataContext), "bound to the same progress");
                // The old page's source box had the window's logical focus; a window keeps that reference until focus
                // moves, so move it to the page now shown (as the shell's initial focus does for its page).
                focusWasInOldPage = FocusManager.GetFocusedElement(w) is DependencyObject focused && oldPage.Target is DependencyObject old && Accessibility.IsInside(focused, old);
                FocusManager.SetFocusedElement(w, (IInputElement)ShellHost.Single<SetupView>(page).FindName("SourceBox"));
            });
            Assert.Equal(1, scanner.Calls, "no second scan was started");

            scanner.Open();
            Wait(() => vm.Stage == AppStage.Results, "the same scan finishes", 60);
            UiHost.Settle();
            UiHost.Invoke(() =>
            {
                Assert.Equal(ScanCompletionState.Complete, session.Results!.State);
                Assert.True(ShellHost.Single<ResultsView>(ShellHost.ScanPage(w)).IsVisible, "the new view shows the result");
            });
            Assert.Equal(1, scanner.Calls, "still one scan");
            ShellHost.CollectGarbage();
            var aliveWhileOpen = oldPage.IsAlive;
            CloseShell(w, scanner);
            ShellHost.CollectGarbage();
            var aliveAfterClose = oldPage.IsAlive;
            Console.WriteLine($"      replaced view alive: {aliveWhileOpen} with the shell open, {aliveAfterClose} after it closed (the session still referenced: {session.Stage}); the window's logical focus was in it: {focusWasInOldPage}");
            Assert.False(aliveAfterClose, "the app-lifetime session does not keep the replaced view alive");
            Assert.False(aliveWhileOpen, "nothing in the open shell keeps the replaced view alive");
        }
        finally { CloseShell(w, scanner); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RemovePage(MainWindow w) => UiHost.Invoke(() =>
    {
        var page = ShellHost.ScanPage(w);
        ShellHost.PageHost(w).Content = null;
        return new WeakReference(page);
    });

    [Test]
    public static void Closing_another_window_hosting_the_Scan_page_does_not_stop_the_scan()
    {
        var scanner = new GatedScanner();
        var session = new ScanSession(scanner);
        var w = ShellHost.Open(session);
        Window? other = null;
        try
        {
            var vm = UiHost.Invoke(() => w.ViewModel);
            StartGatedScan(vm, scanner, NewOutput());
            other = UiHost.Invoke(() =>
            {
                var o = new Window { Content = new ScanPage { DataContext = vm }, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0, Width = 880, Height = 820 };
                o.Show();
                return o;
            });
            UiHost.Settle();
            Assert.True(UiHost.Invoke(() => ShellHost.Single<ScanProgressView>(other).IsVisible), "the second host shows the running scan");
            UiHost.Invoke(other.Close);
            UiHost.Settle();
            UiHost.Invoke(() =>
            {
                Assert.True(session.IsBusy && !scanner.LastToken.IsCancellationRequested && !session.Progress.IsStopping, "closing a view's window leaves the scan running");
                Assert.True(w.IsVisible, "the shell is unaffected");
            });
            scanner.Open();
            Wait(() => vm.Stage == AppStage.Results, "the scan finishes", 60);
            Assert.Equal(ScanCompletionState.Complete, UiHost.Invoke(() => session.Results!.State));
            Assert.Equal(1, scanner.Calls, "one scan");
        }
        finally
        {
            if (other is not null) UiHost.Invoke(other.Close);
            CloseShell(w, scanner);
        }
    }

    [Test]
    public static void The_session_runs_one_scan_at_a_time()
    {
        var scanner = new GatedScanner();
        var session = new ScanSession(scanner);
        var w = ShellHost.Open(session);
        try
        {
            var vm = UiHost.Invoke(() => w.ViewModel);
            StartGatedScan(vm, scanner, NewOutput());
            UiHost.Invoke(() =>
            {
                Assert.False(vm.StartScanCommand.CanExecute(null), "Start scan unavailable while scanning");
                Assert.False(session.Start(new StorageScanOptions { RootPath = SyntheticTree.Root, OutputPath = NewOutput() }, false), "a second start is refused");
                session.Clear();
                Assert.Equal(AppStage.Scanning, session.Stage, "a running scan is never put away");
            });
            Assert.Equal(1, scanner.Calls, "no second scan reached Core");
            scanner.Open();
            Wait(() => vm.Stage == AppStage.Results, "the first scan finishes", 60);
            Assert.Equal(1, scanner.Calls, "still one");
        }
        finally { CloseShell(w, scanner); }
    }

    [Test]
    public static void Closing_the_shell_during_a_scan_stops_it_through_the_session_and_waits()
    {
        // v1's UiScanTests cover this on a 60,000-file tree when one is present; the gate makes it run everywhere.
        var scanner = new GatedScanner();
        var session = new ScanSession(scanner);
        var w = ShellHost.Open(session);
        var output = NewOutput();
        var closed = false;
        var asked = 0;
        UiHost.Invoke(() =>
        {
            w.ConfirmStopAndClose = () => { asked++; return true; };
            w.Closed += (_, _) => closed = true;
        });
        StartGatedScan(UiHost.Invoke(() => w.ViewModel), scanner, output);

        UiHost.Invoke(w.Close);
        UiHost.Settle();
        Assert.Equal(1, asked, "asked once");
        Assert.True(UiHost.Invoke(() => w.IsVisible && !closed), "the window waits for the scan to stop");
        Assert.True(UiHost.Invoke(() => session.Progress.IsStopping && scanner.LastToken.IsCancellationRequested), "the session's scan was cancelled");
        UiHost.Invoke(w.Close);   // a second close while waiting asks nothing more
        Assert.Equal(1, asked, "still asked once");

        scanner.Open();
        Assert.True(UiHost.WaitUntil(() => closed, TimeSpan.FromSeconds(60)), "the window closes once the scan has stopped");
        Assert.False(UiHost.Invoke(() => session.IsBusy), "no scan left running");
        Assert.Equal(ScanCompletionState.Cancelled, UiHost.Invoke(() => session.Results!.State));
        if (Directory.Exists(output))
        {
            foreach (var f in Directory.GetFiles(output))
            {
                using var exclusive = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.None);   // throws if still open
            }
        }
    }

    [Test]
    public static void Declining_to_close_the_shell_keeps_the_session_scanning()
    {
        var scanner = new GatedScanner();
        var session = new ScanSession(scanner);
        var w = ShellHost.Open(session);
        try
        {
            UiHost.Invoke(() => w.ConfirmStopAndClose = () => false);
            var vm = UiHost.Invoke(() => w.ViewModel);
            StartGatedScan(vm, scanner, NewOutput());
            UiHost.Invoke(w.Close);
            UiHost.Settle();
            Assert.True(UiHost.Invoke(() => w.IsVisible), "the window stays open");
            Assert.False(UiHost.Invoke(() => session.Progress.IsStopping || scanner.LastToken.IsCancellationRequested), "the scan was not cancelled");
            scanner.Open();
            Wait(() => vm.Stage == AppStage.Results, "results", 60);
            Assert.Equal(ScanCompletionState.Complete, UiHost.Invoke(() => session.Results!.State));
        }
        finally { CloseShell(w, scanner); }
    }

    [Test]
    public static void Repeated_new_scan_cycles_reuse_one_session_without_duplicating_work_or_handlers()
    {
        const int cycles = 5;
        var scanner = new GatedScanner();
        var session = new ScanSession(scanner);
        var w = ShellHost.Open(session);
        try
        {
            var vm = UiHost.Invoke(() => w.ViewModel);
            var earlier = new List<WeakReference>();
            int sessionHandlers = -1, progressHandlers = -1;
            for (var cycle = 1; cycle <= cycles; cycle++)
            {
                UiHost.Invoke(() => { vm.SourcePath = SyntheticTree.Root; vm.OutputPath = NewOutput(); vm.CreateWorkbook = false; });
                Wait(() => vm.Stage == AppStage.Setup && vm.StartScanCommand.CanExecute(null), $"cycle {cycle}: ready");
                UiHost.Invoke(() => vm.StartScanCommand.Execute(null));
                Wait(() => vm.Stage == AppStage.Results && !vm.Results!.Exploration.IsLoading, $"cycle {cycle}: results", 60);
                Assert.Equal(cycle, scanner.Calls, $"cycle {cycle}: exactly one call to Core per scan");
                Assert.Equal(ScanCompletionState.Complete, UiHost.Invoke(() => session.Results!.State));
                earlier.Add(Track(session));

                // The elapsed-time clock stops with each scan: no timer survives into the next cycle.
                var elapsed = UiHost.Invoke(() => session.Progress.Elapsed);
                Thread.Sleep(1300);
                Assert.Equal(elapsed, UiHost.Invoke(() => session.Progress.Elapsed), $"cycle {cycle}: the clock stopped");

                UiHost.Invoke(() => vm.NewScanCommand.Execute(null));
                UiHost.Settle();
                var (s, p) = UiHost.Invoke(() => (ShellHost.Subscribers(session), ShellHost.Subscribers(session.Progress)));
                if (cycle == 1) (sessionHandlers, progressHandlers) = (s, p);
                Assert.Equal(sessionHandlers, s, $"cycle {cycle}: handlers on the session");
                Assert.Equal(progressHandlers, p, $"cycle {cycle}: handlers on the progress");
            }
            Assert.True(sessionHandlers <= 2 && progressHandlers <= 2, $"handlers stay few (session {sessionHandlers}, progress {progressHandlers})");

            ShellHost.CollectGarbage();
            var alive = earlier.Count(r => r.IsAlive);
            Console.WriteLine($"      {cycles} cycles: {scanner.Calls} scans, {sessionHandlers} session and {progressHandlers} progress handlers each cycle, {alive} of {cycles} earlier results still alive after GC");
            Assert.True(alive <= 1, $"earlier results are released ({alive} of {cycles} alive)");
            Assert.True(UiHost.Invoke(() => ReferenceEquals(session, w.Session) && ReferenceEquals(session, vm.Session)), "one session throughout");
        }
        finally { CloseShell(w, scanner); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Track(ScanSession session) => UiHost.Invoke(() => new WeakReference(session.Results));
}

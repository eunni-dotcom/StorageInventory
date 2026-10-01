using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using StorageInventory.App.Services;
using StorageInventory.App.ViewModels;
using StorageInventory.App.Views;

namespace StorageInventory.App;

/// <summary>
/// The shell window: a navigation list and the page it shows. Its only page is Scan. The scan belongs to the app
/// session's <see cref="ScanSession"/>, not to this window or to the Scan page's views.
/// </summary>
public partial class MainWindow : Window
{
    private bool _closingAfterScan;

    /// <summary>A window created on its own (as v1's UI tests do) is the shell of its own app session.</summary>
    public MainWindow() : this(new ScanSession())
    {
    }

    public MainWindow(ScanSession session)
    {
        Session = session;
        InitializeComponent();
        var scanPage = new MainViewModel(session, new FolderPicker());
        Shell = new ShellViewModel([new ShellPage("Scan", scanPage)]);
        ShellRoot.DataContext = Shell;   // first, so the shell's bindings never see the page's view model
        DataContext = scanPage;          // v1's contract: the window's DataContext is the scan's view model
        Loaded += (_, _) => FindDescendant<SetupView>(PageHost)?.SourceBox.Focus();
        ConfirmStopAndClose = () => MessageBox.Show(this,
            "A scan is running.\n\nStop the scan and close StorageInventory? The report files created so far will be closed and marked incomplete. Your scanned files are not affected.",
            "Stop the scan?", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    public MainViewModel ViewModel => (MainViewModel)DataContext;

    /// <summary>The app session's scan.</summary>
    public ScanSession Session { get; }

    /// <summary>The navigation list's pages and the page shown.</summary>
    public ShellViewModel Shell { get; }

    /// <summary>Asks whether to stop a running scan and close. Replaceable for automated tests.</summary>
    public Func<bool> ConfirmStopAndClose { get; set; }

    /// <summary>
    /// Closing during a scan never abandons it half-way: the user confirms, the scan is cancelled through Core, the
    /// window waits until Core has closed every report file, and only then closes.
    /// </summary>
    protected override async void OnClosing(CancelEventArgs e)
    {
        if (!Session.IsBusy)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        if (_closingAfterScan || !ConfirmStopAndClose()) return;
        _closingAfterScan = true;
        Session.Cancel();
        await Session.WhenIdle;   // every report stream is closed when this completes
        Close();
    }

    private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            if (FindDescendant<T>(child) is { } deeper) return deeper;
        }
        return null;
    }
}

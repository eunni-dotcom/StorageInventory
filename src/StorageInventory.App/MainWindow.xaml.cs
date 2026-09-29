using System.ComponentModel;
using System.Windows;
using StorageInventory.App.Services;
using StorageInventory.App.ViewModels;

namespace StorageInventory.App;

public partial class MainWindow : Window
{
    private bool _closingAfterScan;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel(new FolderPicker());
        Loaded += (_, _) => SourceBox.Focus();
        ConfirmStopAndClose = () => MessageBox.Show(this,
            "A scan is running.\n\nStop the scan and close StorageInventory? The report files created so far will be closed and marked incomplete. Your scanned files are not affected.",
            "Stop the scan?", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    public MainViewModel ViewModel => (MainViewModel)DataContext;

    /// <summary>Asks whether to stop a running scan and close. Replaceable for automated tests.</summary>
    public Func<bool> ConfirmStopAndClose { get; set; }

    /// <summary>
    /// Closing during a scan never abandons it half-way: the user confirms, the scan is cancelled through Core, the
    /// window waits until Core has closed every report file, and only then closes.
    /// </summary>
    protected override async void OnClosing(CancelEventArgs e)
    {
        if (!ViewModel.IsBusy)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        if (_closingAfterScan || !ConfirmStopAndClose()) return;
        _closingAfterScan = true;
        ViewModel.CancelScan();
        await ViewModel.WhenIdle;   // every report stream is closed when this completes
        Close();
    }
}

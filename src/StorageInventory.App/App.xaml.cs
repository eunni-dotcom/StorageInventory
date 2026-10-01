using System.Windows;
using StorageInventory.App.Services;

namespace StorageInventory.App;

public partial class App : Application
{
    /// <summary>The app session's scan (UI-12): created once, before the shell and its first page.</summary>
    public ScanSession? ScanSession { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ScanSession = new ScanSession();
        new MainWindow(ScanSession).Show();
    }
}

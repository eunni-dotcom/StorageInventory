using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace StorageInventory.App.Views;

/// <summary>The Scanning stage: live progress and Cancel scan.</summary>
public partial class ScanProgressView : UserControl
{
    public ScanProgressView()
    {
        InitializeComponent();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ViewAutomationPeer(this);
}

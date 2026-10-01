using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace StorageInventory.App.Views;

/// <summary>The Scan page: the v1 flow (Setup, Scanning, Results). It owns no state: the scan belongs to the session.</summary>
public partial class ScanPage : UserControl
{
    public ScanPage()
    {
        InitializeComponent();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ViewAutomationPeer(this);
}

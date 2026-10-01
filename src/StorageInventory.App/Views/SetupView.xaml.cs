using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace StorageInventory.App.Views;

/// <summary>The Setup stage: source, report folder, options, pre-flight and Start scan.</summary>
public partial class SetupView : UserControl
{
    public SetupView()
    {
        InitializeComponent();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ViewAutomationPeer(this);
}

using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace StorageInventory.App.Views;

/// <summary>The Results stage: outcome, report actions, the results tabs and New scan.</summary>
public partial class ResultsView : UserControl
{
    public ResultsView()
    {
        InitializeComponent();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ViewAutomationPeer(this);
}

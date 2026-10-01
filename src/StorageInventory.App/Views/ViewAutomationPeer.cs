using System.Windows;
using System.Windows.Automation.Peers;

namespace StorageInventory.App.Views;

/// <summary>
/// Keeps a view out of UI Automation's control and content views, so the controls it hosts appear to screen readers
/// and to UI Automation clients exactly where v1's single window had them: splitting the window into views adds no
/// unnamed "custom" element around them.
/// </summary>
internal sealed class ViewAutomationPeer(FrameworkElement owner) : FrameworkElementAutomationPeer(owner)
{
    protected override bool IsControlElementCore() => false;

    protected override bool IsContentElementCore() => false;
}

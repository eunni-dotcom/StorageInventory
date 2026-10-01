using StorageInventory.App.Mvvm;

namespace StorageInventory.App.ViewModels;

/// <summary>One entry of the shell's navigation list: its visible name and the page's view model, which the shell
/// shows through the page's view.</summary>
public sealed class ShellPage(string title, object viewModel)
{
    public string Title { get; } = title;

    public object ViewModel { get; } = viewModel;

    /// <summary>Also the list item's UI Automation name.</summary>
    public override string ToString() => Title;
}

/// <summary>
/// The shell's navigation (UI-01): the pages and the one shown. The shell hosts exactly one page in this release, Scan;
/// pages are views over their own view models, and long-lived state stays in app-lifetime services (UI-12), so a page
/// can be added later without moving the scan.
/// </summary>
public sealed class ShellViewModel : ObservableObject
{
    private ShellPage _current;

    public ShellViewModel(IReadOnlyList<ShellPage> pages)
    {
        if (pages.Count == 0) throw new ArgumentException("The shell needs at least one page.", nameof(pages));
        Pages = pages;
        _current = pages[0];
    }

    public IReadOnlyList<ShellPage> Pages { get; }

    /// <summary>The page shown. A page is always shown: clearing the list's selection does not empty the shell.</summary>
    public ShellPage? Current
    {
        get => _current;
        set
        {
            if (value is null || !Pages.Contains(value))
            {
                OnPropertyChanged();   // puts the list's selection back on the page that is still shown
                return;
            }
            Set(ref _current, value);
        }
    }
}

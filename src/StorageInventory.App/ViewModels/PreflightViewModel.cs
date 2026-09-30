using System.Collections.ObjectModel;
using StorageInventory.App.Mvvm;
using StorageInventory.Core.Paths;

namespace StorageInventory.App.ViewModels;

public enum PreflightState
{
    /// <summary>Nothing chosen yet.</summary>
    Waiting,
    Checking,
    Ready,
    Warning,
    Blocked,
}

/// <summary>One finding shown in the pre-flight area. Severity is always spelled out, never shown by colour alone.</summary>
public sealed record PreflightIssue(IssueSeverity Severity, string Label, string Glyph, string Subject, string Message, string? Details)
{
    public bool HasDetails => !string.IsNullOrWhiteSpace(Details);

    public string DetailsHeader => Severity == IssueSeverity.Blocked ? "Why is this blocked?" : "Details";

    public static PreflightIssue From(PathIssue issue) => new(
        issue.Severity,
        issue.Severity switch { IssueSeverity.Blocked => "BLOCKED", IssueSeverity.Warning => "WARNING", _ => "INFO" },
        issue.Severity switch { IssueSeverity.Blocked => Glyphs.Blocked, IssueSeverity.Warning => Glyphs.Warning, _ => Glyphs.Info },
        issue.Subject == PathSubject.Source ? "Source" : "Reports",
        issue.Message,
        issue.Details);
}

/// <summary>Segoe Fluent Icons / Segoe MDL2 Assets code points used by the UI.</summary>
public static class Glyphs
{
    public const string Ready = "";      // Completed
    public const string Warning = "";    // Warning
    public const string Blocked = "";    // Important (circle)
    public const string Info = "";       // Info
    public const string Waiting = "";    // Folder
    public const string Checking = "";   // Sync
}

/// <summary>
/// The pre-flight summary. It only presents a <see cref="PathValidationResult"/> produced by Core; it makes no
/// safety decisions of its own.
/// </summary>
public sealed class PreflightViewModel : ObservableObject
{
    private PreflightState _state = PreflightState.Waiting;
    private string _headline = "Choose a folder or drive to scan.";
    private string _sourceSummary = "Not chosen";
    private string _reportsSummary = "Not chosen";
    private string _networkSummary = "None";

    public PreflightState State { get => _state; private set { if (Set(ref _state, value)) { OnPropertyChanged(nameof(StatusLabel)); OnPropertyChanged(nameof(StatusGlyph)); } } }

    public string StatusLabel => State switch
    {
        PreflightState.Ready => "READY TO SCAN",
        PreflightState.Warning => "READY, WITH WARNINGS",
        PreflightState.Blocked => "BLOCKED",
        PreflightState.Checking => "CHECKING",
        _ => "NOT READY",
    };

    public string StatusGlyph => State switch
    {
        PreflightState.Ready => Glyphs.Ready,
        PreflightState.Warning => Glyphs.Warning,
        PreflightState.Blocked => Glyphs.Blocked,
        PreflightState.Checking => Glyphs.Checking,
        _ => Glyphs.Waiting,
    };

    public string Headline { get => _headline; private set => Set(ref _headline, value); }
    public string SourceSummary { get => _sourceSummary; private set => Set(ref _sourceSummary, value); }
    public string ReportsSummary { get => _reportsSummary; private set => Set(ref _reportsSummary, value); }
    public string NetworkSummary { get => _networkSummary; private set => Set(ref _networkSummary, value); }

    /// <summary>Fixed facts about how StorageInventory works, shown every time so the boundary is always visible.</summary>
    public string FileContentsSummary => "Never opened or changed. Only names, sizes, dates and attributes are read.";
    public string LinksSummary => "Junctions and symbolic links are listed but never followed.";
    public string AdministratorSummary => "Not required. Folders you cannot open are reported, not forced.";

    public ObservableCollection<PreflightIssue> Issues { get; } = [];

    public bool CanScan { get; private set; }

    public void ShowChecking()
    {
        State = PreflightState.Checking;
        Headline = "Checking the folders…";
        CanScan = false;
        OnPropertyChanged(nameof(CanScan));
    }

    public void Show(PathValidationResult result, bool sourceEmpty)
    {
        Issues.Clear();
        foreach (var issue in result.Issues.OrderByDescending(i => i.Severity)) Issues.Add(PreflightIssue.From(issue));

        SourceSummary = sourceEmpty ? "Not chosen yet" : result.RootFullPath ?? "Not usable";
        ReportsSummary = result.OutputFullPath is null
            ? "Not usable"
            : result.OutputFolderExists ? result.OutputFullPath : result.OutputFullPath + "  (will be created)";
        NetworkSummary = (result.RootIsNetwork, result.OutputIsNetwork) switch
        {
            (false, false) => "None. Both locations are on this PC.",
            (true, false) => "The source is on a network location.",
            (false, true) => "The report folder is on a network location.",
            _ => "Both locations are on the network.",
        };

        CanScan = result.CanScan;
        OnPropertyChanged(nameof(CanScan));
        if (sourceEmpty)
        {
            State = PreflightState.Waiting;
            Headline = "Choose a folder or drive to scan.";
            // The "choose a source" item is the headline itself; don't repeat it as a blocking error.
            foreach (var i in Issues.Where(i => i.Subject == "Source" && i.Severity == IssueSeverity.Blocked).ToList()) Issues.Remove(i);
        }
        else if (result.Issues.Any(i => i.Severity == IssueSeverity.Blocked))
        {
            State = PreflightState.Blocked;
            Headline = "The scan cannot start until the problems below are fixed.";
        }
        else if (result.Issues.Any(i => i.Severity == IssueSeverity.Warning))
        {
            State = PreflightState.Warning;
            Headline = "You can scan, but please read the warnings below.";
        }
        else
        {
            State = PreflightState.Ready;
            Headline = "Everything checks out. The scan will only read file information.";
        }
    }
}

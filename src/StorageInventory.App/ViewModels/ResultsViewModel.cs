using System.IO;
using System.Windows.Input;
using StorageInventory.App.Mvvm;
using StorageInventory.App.Services;
using StorageInventory.Core;
using StorageInventory.Core.Reports;

namespace StorageInventory.App.ViewModels;

/// <summary>A top-level folder row for the results table (Complete is spelled out, never colour-only).</summary>
public sealed record TopFolderRow(string Folder, string Size, long SizeBytes, string PercentOfRoot, double Percent, string Files, string Complete);

/// <summary>What the finished screen shows. Built only from Core's <see cref="StorageScanResult"/>.</summary>
public sealed class ResultsViewModel : ObservableObject
{
    private string _actionStatus = "";

    public ResultsViewModel(StorageScanResult result, WorkbookExportResult? workbook, TimeSpan elapsed, IReportOpener? opener = null)
    {
        Result = result;
        var workbookPath = workbook?.State == WorkbookExportState.Created ? workbook.WorkbookPath : null;
        _opener = opener ?? new ReportOpener(result, workbookPath);
        OpenFilesReportCommand = new RelayCommand(() => Open(result.Reports?.FilesCsv), () => result.Finished);
        OpenFoldersReportCommand = new RelayCommand(() => Open(result.Reports?.FoldersCsv), () => result.Finished);
        OpenErrorsReportCommand = new RelayCommand(() => Open(result.Reports?.ErrorsCsv), () => result.Finished);
        OpenReportFolderCommand = new RelayCommand(() => Open(result.OutputPath), () => result.Finished);
        OpenWorkbookCommand = new RelayCommand(() => Open(workbookPath), () => workbookPath is not null);
        HasWorkbook = workbookPath is not null;
        Workbook = workbook;
        Elapsed = Format.Elapsed(elapsed);
        (Headline, Explanation) = result.State switch
        {
            ScanCompletionState.Complete => ("SCAN COMPLETE", "Every folder was read. The totals are exact."),
            ScanCompletionState.Incomplete => ("SCAN FINISHED, BUT INCOMPLETE",
                $"{Format.Count(result.Totals.LocallyIncompleteFolders)} folder(s) could not be read, so totals that include them are lower bounds. The Errors report lists each one. The reports are otherwise complete."),
            ScanCompletionState.Cancelled => ("SCAN CANCELLED",
                "You stopped the scan. No reports were completed. Your files were not touched."),
            _ => ("SCAN FAILED", (result.Failure?.Message ?? "The scan could not finish.") + " Your files were not touched."),
        };
        Glyph = result.State switch
        {
            ScanCompletionState.Complete => Glyphs.Ready,
            ScanCompletionState.Incomplete => Glyphs.Warning,
            _ => Glyphs.Blocked,
        };
        TopFolders = result.LargestTopLevelFolders.Select(f => new TopFolderRow(
            f.Name,
            Format.Bytes(f.TotalSizeBytes),
            f.TotalSizeBytes,
            Format.Percent(f.PercentOfRoot),
            f.PercentOfRoot,
            Format.Count(f.TotalFileCount),
            f.Status == FolderScanStatus.ReparsePointSkipped ? "Link (not followed)" : f.SubtreeComplete ? "Yes" : "No")).ToList();
        WorkbookStatus = workbook switch
        {
            null => null,
            { State: WorkbookExportState.Created } w => "Excel workbook created. " + (w.FilesSheetOmitted ? w.Message : ""),
            { } w => w.Message,
        };
    }

    private readonly IReportOpener _opener;

    public StorageScanResult Result { get; }
    public ExplorationViewModel Exploration { get; } = new();

    public ICommand OpenFilesReportCommand { get; }
    public ICommand OpenFoldersReportCommand { get; }
    public ICommand OpenErrorsReportCommand { get; }
    public ICommand OpenReportFolderCommand { get; }
    public ICommand OpenWorkbookCommand { get; }
    public bool HasWorkbook { get; }

    /// <summary>Feedback if a report could not be opened (never a silent failure).</summary>
    public string ActionStatus { get => _actionStatus; private set { if (Set(ref _actionStatus, value)) OnPropertyChanged(nameof(HasActionStatus)); } }
    public bool HasActionStatus => ActionStatus.Length > 0;

    private void Open(string? path)
    {
        if (path is null) return;
        try
        {
            _opener.Open(path);
            ActionStatus = "";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            ActionStatus = $"Could not open '{path}': {ex.Message}";
        }
    }
    public WorkbookExportResult? Workbook { get; }
    public string Headline { get; }
    public string Explanation { get; }
    public string Glyph { get; }
    public string Elapsed { get; }
    public ScanCompletionState State => Result.State;
    public bool Finished => Result.Finished;
    public bool HasIncompleteArtifacts => IncompleteArtifacts.Count > 0;
    public IReadOnlyList<string> IncompleteArtifacts =>
        [.. Result.IncompleteArtifacts, .. Workbook?.UnfinishedPath is { } p ? new[] { p } : []];

    public string Files => Format.Count(Result.Totals.Files);
    public string Folders => Format.Count(Result.Totals.Folders);
    public string TotalData => Format.Bytes(Result.Totals.Bytes);
    public string Errors => Format.Count(Result.Totals.ScanErrors);
    public string LinksSkipped => Format.Count(Result.Totals.ReparsePointsSkipped);
    public string Completeness => Result.State switch
    {
        ScanCompletionState.Complete => "Complete",
        ScanCompletionState.Incomplete => "Incomplete (totals are lower bounds)",
        ScanCompletionState.Cancelled => "Cancelled",
        _ => "Failed",
    };

    /// <summary>For cancelled/failed scans the numbers are partial and must not read like final totals.</summary>
    public bool CountsArePartial => !Result.Finished;

    public IReadOnlyList<TopFolderRow> TopFolders { get; }
    public bool HasTopFolders => TopFolders.Count > 0;
    public string? WorkbookStatus { get; }
    public bool HasWorkbookStatus => !string.IsNullOrWhiteSpace(WorkbookStatus);
}

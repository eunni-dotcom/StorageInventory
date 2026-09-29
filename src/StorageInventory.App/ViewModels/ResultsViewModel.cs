using StorageInventory.App.Mvvm;
using StorageInventory.Core;
using StorageInventory.Core.Reports;

namespace StorageInventory.App.ViewModels;

/// <summary>A top-level folder row for the results table (Complete is spelled out, never colour-only).</summary>
public sealed record TopFolderRow(string Folder, string Size, long SizeBytes, string PercentOfRoot, double Percent, string Files, string Complete);

/// <summary>What the finished screen shows. Built only from Core's <see cref="StorageScanResult"/>.</summary>
public sealed class ResultsViewModel : ObservableObject
{
    public ResultsViewModel(StorageScanResult result, WorkbookExportResult? workbook, TimeSpan elapsed)
    {
        Result = result;
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

    public StorageScanResult Result { get; }
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

using System.IO;
using StorageInventory.App.Mvvm;
using StorageInventory.Core;
using StorageInventory.Core.Reports;

namespace StorageInventory.App.ViewModels;

public sealed record FolderRowView(string Folder, string Size, long SizeBytes, string PercentOfRoot, double Percent, string Files, long FileCount, string Status);
public sealed record FileRowView(string File, string Size, long SizeBytes, string Type, string Modified);
public sealed record TypeRowView(string Name, string Category, string Files, long FileCount, string Size, long SizeBytes);
public sealed record ErrorRowView(string Type, string Path, string Message);

/// <summary>
/// Read-only browsing of a finished run's reports (largest folders, largest files, file types, errors), loaded in the
/// background from the CSV reports through Core's <see cref="ReportExplorer"/>. Bounded: at most a few thousand rows
/// per table, whatever the size of the reports.
/// </summary>
public sealed class ExplorationViewModel : ObservableObject
{
    public const int FolderLimit = 2000;
    public const int FileLimit = 1000;
    public const int ExtensionLimit = 200;
    public const int ErrorLimit = 5000;

    private bool _isLoading = true;
    private string _status = "Loading details from the reports…";

    public IReadOnlyList<FolderRowView> Folders { get; private set; } = [];
    public IReadOnlyList<FileRowView> Files { get; private set; } = [];
    public IReadOnlyList<TypeRowView> Categories { get; private set; } = [];
    public IReadOnlyList<TypeRowView> Extensions { get; private set; } = [];
    public IReadOnlyList<ErrorRowView> Errors { get; private set; } = [];
    public string FilesNote { get; private set; } = "";
    public string FoldersNote { get; private set; } = "";
    public string ErrorsNote { get; private set; } = "";

    public bool IsLoading { get => _isLoading; private set => Set(ref _isLoading, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }

    public async Task LoadAsync(StorageScanResult scan, CancellationToken cancellationToken)
    {
        try
        {
            var data = await Task.Run(() => new
            {
                Folders = ReportExplorer.LargestFolders(scan, FolderLimit, cancellationToken),
                Files = ReportExplorer.LargestFiles(scan, FileLimit, cancellationToken),
                Types = ReportExplorer.FileTypes(scan, ExtensionLimit, cancellationToken),
                Errors = ReportExplorer.Errors(scan, ErrorLimit, cancellationToken),
            }, cancellationToken);

            Folders = [.. data.Folders.Select(f => new FolderRowView(f.RelativePath, Format.Bytes(f.TotalBytes), f.TotalBytes, Format.Percent(f.PercentOfRoot),
                f.PercentOfRoot, Format.Count(f.TotalFiles), f.TotalFiles, DescribeStatus(f)))];
            Files = [.. data.Files.Select(f => new FileRowView(f.RelativePath, Format.Bytes(f.SizeBytes), f.SizeBytes, f.FileType, f.Modified))];
            Categories = [.. data.Types.Categories.Select(t => new TypeRowView(t.Name, t.Category, Format.Count(t.Files), t.Files, Format.Bytes(t.Bytes), t.Bytes))];
            Extensions = [.. data.Types.Extensions.Select(t => new TypeRowView(t.Name, t.Category, Format.Count(t.Files), t.Files, Format.Bytes(t.Bytes), t.Bytes))];
            Errors = [.. data.Errors.Rows.Select(e => new ErrorRowView(e.ErrorType, e.Path, e.Message))];

            FoldersNote = scan.Totals.Folders > FolderLimit
                ? $"Showing the {Format.Count(FolderLimit)} largest of {Format.Count(scan.Totals.Folders)} folders. The Folders report has all of them."
                : $"All {Format.Count(scan.Totals.Folders)} folders.";
            FilesNote = scan.Totals.Files > FileLimit
                ? $"Showing the {Format.Count(FileLimit)} largest of {Format.Count(scan.Totals.Files)} files. The Files report has all of them."
                : $"All {Format.Count(scan.Totals.Files)} files.";
            var counts = string.Join(", ", data.Errors.CountsByType.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}: {Format.Count(kv.Value)}"));
            ErrorsNote = data.Errors.TotalRows == 0 ? "Nothing to report: every item could be read and no links were found."
                : (data.Errors.TotalRows > ErrorLimit ? $"Showing the first {Format.Count(ErrorLimit)} of {Format.Count(data.Errors.TotalRows)} rows. " : "")
                  + counts + ". ReparsePointSkipped and ReparsePointFile rows are informational, not errors.";
            Status = "";
            foreach (var name in new[] { nameof(Folders), nameof(Files), nameof(Categories), nameof(Extensions), nameof(Errors), nameof(FilesNote), nameof(FoldersNote), nameof(ErrorsNote) })
                OnPropertyChanged(name);
        }
        catch (OperationCanceledException)
        {
            Status = "";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or FormatException)
        {
            Status = "The details could not be loaded from the reports: " + ex.Message + " The reports themselves are unaffected.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static string DescribeStatus(ExploredFolder f) => f.ScanStatus switch
    {
        "OK" => f.SubtreeComplete ? "Complete" : "Incomplete (something inside could not be read)",
        "ReparsePointSkipped" => "Link (not followed)",
        var s when s.StartsWith("Partial", StringComparison.Ordinal) => "Incomplete (some entries could not be read)",
        var s => $"Could not be read ({s})",
    };
}

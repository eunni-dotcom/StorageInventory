namespace StorageInventory.Core.Reports;

public enum WorkbookExportState
{
    Created,
    Failed,
    Cancelled,
}

/// <summary>Outcome of the optional workbook export. It never changes the scan result.</summary>
/// <param name="WorkbookPath">The finished workbook (only when <see cref="WorkbookExportState.Created"/>).</param>
/// <param name="UnfinishedPath">A '.xlsx.partial' file THIS export created and could not finish or publish. It is
/// not a valid report, is safe to delete, and is never deleted automatically.</param>
/// <param name="FilesSheetOmitted">True when the Files sheet was left out because it exceeds Excel's row limit.</param>
public sealed record WorkbookExportResult(WorkbookExportState State, string? WorkbookPath, string? UnfinishedPath, string Message, bool FilesSheetOmitted);

/// <summary>
/// Optional post-processing: builds StorageInventory_&lt;run&gt;.xlsx from the COMPLETED CSV reports of a finished
/// scan. Excel does not need to be installed. The CSV reports stay authoritative; a workbook failure never affects
/// them. The workbook is written to '.xlsx.partial' with create-new semantics and only then renamed to its final name
/// with a rename that never replaces an existing file.
/// </summary>
public static class WorkbookExporter
{
    private static readonly HashSet<string> FilesNumeric = ["SizeBytes", "SizeKB", "SizeMB", "SizeGB"];
    private static readonly HashSet<string> FoldersNumeric =
    [
        "Depth", "TotalSizeBytes", "TotalSizeKB", "TotalSizeMB", "TotalSizeGB", "TotalSizeTB", "PercentOfRoot", "PercentOfParent",
        "DirectSizeBytes", "DirectSizeMB", "DirectFileCount", "TotalFileCount", "DirectSubfolderCount", "TotalSubfolderCount",
        "AverageFileSizeMB", "LargestFileSizeMB",
    ];

    public static WorkbookExportResult Export(StorageScanResult scan, CancellationToken cancellationToken = default)
        => Export(scan, XlsxWorkbookWriter.ExcelMaxDataRows, cancellationToken);

    internal static WorkbookExportResult Export(StorageScanResult scan, int maxDataRows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scan);
        if (!scan.Finished || scan.Reports is null)
        {
            return new(WorkbookExportState.Failed, null, null, "A workbook can only be made from a finished scan's reports.", false);
        }

        var run = ReportRun.ForFinishedRun(scan.OutputPath, scan.RunId);
        var filesRows = CountDataRows(scan.Reports.FilesCsv);
        var omitFiles = filesRows > maxDataRows;
        var sheets = new List<XlsxWorkbookWriter.Sheet>();
        if (!omitFiles) sheets.Add(new("Files", scan.Reports.FilesCsv, FilesNumeric));
        sheets.Add(new("Folders", scan.Reports.FoldersCsv, FoldersNumeric));

        try
        {
            using (var stream = run.CreateNew(run.WorkbookPartial))
            {
                XlsxWorkbookWriter.Write(stream, sheets, cancellationToken);
            }
            run.PublishOwnWorkbook();
            var note = omitFiles
                ? $" The Files sheet was left out: {filesRows:N0} files exceeds Excel's limit of {maxDataRows:N0} rows (the Files CSV is complete)."
                : "";
            return new(WorkbookExportState.Created, run.Workbook, null, "Workbook created." + note, omitFiles);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            var unfinished = run.CreatedFiles.Contains(run.WorkbookPartial) ? run.WorkbookPartial : null;
            var state = ex is OperationCanceledException ? WorkbookExportState.Cancelled : WorkbookExportState.Failed;
            var message = (state == WorkbookExportState.Cancelled ? "Workbook export cancelled." : "Workbook not created: " + ex.Message)
                          + " The CSV reports are complete and unaffected."
                          + (unfinished is null ? "" : $" An unfinished workbook created by this export was left at '{unfinished}'; it is not a valid report and is safe to delete.");
            return new(state, null, unfinished, message, omitFiles);
        }
    }

    private static int CountDataRows(string csvPath)
    {
        using var reader = new ReportCsvReader(csvPath);
        var rows = 0;
        while (reader.ReadRecord() is not null) rows++;
        return rows;
    }
}

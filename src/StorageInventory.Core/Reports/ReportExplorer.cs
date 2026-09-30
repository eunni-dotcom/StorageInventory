using System.Globalization;

namespace StorageInventory.Core.Reports;

public sealed record ExploredFolder(string RelativePath, long TotalBytes, double PercentOfRoot, long TotalFiles, string ScanStatus, bool SubtreeComplete);

public sealed record ExploredFile(string RelativePath, long SizeBytes, string FileType, string Modified);

public sealed record FileTypeTotal(string Name, string Category, long Files, long Bytes);

public sealed record ExploredError(string Path, string ErrorType, string Message);

public sealed record FileTypeSummary(IReadOnlyList<FileTypeTotal> Categories, IReadOnlyList<FileTypeTotal> Extensions);

public sealed record ErrorSummary(IReadOnlyList<ExploredError> Rows, IReadOnlyDictionary<string, long> CountsByType, long TotalRows);

/// <summary>
/// Read-only exploration of a finished run's CSV reports, separate from the scan engine: it never touches the
/// scanned tree. Every method streams its report and keeps at most <c>limit</c> rows, so multi-million-row reports
/// never have to fit in memory. Paths are derived from the FullPath column (never from Excel-guarded text), so
/// names are shown exactly as they are on disk.
/// </summary>
public static class ReportExplorer
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The largest folders (the Folders report is already sorted largest-first).</summary>
    public static IReadOnlyList<ExploredFolder> LargestFolders(StorageScanResult scan, int limit, CancellationToken cancellationToken = default)
    {
        var reports = RequireFinished(scan);
        using var reader = new ReportCsvReader(reports.FoldersCsv);
        var c = Columns(reader, "FullPath", "TotalSizeBytes", "PercentOfRoot", "TotalFileCount", "ScanStatus", "SubtreeComplete");
        var rows = new List<ExploredFolder>();
        while (rows.Count < limit && reader.ReadRecord() is { } r)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rows.Add(new ExploredFolder(Relative(scan.RootPath, r[c[0]]), long.Parse(r[c[1]], Inv), double.Parse(r[c[2]], Inv),
                long.Parse(r[c[3]], Inv), r[c[4]], r[c[5]] == "True"));
        }
        return rows;
    }

    /// <summary>The largest files: read directly from a sorted report, or with a streaming top-N selection.</summary>
    public static IReadOnlyList<ExploredFile> LargestFiles(StorageScanResult scan, int limit, CancellationToken cancellationToken = default)
    {
        var reports = RequireFinished(scan);
        using var reader = new ReportCsvReader(reports.FilesCsv);
        var c = Columns(reader, "FullPath", "SizeBytes", "FileType", "ModifiedDate");
        ExploredFile Row(string[] r) => new(Relative(scan.RootPath, r[c[0]]), long.Parse(r[c[1]], Inv), r[c[2]], r[c[3]]);

        if (reports.FilesSortedLargestFirst)
        {
            var sorted = new List<ExploredFile>();
            while (sorted.Count < limit && reader.ReadRecord() is { } r) sorted.Add(Row(r));
            return sorted;
        }

        // Unsorted report: keep only the current top `limit` (min-heap on size; discovery order breaks ties).
        var heap = new PriorityQueue<(ExploredFile File, long Sequence), (long Size, long NegSequence)>();
        long sequence = 0;
        while (reader.ReadRecord() is { } r)
        {
            if ((sequence & 8191) == 0) cancellationToken.ThrowIfCancellationRequested();
            var size = long.Parse(r[c[1]], Inv);
            var key = (size, -sequence);
            if (heap.Count < limit) heap.Enqueue((Row(r), sequence), key);
            else if (heap.TryPeek(out _, out var smallest) && key.CompareTo(smallest) > 0) heap.EnqueueDequeue((Row(r), sequence), key);
            sequence++;
        }
        var result = new List<(ExploredFile File, long Sequence)>(heap.Count);
        while (heap.TryDequeue(out var item, out _)) result.Add(item);
        return result.OrderByDescending(x => x.File.SizeBytes).ThenBy(x => x.Sequence).Select(x => x.File).ToList();
    }

    /// <summary>Totals by file-type category and by extension, in one streaming pass over the Files report.</summary>
    public static FileTypeSummary FileTypes(StorageScanResult scan, int extensionLimit, CancellationToken cancellationToken = default)
    {
        var reports = RequireFinished(scan);
        using var reader = new ReportCsvReader(reports.FilesCsv);
        var c = Columns(reader, "Extension", "FileType", "SizeBytes");
        var categories = new Dictionary<string, (long Files, long Bytes)>(StringComparer.Ordinal);
        var extensions = new Dictionary<string, (string Category, long Files, long Bytes)>(StringComparer.OrdinalIgnoreCase);
        long n = 0;
        while (reader.ReadRecord() is { } r)
        {
            if ((n++ & 8191) == 0) cancellationToken.ThrowIfCancellationRequested();
            var size = long.Parse(r[c[2]], Inv);
            var category = r[c[1]];
            var (cf, cb) = categories.GetValueOrDefault(category);
            categories[category] = (cf + 1, cb + size);
            var ext = r[c[0]].Length == 0 ? "(none)" : r[c[0]].ToLowerInvariant();
            var (_, ef, eb) = extensions.GetValueOrDefault(ext);
            extensions[ext] = (category, ef + 1, eb + size);
        }
        return new FileTypeSummary(
            categories.Select(kv => new FileTypeTotal(kv.Key, kv.Key, kv.Value.Files, kv.Value.Bytes)).OrderByDescending(x => x.Bytes).ThenBy(x => x.Name, StringComparer.Ordinal).ToList(),
            extensions.Select(kv => new FileTypeTotal(kv.Key, kv.Value.Category, kv.Value.Files, kv.Value.Bytes)).OrderByDescending(x => x.Bytes).ThenBy(x => x.Name, StringComparer.Ordinal).Take(extensionLimit).ToList());
    }

    /// <summary>The first <paramref name="limit"/> error rows, plus counts by type over the whole report.</summary>
    public static ErrorSummary Errors(StorageScanResult scan, int limit, CancellationToken cancellationToken = default)
    {
        var reports = RequireFinished(scan);
        using var reader = new ReportCsvReader(reports.ErrorsCsv);
        var c = Columns(reader, "Path", "ErrorType", "Message");
        var rows = new List<ExploredError>();
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        long total = 0;
        while (reader.ReadRecord() is { } r)
        {
            if ((total & 8191) == 0) cancellationToken.ThrowIfCancellationRequested();
            total++;
            counts[r[c[1]]] = counts.GetValueOrDefault(r[c[1]]) + 1;
            if (rows.Count < limit) rows.Add(new ExploredError(Unguard(r[c[0]]), r[c[1]], Unguard(r[c[2]])));
        }
        return new ErrorSummary(rows, counts, total);
    }

    private static ReportSet RequireFinished(StorageScanResult scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        return scan.Finished && scan.Reports is { } reports ? reports : throw new InvalidOperationException("Only a finished scan's reports can be explored.");
    }

    private static int[] Columns(ReportCsvReader reader, params string[] names) =>
        [.. names.Select(n => reader.ColumnIndex(n) is var i and >= 0 ? i : throw new InvalidDataException($"The report has no '{n}' column."))];

    /// <summary>Relative display path from the exact FullPath ("." for the root).</summary>
    internal static string Relative(string root, string fullPath)
    {
        var prefix = root.EndsWith('\\') ? root : root + "\\";
        if (string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)) return ".";
        return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? fullPath[prefix.Length..] : fullPath;
    }

    /// <summary>Paths and messages in the errors report start with a drive letter or ordinary text; only a guard
    /// apostrophe followed by a formula character is removed for display.</summary>
    private static string Unguard(string value) =>
        value.Length >= 2 && value[0] == '\'' && "=+-@\t\r\n".Contains(value[1]) ? value[1..] : value;
}

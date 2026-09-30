using System.Runtime.InteropServices;
using System.Text;
using StorageInventory.Core.Scanning;

namespace StorageInventory.Core.Reports;

/// <summary>Writes the Folders report and the sorted Files report after the scan.</summary>
internal static class ReportWriters
{
    /// <summary>Folder indices sorted by total size (largest first); ties keep discovery order, so a folder always
    /// precedes an equally-sized child (same ordering as the reference).</summary>
    public static int[] FolderOrder(IReadOnlyList<FolderState> folders)
    {
        var order = new int[folders.Count];
        for (var i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, (a, b) =>
        {
            var bySize = folders[b].TotalSizeBytes.CompareTo(folders[a].TotalSizeBytes);
            return bySize != 0 ? bySize : a.CompareTo(b);
        });
        return order;
    }

    public static void WriteFolders(ReportRun run, IReadOnlyList<FolderState> folders, int[] order, string rootFullPath,
        Action<long, long> onProgress, CancellationToken cancellationToken)
    {
        using var writer = new StreamWriter(run.CreateNew(run.FoldersCsv), CsvFormat.Utf8WithBom, 65536) { NewLine = "\r\n" };
        writer.WriteLine(CsvFormat.FoldersHeader);
        var sb = new StringBuilder(512);
        for (var i = 0; i < order.Length; i++)
        {
            if ((i & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                onProgress(i, order.Length);
            }
            var r = FolderAggregator.ToRecord(folders, order[i], rootFullPath);
            sb.Clear();
            CsvFormat.AppendText(sb, r.Name); sb.Append(',');
            CsvFormat.AppendText(sb, r.RelativePath); sb.Append(',');
            CsvFormat.AppendText(sb, r.ParentRelativePath); sb.Append(',');
            CsvFormat.AppendText(sb, r.FullPath); sb.Append(',');
            sb.Append(CsvFormat.Integer(r.Depth)).Append(',');
            sb.Append(CsvFormat.Integer(r.TotalSizeBytes)).Append(',');
            sb.Append(CsvFormat.Scaled(r.TotalSizeBytes, CsvFormat.KB, "0.00")).Append(',');
            sb.Append(CsvFormat.Scaled(r.TotalSizeBytes, CsvFormat.MB, "0.00")).Append(',');
            sb.Append(CsvFormat.Scaled(r.TotalSizeBytes, CsvFormat.GB, "0.000")).Append(',');
            sb.Append(CsvFormat.Scaled(r.TotalSizeBytes, CsvFormat.TB, "0.0000")).Append(',');
            sb.Append(CsvFormat.Number(r.PercentOfRoot, "0.000")).Append(',');
            sb.Append(r.PercentOfParent is { } p ? CsvFormat.Number(p, "0.000") : "").Append(',');
            sb.Append(CsvFormat.Integer(r.DirectSizeBytes)).Append(',');
            sb.Append(CsvFormat.Scaled(r.DirectSizeBytes, CsvFormat.MB, "0.00")).Append(',');
            sb.Append(CsvFormat.Integer(r.DirectFileCount)).Append(',');
            sb.Append(CsvFormat.Integer(r.TotalFileCount)).Append(',');
            sb.Append(CsvFormat.Integer(r.DirectSubfolderCount)).Append(',');
            sb.Append(CsvFormat.Integer(r.TotalSubfolderCount)).Append(',');
            sb.Append(r.TotalFileCount > 0 ? CsvFormat.Number((double)r.TotalSizeBytes / r.TotalFileCount / CsvFormat.MB, "0.00") : "").Append(',');
            sb.Append(r.LargestFileBytes is { } largest ? CsvFormat.Scaled(largest, CsvFormat.MB, "0.00") : "").Append(',');
            CsvFormat.AppendText(sb, r.LargestFileRelativePath); sb.Append(',');
            sb.Append(CsvFormat.Date(r.CreatedUtc)).Append(',');
            sb.Append(CsvFormat.Date(r.ModifiedUtc)).Append(',');
            CsvFormat.AppendText(sb, r.Attributes.ToString()); sb.Append(',');
            CsvFormat.AppendText(sb, r.StatusText); sb.Append(',');
            sb.Append(r.SubtreeComplete ? "True" : "False");
            writer.WriteLine(sb.ToString());
        }
    }

    /// <summary>Sorts the compact index in place (size descending, discovery order for ties) and copies each row,
    /// byte for byte, from the run's temporary file into the final Files report.</summary>
    public static void WriteSortedFiles(ReportRun run, List<SortEntry> index, int longestRowBytes,
        Action<long, long> onProgress, CancellationToken cancellationToken)
    {
        CollectionsMarshal.AsSpan(index).Sort(static (a, b) =>
        {
            var bySize = b.Size.CompareTo(a.Size);
            return bySize != 0 ? bySize : a.Sequence.CompareTo(b.Sequence);
        });

        using var output = run.CreateNew(run.FilesCsv);
        CsvReportSink.WriteBomAndHeader(output, CsvFormat.FilesHeader);

        // Read-only access to this run's OWN temporary file (never to anything in the scanned tree).
        using var input = new FileStream(run.FilesTemporary, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        var buffer = new byte[Math.Max(longestRowBytes, 1)];
        for (var k = 0; k < index.Count; k++)
        {
            if ((k & 16383) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                onProgress(k, index.Count);
            }
            var entry = index[k];
            input.Position = entry.Offset;
            input.ReadExactly(buffer, 0, entry.Length);
            output.Write(buffer, 0, entry.Length);
        }
    }
}

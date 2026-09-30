using System.Text;
using System.Text.RegularExpressions;
using StorageInventory.Core;
using StorageInventory.Core.Reports;
using StorageInventory.Testing;

namespace StorageInventory.IntegrationTests;

/// <summary>Runs synchronously on the scanning thread, so a test can act at a precise point of a scan.</summary>
internal sealed class InlineProgress(Action<StorageScanProgress> onReport) : IProgress<StorageScanProgress>
{
    public void Report(StorageScanProgress value) => onReport(value);
}

public static class ReportPipelineTests
{
    private static PhaseAFixture Fx => PhaseAFixture.Shared;
    private static readonly Lazy<(StorageScanResult Result, string Output)> SortedRun = new(() => RunScan(sort: true));

    private static (StorageScanResult Result, string Output) RunScan(bool sort, string? root = null, IProgress<StorageScanProgress>? progress = null, CancellationToken token = default)
    {
        var output = Path.Combine(Fx.Base, "reports_" + Guid.NewGuid().ToString("N")[..6]);
        var result = new InventoryScanner().Scan(new StorageScanOptions
        {
            RootPath = root ?? Fx.Root, OutputPath = output, SortFiles = sort, ProgressInterval = TimeSpan.Zero,
        }, progress, token);
        return (result, output);
    }

    private static string Unguard(string v) => v.Length >= 2 && v[0] == '\'' && "=+-@".Contains(v[1]) ? v[1..] : v;

    [Test]
    public static void A_readable_tree_is_Complete_and_an_unreadable_one_is_Incomplete()
    {
        var (complete, _) = RunScan(sort: true, root: Path.Combine(Fx.Root, @"Kpop\TWICE"));
        Assert.Equal(ScanCompletionState.Complete, complete.State);
        Assert.True(complete.IsComplete);
        var (incomplete, _) = SortedRun.Value;
        Assert.Equal(ScanCompletionState.Incomplete, incomplete.State);
        Assert.False(incomplete.IsComplete);
        Assert.True(incomplete.Finished);
        Assert.Equal(2L, incomplete.Totals.LocallyIncompleteFolders);
        Assert.Equal(3L, incomplete.Totals.AffectedAncestorFolders);
    }

    [Test]
    public static void Reports_are_named_per_run_UTF8_with_BOM_CRLF_and_exact_headers()
    {
        var (r, output) = SortedRun.Value;
        var reports = Assert.NotNull(r.Reports);
        Assert.True(Regex.IsMatch(r.RunId, @"^\d{8}_\d{6}_[0-9a-f]{6}$"), r.RunId);
        Assert.Equal(Path.Combine(output, $"Files_{r.RunId}.csv"), reports.FilesCsv);
        Assert.Equal(Path.Combine(output, $"Folders_{r.RunId}.csv"), reports.FoldersCsv);
        Assert.Equal(Path.Combine(output, $"ScanErrors_{r.RunId}.csv"), reports.ErrorsCsv);
        Assert.SequenceEqual(new[] { reports.FilesCsv, reports.FoldersCsv, reports.ErrorsCsv }.Order(StringComparer.OrdinalIgnoreCase),
            Directory.GetFiles(output).Order(StringComparer.OrdinalIgnoreCase), "exactly three files; the temporary file was removed");
        foreach (var (path, header) in new[] { (reports.FilesCsv, CsvFormat.FilesHeader), (reports.FoldersCsv, CsvFormat.FoldersHeader), (reports.ErrorsCsv, CsvFormat.ErrorsHeader) })
        {
            var bytes = File.ReadAllBytes(path);
            Assert.True(bytes.Length > 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "UTF-8 BOM: " + path);
            var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            Assert.True(text.StartsWith(header + "\r\n", StringComparison.Ordinal), "header: " + path);
            Assert.False(Regex.IsMatch(text, "(?<!\r)\n"), "every line ends with CRLF: " + path);
        }
    }

    [Test]
    public static void Files_report_matches_ground_truth_sorted_largest_first_with_safe_text()
    {
        var (r, _) = SortedRun.Value;
        var rows = ReportCsvReader.ReadAll(r.Reports!.FilesCsv);
        var got = rows.Select(x => Unguard(x["RelativePath"]) + "|" + x["SizeBytes"]).Order(StringComparer.Ordinal).ToList();
        Assert.SequenceEqual(Fx.Expected.Select(e => e.RelativePath + "|" + e.Size).Order(StringComparer.Ordinal), got, "files");
        var sizes = rows.Select(x => long.Parse(x["SizeBytes"])).ToList();
        for (var i = 1; i < sizes.Count; i++) Assert.True(sizes[i] <= sizes[i - 1], "sorted largest first");
        foreach (var row in rows)
        {
            Assert.True(File.Exists(row["FullPath"]), "FullPath round-trips: " + row["FullPath"]);
            var realName = Path.GetFileName(row["FullPath"]);
            var realRel = Path.GetRelativePath(Fx.Root, row["FullPath"]);
            Assert.Equal("=+-@".Contains(realName[0]) ? "'" + realName : realName, row["FileName"], "guard: FileName");
            Assert.Equal("=+-@".Contains(realRel[0]) ? "'" + realRel : realRel, row["RelativePath"], "guard: RelativePath");
            var bytes = long.Parse(row["SizeBytes"]);
            Assert.Equal((bytes / 1024.0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), row["SizeKB"]);
            Assert.Equal(FileTypeCatalog.CategoryOf(row["Extension"]), row["FileType"]);
        }
        Assert.Equal("", rows.Single(x => x["FileName"] == "bad-time.dat")["ModifiedDate"], "invalid timestamp is blank");
    }

    [Test]
    public static void Folders_report_matches_the_result_and_is_sorted()
    {
        var (r, _) = SortedRun.Value;
        var rows = ReportCsvReader.ReadAll(r.Reports!.FoldersCsv);
        Assert.Equal(r.Totals.Folders, (long)rows.Count);
        var totals = rows.Select(x => long.Parse(x["TotalSizeBytes"])).ToList();
        for (var i = 1; i < totals.Count; i++) Assert.True(totals[i] <= totals[i - 1], "sorted by total size");
        var root = rows[0];
        Assert.Equal(".", root["RelativePath"]);
        Assert.Equal(r.Root!.TotalSizeBytes.ToString(), root["TotalSizeBytes"]);
        Assert.Equal("False", root["SubtreeComplete"]);
        Assert.Equal("100.000", root["PercentOfRoot"]);
        Assert.Equal("", root["PercentOfParent"]);
        Assert.Equal("AccessDenied", rows.Single(x => x["RelativePath"] == "Denied")["ScanStatus"]);
        Assert.Equal("ReparsePointSkipped", rows.Single(x => x["RelativePath"] == "Loop")["ScanStatus"]);
        Assert.Equal("'-Dash Folder", rows.Single(x => x["FolderName"] == "'-Dash Folder")["RelativePath"], "guarded folder path");
    }

    [Test]
    public static void Errors_report_lists_every_error_and_informational_row()
    {
        var (r, _) = SortedRun.Value;
        var rows = ReportCsvReader.ReadAll(r.Reports!.ErrorsCsv);
        Assert.Equal(2, rows.Count(x => x["ErrorType"] == "AccessDenied"));
        Assert.Equal(2, rows.Count(x => x["ErrorType"] == "ReparsePointSkipped"));
        Assert.Equal(1, rows.Count(x => x["ErrorType"] == "InvalidTimestamp"));
        Assert.Equal(r.Totals.ScanErrors, (long)rows.Count(x => x["ErrorType"] is not ("ReparsePointSkipped" or "ReparsePointFile")));
        Assert.False(rows.Any(x => x["Message"].Contains('\n')), "messages are one line");
    }

    [Test]
    public static void Result_exposes_root_and_top_level_folders_largest_first()
    {
        var (r, _) = SortedRun.Value;
        Assert.Equal(Fx.Expected.Sum(e => e.Size), r.Totals.Bytes);
        Assert.Equal((long)Fx.Expected.Count, r.Totals.Files);
        Assert.True(r.LargestTopLevelFolders.All(f => f.Depth == 1));
        Assert.Equal("Kpop", r.LargestTopLevelFolders[0].Name);
        for (var i = 1; i < r.LargestTopLevelFolders.Count; i++)
            Assert.True(r.LargestTopLevelFolders[i].TotalSizeBytes <= r.LargestTopLevelFolders[i - 1].TotalSizeBytes);
    }

    [Test]
    public static void Unsorted_mode_never_creates_a_temporary_file_and_reports_the_same_rows()
    {
        var sawTemp = false;
        var existing = Directory.GetDirectories(Fx.Base, "reports_*").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var (r, output) = RunScan(sort: false, progress: new InlineProgress(_ =>
        {
            // Only THIS run's output folder (other tests' cancelled runs legitimately keep their temporary files).
            if (Directory.GetDirectories(Fx.Base, "reports_*").Where(d => !existing.Contains(d)).Any(d => Directory.GetFiles(d, "*.tmp").Length > 0)) sawTemp = true;
        }));
        Assert.False(sawTemp, "no temporary file in unsorted mode");
        Assert.False(r.Reports!.FilesSortedLargestFirst);
        var unsorted = ReportCsvReader.ReadAll(r.Reports.FilesCsv).Select(x => string.Join("|", x.Values)).Order(StringComparer.Ordinal);
        var sorted = ReportCsvReader.ReadAll(SortedRun.Value.Result.Reports!.FilesCsv).Select(x => string.Join("|", x.Values)).Order(StringComparer.Ordinal);
        Assert.SequenceEqual(sorted, unsorted, "same rows, different order");
        Assert.Equal(3, Directory.GetFiles(output).Length);
    }

    [Test]
    public static void A_full_scan_does_not_change_the_tree()
    {
        _ = SortedRun.Value;   // make sure the shared fixture has been scanned before (it settles NTFS timestamps)
        Snapshot.Settle(Fx.Root);
        var listing = Snapshot.Take(Fx.Root);
        var records = Snapshot.Take(Fx.Root, trueValues: true);
        RunScan(sort: true);
        Assert.Equal(listing, Snapshot.Take(Fx.Root), "listing view");
        Assert.Equal(records, Snapshot.Take(Fx.Root, trueValues: true), "per-item records");
    }

    [Test]
    public static void Blocked_paths_fail_before_anything_is_created()
    {
        var output = Path.Combine(Fx.Root, "InsideOut");
        var r = new InventoryScanner().Scan(new StorageScanOptions { RootPath = Fx.Root, OutputPath = output });
        Assert.Equal(ScanCompletionState.Failed, r.State);
        Assert.Equal(ScanFailureKind.InvalidPaths, r.Failure!.Kind);
        Assert.Contains("inside", r.Failure.Message);
        Assert.False(Directory.Exists(output));
        Assert.Equal(0, r.IncompleteArtifacts.Count);
    }

    [Test]
    public static void Cancellation_closes_every_file_and_lists_the_incomplete_ones()
    {
        using var cts = new CancellationTokenSource();
        var (r, output) = RunScan(sort: true, progress: new InlineProgress(p => { if (p.Phase == ScanPhase.Enumerating && p.FoldersDiscovered > 5) cts.Cancel(); }), token: cts.Token);
        Assert.Equal(ScanCompletionState.Cancelled, r.State);
        Assert.Null(r.Reports);
        Assert.True(r.IncompleteArtifacts.Count == 2, string.Join(", ", r.IncompleteArtifacts));
        foreach (var f in Directory.GetFiles(output))
        {
            using var exclusive = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.None);   // throws if still open
            Assert.True(r.IncompleteArtifacts.Contains(f), "every created file is listed as incomplete: " + f);
        }
        Assert.Equal(0, Directory.GetFiles(output, "Folders_*").Length);
    }

    [Test]
    public static void Output_folder_becoming_unwritable_mid_run_fails_as_an_output_error()
    {
        var denied = false;
        string? outputDir = null;
        try
        {
            var (r, output) = RunScan(sort: true, progress: new InlineProgress(p =>
            {
                if (denied || p.Phase != ScanPhase.Enumerating) return;
                outputDir = Directory.GetDirectories(Fx.Base, "reports_*").OrderByDescending(Directory.GetCreationTimeUtc).First();
                TestEnvironment.Run("icacls.exe", outputDir, "/deny", $"{Environment.UserName}:(WD)");
                denied = true;
            }));
            Assert.Equal(ScanCompletionState.Failed, r.State);
            Assert.Equal(ScanFailureKind.OutputError, r.Failure!.Kind);
            Assert.Contains("report file could not be written", r.Failure.Message);
            Assert.True(r.IncompleteArtifacts.Count >= 2);
            Assert.Equal(0, Directory.GetFiles(output, "Folders_*").Length);
        }
        finally
        {
            if (outputDir is not null) TestEnvironment.Run("icacls.exe", outputDir, "/remove:d", Environment.UserName);
        }
    }

    [Test]
    public static void A_report_name_taken_mid_run_is_never_overwritten()
    {
        string? decoy = null;
        var (r, _) = RunScan(sort: true, progress: new InlineProgress(p =>
        {
            if (decoy is not null || p.Phase != ScanPhase.Enumerating) return;
            var temp = Directory.GetDirectories(Fx.Base, "reports_*").SelectMany(d => Directory.GetFiles(d, "*.unsorted.tmp")).Single(f => !File.Exists(f.Replace(".unsorted.tmp", ".csv")));
            decoy = temp.Replace(Path.GetFileName(temp), "Folders_" + Path.GetFileName(temp)["Files_".Length..].Replace(".unsorted.tmp", ".csv"));
            File.WriteAllText(decoy, "DECOY - MUST NOT BE OVERWRITTEN");
        }));
        Assert.Equal(ScanCompletionState.Failed, r.State);
        Assert.Equal(ScanFailureKind.OutputError, r.Failure!.Kind);
        Assert.Equal("DECOY - MUST NOT BE OVERWRITTEN", File.ReadAllText(decoy!));
        Assert.False(r.IncompleteArtifacts.Contains(decoy!), "the decoy is not claimed as this run's file");
    }

    [Test]
    public static void Unwritable_output_folder_fails_cleanly_without_creating_files()
    {
        var output = Path.Combine(Fx.Base, "readonly_" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(output);
        TestEnvironment.Run("icacls.exe", output, "/deny", $"{Environment.UserName}:(WD)");
        try
        {
            var r = new InventoryScanner().Scan(new StorageScanOptions { RootPath = Path.Combine(Fx.Root, @"Kpop\TWICE"), OutputPath = output });
            Assert.Equal(ScanCompletionState.Failed, r.State);
            Assert.Equal(ScanFailureKind.OutputError, r.Failure!.Kind);
            Assert.Equal(0, Directory.GetFiles(output).Length);
        }
        finally { TestEnvironment.Run("icacls.exe", output, "/remove:d", Environment.UserName); }
    }

    [Test]
    public static void Progress_never_claims_a_total_while_enumerating()
    {
        var reports = new List<StorageScanProgress>();
        RunScan(sort: true, progress: new InlineProgress(reports.Add));
        Assert.True(reports.Any(p => p.Phase == ScanPhase.Enumerating));
        Assert.False(reports.Any(p => p.Phase == ScanPhase.Enumerating && p.HasKnownTotal));
        Assert.Equal(ScanPhase.Finished, reports[^1].Phase);
        Assert.Equal((long)Fx.Expected.Count, reports[^1].FilesDiscovered);
    }
}

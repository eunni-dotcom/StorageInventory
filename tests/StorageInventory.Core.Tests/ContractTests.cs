using StorageInventory.Testing;

namespace StorageInventory.Core.Tests;

public static class ContractTests
{
    private static FolderInventoryRecord Root(bool complete) => new()
    {
        Name = "root", RelativePath = ".", ParentRelativePath = "", FullPath = @"C:\root", Depth = 0,
        DirectSizeBytes = 1, TotalSizeBytes = 10, DirectFileCount = 1, TotalFileCount = 3,
        DirectSubfolderCount = 1, TotalSubfolderCount = 2, PercentOfRoot = 100,
        Attributes = FileAttributes.Directory, Status = FolderScanStatus.Ok, SubtreeComplete = complete,
    };

    private static readonly ReportSet Reports = new(@"C:\out\Files_x.csv", @"C:\out\Folders_x.csv", @"C:\out\ScanErrors_x.csv", true);
    private static readonly Dictionary<ScanErrorType, long> NoErrors = [];

    [Test]
    public static void Finished_scan_with_readable_root_is_Complete()
    {
        var r = StorageScanResult.ForFinishedScan("id", @"C:\root", @"C:\out", new ScanTotals(), NoErrors, Root(true), [], Reports, new PhaseTimings());
        Assert.Equal(ScanCompletionState.Complete, r.State);
        Assert.True(r.IsComplete);
        Assert.True(r.Finished);
        Assert.NotNull(r.Reports);
        Assert.Equal(0, r.IncompleteArtifacts.Count);
        Assert.Null(r.Failure);
    }

    [Test]
    public static void Finished_scan_with_unreadable_subtree_is_Incomplete_not_Complete()
    {
        var r = StorageScanResult.ForFinishedScan("id", @"C:\root", @"C:\out", new ScanTotals(), NoErrors, Root(false), [], Reports, new PhaseTimings());
        Assert.Equal(ScanCompletionState.Incomplete, r.State);
        Assert.False(r.IsComplete, "an incomplete scan must never report IsComplete");
        Assert.True(r.Finished, "an incomplete scan still finished and its reports are valid");
        Assert.NotNull(r.Reports);
    }

    [Test]
    public static void Cancelled_scan_is_neither_complete_nor_finished_and_lists_artifacts()
    {
        var r = StorageScanResult.ForCancelled("id", @"C:\root", @"C:\out", new ScanTotals { Files = 5 }, NoErrors, [@"C:\out\Files_x.unsorted.tmp"], new PhaseTimings());
        Assert.Equal(ScanCompletionState.Cancelled, r.State);
        Assert.False(r.IsComplete);
        Assert.False(r.Finished);
        Assert.Null(r.Reports, "a cancelled scan must not expose reports as valid");
        Assert.Null(r.Root);
        Assert.Equal(1, r.IncompleteArtifacts.Count);
        Assert.Equal(5L, r.Totals.Files);
    }

    [Test]
    public static void Failed_scan_carries_a_failure_and_no_reports()
    {
        var r = StorageScanResult.ForFailed(new ScanFailure(ScanFailureKind.OutputError, "disk full"), "id", @"C:\root", @"C:\out",
            new ScanTotals(), NoErrors, [], new PhaseTimings());
        Assert.Equal(ScanCompletionState.Failed, r.State);
        Assert.False(r.IsComplete);
        Assert.False(r.Finished);
        Assert.Null(r.Reports);
        Assert.Equal(ScanFailureKind.OutputError, Assert.NotNull(r.Failure).Kind);
    }

    [Test]
    public static void Finished_scan_requires_all_reports_and_a_root_record()
    {
        Assert.Throws<ArgumentException>(() => StorageScanResult.ForFinishedScan("id", "r", "o", new ScanTotals(), NoErrors, Root(true), [],
            Reports with { ErrorsCsv = "" }, new PhaseTimings()));
        Assert.Throws<ArgumentException>(() => StorageScanResult.ForFinishedScan("", "r", "o", new ScanTotals(), NoErrors, Root(true), [],
            Reports, new PhaseTimings()));
        Assert.Throws<ArgumentException>(() => StorageScanResult.ForFinishedScan("id", "r", "o", new ScanTotals(), NoErrors,
            Root(true) with { RelativePath = "sub" }, [], Reports, new PhaseTimings()));
    }

    [Test]
    public static void Reparse_rows_are_informational_and_errors_are_not()
    {
        Assert.True(ScanErrorType.ReparsePointSkipped.IsInformational());
        Assert.True(ScanErrorType.ReparsePointFile.IsInformational());
        foreach (var t in new[] { ScanErrorType.AccessDenied, ScanErrorType.NotFound, ScanErrorType.PathTooLong, ScanErrorType.IOError,
                                  ScanErrorType.InvalidPath, ScanErrorType.InvalidTimestamp, ScanErrorType.UnexpectedError })
        {
            Assert.False(t.IsInformational(), t.ToString());
        }
    }

    [Test]
    public static void Error_type_names_match_the_PowerShell_report_vocabulary()
    {
        var expected = new[] { "AccessDenied", "NotFound", "PathTooLong", "IOError", "InvalidPath", "InvalidTimestamp",
                               "UnexpectedError", "ReparsePointSkipped", "ReparsePointFile" };
        Assert.SequenceEqual(expected, Enum.GetNames<ScanErrorType>());
    }

    [Test]
    public static void Folder_status_text_matches_the_PowerShell_ScanStatus_column()
    {
        var root = Root(true);
        Assert.Equal("OK", root.StatusText);
        Assert.Equal("AccessDenied", (root with { Status = FolderScanStatus.Unreadable, StatusReason = ScanErrorType.AccessDenied }).StatusText);
        Assert.Equal("Partial:IOError", (root with { Status = FolderScanStatus.Partial, StatusReason = ScanErrorType.IOError }).StatusText);
        Assert.Equal("ReparsePointSkipped", (root with { Status = FolderScanStatus.ReparsePointSkipped }).StatusText);
    }

    [Test]
    public static void Options_default_to_never_following_reparse_points_and_there_is_no_other_policy()
    {
        var o = new StorageScanOptions { RootPath = "a", OutputPath = "b" };
        Assert.Equal(ReparsePointPolicy.NeverFollow, o.ReparsePoints);
        Assert.Equal(1, Enum.GetValues<ReparsePointPolicy>().Length);
    }

    [Test]
    public static void Progress_has_no_known_total_while_enumerating()
    {
        var p = new StorageScanProgress(ScanPhase.Enumerating, 10, 2, 100, 0, 0, 0, "a", TimeSpan.Zero);
        Assert.False(p.HasKnownTotal, "enumeration must never pretend to know the total");
        Assert.True((p with { Phase = ScanPhase.WritingFilesReport, PhaseItemsDone = 1, PhaseItemsTotal = 10 }).HasKnownTotal);
    }

    [Test]
    public static void Core_assembly_has_no_UI_framework_references()
    {
        var refs = typeof(StorageScanResult).Assembly.GetReferencedAssemblies().Select(a => a.Name ?? "").ToList();
        foreach (var forbidden in new[] { "PresentationFramework", "PresentationCore", "WindowsBase", "System.Windows.Forms" })
        {
            Assert.False(refs.Contains(forbidden), $"Core must not reference {forbidden}");
        }
    }
}

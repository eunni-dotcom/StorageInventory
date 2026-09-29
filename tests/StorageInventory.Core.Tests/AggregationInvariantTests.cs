using StorageInventory.Core.Scanning;
using StorageInventory.Testing;

namespace StorageInventory.Core.Tests;

/// <summary>The aggregation pass and its invariants on hand-built folder tables (no file system).</summary>
public static class AggregationInvariantTests
{
    private static FolderState F(int parent, int depth, string rel, long directBytes = 0, long directFiles = 0, long directSub = 0, long largest = -1) => new()
    {
        ParentIndex = parent, Depth = depth, Name = rel, RelativePath = rel,
        DirectSizeBytes = directBytes, DirectFileCount = directFiles, DirectSubfolderCount = directSub, LargestFileBytes = largest,
        LargestFileRelativePath = largest >= 0 ? rel + @"\big" : "",
    };

    //  .           10 bytes, 1 file
    //  A           100 bytes, 2 files
    //  A\B         1000 bytes, 1 file (largest)
    //  C           0
    private static List<FolderState> Table() =>
    [
        F(-1, 0, ".", 10, 1, 2, 10),
        F(0, 1, "A", 100, 2, 1, 60),
        F(0, 1, "C"),
        F(1, 2, @"A\B", 1000, 1, 0, 1000),
    ];

    [Test]
    public static void Totals_are_direct_plus_all_descendants_counted_once()
    {
        var t = Table();
        var summary = FolderAggregator.Aggregate(t, scannedFileCount: 4, scannedBytes: 1110);
        Assert.Equal(1110L, t[0].TotalSizeBytes);
        Assert.Equal(4L, t[0].TotalFileCount);
        Assert.Equal(3L, t[0].TotalSubfolderCount);
        Assert.Equal(1100L, t[1].TotalSizeBytes);
        Assert.Equal(3L, t[1].TotalFileCount);
        Assert.Equal(1L, t[1].TotalSubfolderCount);
        Assert.Equal(0L, t[2].TotalSizeBytes);
        Assert.Equal(1000L, t[0].LargestFileBytes);
        Assert.Equal(@"A\B\big", t[0].LargestFileRelativePath);
        Assert.True(summary.RootSubtreeComplete);
    }

    [Test]
    public static void Incompleteness_propagates_to_every_ancestor_only()
    {
        var t = Table();
        t[3].Status = FolderScanStatus.Unreadable;
        t[3].SubtreeComplete = false;
        var summary = FolderAggregator.Aggregate(t, 4, 1110);
        Assert.False(t[1].SubtreeComplete, "parent");
        Assert.False(t[0].SubtreeComplete, "root");
        Assert.True(t[2].SubtreeComplete, "unrelated sibling");
        Assert.False(summary.RootSubtreeComplete);
        Assert.Equal(1L, summary.LocallyIncompleteFolders);
        Assert.Equal(2L, summary.AffectedAncestorFolders);
    }

    [Test]
    public static void Skipped_reparse_points_never_make_anything_incomplete()
    {
        var t = Table();
        t[2].Status = FolderScanStatus.ReparsePointSkipped;
        var summary = FolderAggregator.Aggregate(t, 4, 1110);
        Assert.True(summary.RootSubtreeComplete);
        Assert.Equal(0L, summary.LocallyIncompleteFolders + summary.AffectedAncestorFolders);
    }

    [Test]
    public static void Root_totals_that_disagree_with_the_file_count_fail_loudly()
    {
        Assert.Throws<ScanConsistencyException>(() => FolderAggregator.Aggregate(Table(), 5, 1110));
        Assert.Throws<ScanConsistencyException>(() => FolderAggregator.Aggregate(Table(), 4, 1111));
    }

    [Test]
    public static void A_child_listed_before_its_parent_fails_loudly()
    {
        var t = Table();
        t[1].ParentIndex = 3;
        Assert.Throws<ScanConsistencyException>(() => FolderAggregator.Aggregate(t, 4, 1110));
    }

    [Test]
    public static void Wrong_direct_subfolder_counts_fail_loudly()
    {
        var t = Table();
        t[0].DirectSubfolderCount = 3;
        Assert.Throws<ScanConsistencyException>(() => FolderAggregator.Aggregate(t, 4, 1110));
    }

    [Test]
    public static void A_skipped_link_with_contents_fails_loudly()
    {
        var t = Table();
        t[1].Status = FolderScanStatus.ReparsePointSkipped;
        Assert.Throws<ScanConsistencyException>(() => FolderAggregator.Aggregate(t, 4, 1110));
    }

    [Test]
    public static void An_unreadable_folder_marked_complete_fails_loudly()
    {
        var t = Table();
        t[2].Status = FolderScanStatus.Unreadable;   // SubtreeComplete left true
        Assert.Throws<ScanConsistencyException>(() => FolderAggregator.Aggregate(t, 4, 1110));
    }

    [Test]
    public static void Aggregating_twice_fails_loudly()
    {
        var t = Table();
        FolderAggregator.Aggregate(t, 4, 1110);
        Assert.Throws<ScanConsistencyException>(() => FolderAggregator.Aggregate(t, 4, 1110));
    }

    [Test]
    public static void Records_carry_paths_and_percentages()
    {
        var t = Table();
        FolderAggregator.Aggregate(t, 4, 1110);
        var root = FolderAggregator.ToRecord(t, 0, @"D:\Media");
        var b = FolderAggregator.ToRecord(t, 3, @"D:\Media");
        var c = FolderAggregator.ToRecord(t, 2, @"D:\");
        Assert.Equal(@"D:\Media", root.FullPath);
        Assert.Equal("", root.ParentRelativePath);
        Assert.Equal(100.0, root.PercentOfRoot);
        Assert.Null(root.PercentOfParent);
        Assert.Equal(@"D:\Media\A\B", b.FullPath);
        Assert.Equal("A", b.ParentRelativePath);
        Assert.Equal(1000 * 100.0 / 1110, b.PercentOfRoot);
        Assert.Equal(1000 * 100.0 / 1100, b.PercentOfParent);
        Assert.Equal(@"D:\C", c.FullPath);
        Assert.Null(c.LargestFileBytes);
    }

    [Test]
    public static void Empty_root_gives_zero_percentages_not_division_errors()
    {
        var t = new List<FolderState> { F(-1, 0, "."), F(0, 1, "E") };
        t[0].DirectSubfolderCount = 1;
        FolderAggregator.Aggregate(t, 0, 0);
        Assert.Equal(0.0, FolderAggregator.ToRecord(t, 0, @"C:\x").PercentOfRoot);
        Assert.Equal(0.0, FolderAggregator.ToRecord(t, 1, @"C:\x").PercentOfParent);
    }
}

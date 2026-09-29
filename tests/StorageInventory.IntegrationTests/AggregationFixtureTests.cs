using StorageInventory.Core;
using StorageInventory.Core.Scanning;
using StorageInventory.Testing;

namespace StorageInventory.IntegrationTests;

/// <summary>
/// Aggregation on the Phase A fixture, checked by INDEPENDENT brute force: totals come from the fixture's ground-truth
/// file list and an independent folder walk, never from the engine's own numbers.
/// </summary>
public static class AggregationFixtureTests
{
    private static PhaseAFixture Fx => PhaseAFixture.Shared;

    // Folders the fixture makes unreadable (deny-ACL). Everything else is readable.
    private static readonly string[] Unreadable = ["Denied", @"Kpop\aespa\Private"];

    private static (List<FolderInventoryRecord> Records, CompletenessSummary Summary) ScanAndAggregate()
    {
        var engine = new ScanEngine(Fx.Root, new CollectingSink(), new HashSet<string>());
        engine.Run(CancellationToken.None);
        var summary = FolderAggregator.Aggregate(engine.Folders, engine.FileCount, engine.TotalBytes);
        var records = Enumerable.Range(0, engine.Folders.Count).Select(i => FolderAggregator.ToRecord(engine.Folders, i, Fx.Root)).ToList();
        return (records, summary);
    }

    private static bool IsAtOrBelow(string rel, string folder) =>
        folder == "." || rel == folder || rel.StartsWith(folder + "\\", StringComparison.Ordinal);

    /// <summary>Independent walk: every folder (reparse points listed, never entered) and its parent.</summary>
    private static Dictionary<string, string?> IndependentFolders()
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal) { ["."] = null };
        var stack = new Stack<(string Full, string Rel)>();
        stack.Push((Fx.Root, "."));
        while (stack.Count > 0)
        {
            var (full, rel) = stack.Pop();
            DirectoryInfo[] dirs;
            try { dirs = new DirectoryInfo(full).GetDirectories(); } catch (UnauthorizedAccessException) { continue; }
            foreach (var d in dirs)
            {
                var childRel = rel == "." ? d.Name : rel + "\\" + d.Name;
                result[childRel] = rel;
                if (!d.Attributes.HasFlag(FileAttributes.ReparsePoint)) stack.Push((d.FullName, childRel));
            }
        }
        return result;
    }

    [Test]
    public static void Every_folder_total_matches_brute_force_recomputation()
    {
        var (records, _) = ScanAndAggregate();
        var folders = IndependentFolders();
        Assert.Equal(folders.Count, records.Count, "folder count");
        foreach (var r in records)
        {
            var rel = r.RelativePath;
            var files = Fx.Expected.Where(e => IsAtOrBelow(e.RelativePath, rel)).ToList();
            var direct = Fx.Expected.Where(e => (Path.GetDirectoryName(e.RelativePath) is { Length: > 0 } d ? d : ".") == rel).ToList();
            var subfolders = folders.Keys.Where(k => k != "." && k != rel && IsAtOrBelow(k, rel)).ToList();
            var directSubfolders = folders.Where(kv => kv.Value == rel).ToList();

            Assert.Equal(files.Sum(x => x.Size), r.TotalSizeBytes, rel + " TotalSizeBytes");
            Assert.Equal((long)files.Count, r.TotalFileCount, rel + " TotalFileCount");
            Assert.Equal(direct.Sum(x => x.Size), r.DirectSizeBytes, rel + " DirectSizeBytes");
            Assert.Equal((long)direct.Count, r.DirectFileCount, rel + " DirectFileCount");
            Assert.Equal((long)subfolders.Count, r.TotalSubfolderCount, rel + " TotalSubfolderCount");
            Assert.Equal((long)directSubfolders.Count, r.DirectSubfolderCount, rel + " DirectSubfolderCount");
            Assert.Equal(files.Count == 0 ? null : files.Max(x => x.Size), r.LargestFileBytes, rel + " LargestFileBytes");
            if (files.Count > 0)
            {
                Assert.True(files.Any(x => x.RelativePath == r.LargestFileRelativePath && x.Size == r.LargestFileBytes),
                    rel + $": largest file path '{r.LargestFileRelativePath}' is not a file of that size in the subtree");
            }
        }
    }

    [Test]
    public static void Percentages_match_brute_force_recomputation()
    {
        var (records, _) = ScanAndAggregate();
        var byRel = records.ToDictionary(r => r.RelativePath, StringComparer.Ordinal);
        var rootTotal = Fx.Expected.Sum(e => e.Size);
        foreach (var r in records)
        {
            Assert.True(Math.Abs(r.PercentOfRoot - r.TotalSizeBytes * 100.0 / rootTotal) < 1e-9, r.RelativePath + " PercentOfRoot");
            if (r.RelativePath == ".") { Assert.Null(r.PercentOfParent); continue; }
            var parent = byRel[r.ParentRelativePath];
            var expected = parent.TotalSizeBytes > 0 ? r.TotalSizeBytes * 100.0 / parent.TotalSizeBytes : 0;
            Assert.True(Math.Abs(r.PercentOfParent!.Value - expected) < 1e-9, r.RelativePath + " PercentOfParent");
        }
    }

    [Test]
    public static void Subtree_completeness_matches_the_known_unreadable_folders()
    {
        var (records, summary) = ScanAndAggregate();
        foreach (var r in records)
        {
            var containsUnreadable = Unreadable.Any(u => IsAtOrBelow(u, r.RelativePath));
            Assert.Equal(!containsUnreadable, r.SubtreeComplete, r.RelativePath + " SubtreeComplete");
            var locallyUnreadable = Unreadable.Contains(r.RelativePath);
            Assert.Equal(locallyUnreadable ? FolderScanStatus.Unreadable : (r.Status == FolderScanStatus.ReparsePointSkipped ? FolderScanStatus.ReparsePointSkipped : FolderScanStatus.Ok),
                r.Status, r.RelativePath + " Status");
        }
        Assert.False(summary.RootSubtreeComplete);
        Assert.Equal(2L, summary.LocallyIncompleteFolders);
        Assert.Equal(3L, summary.AffectedAncestorFolders, "root, Kpop and Kpop\\aespa");
    }

    [Test]
    public static void Full_paths_and_parents_are_consistent()
    {
        var (records, _) = ScanAndAggregate();
        foreach (var r in records)
        {
            Assert.True(Directory.Exists(r.FullPath) || r.Status == FolderScanStatus.ReparsePointSkipped, r.FullPath);
            if (r.RelativePath != ".") Assert.Equal(Path.Combine(Fx.Root, r.RelativePath), r.FullPath);
        }
    }
}

using StorageInventory.Core;
using StorageInventory.Core.Scanning;
using StorageInventory.Testing;

namespace StorageInventory.IntegrationTests;

/// <summary>A test sink that keeps everything (fine for small fixtures; the product never does this). The engine calls
/// only OnFile and OnError; the lifecycle callbacks of the observer contract are not used here.</summary>
internal sealed class CollectingSink : IScanObserver
{
    public List<(FileInventoryRecord File, int FolderIndex)> Files { get; } = [];
    public List<ScanErrorRecord> Errors { get; } = [];
    public Action<FileInventoryRecord>? OnFileHook { get; init; }

    public void OnFile(in FileInventoryRecord file, int folderIndex)
    {
        Files.Add((file, folderIndex));
        OnFileHook?.Invoke(file);
    }

    public void OnError(ScanErrorRecord error) => Errors.Add(error);

    public void OnScanStarted(in ScanStartInfo start) { }
    public void OnFolderFinalised(int index, int parentIndex, FolderInventoryRecord folder) { }
    public void OnScanEnded(in ScanEndInfo end) { }
}

public static class ScanEngineTests
{
    private static readonly HashSet<string> NoOwnReports = [];
    private static PhaseAFixture Fx => PhaseAFixture.Shared;

    private static (ScanEngine Engine, CollectingSink Sink) Scan(string root, CollectingSink? sink = null, IReadOnlySet<string>? own = null, CancellationToken token = default)
    {
        sink ??= new CollectingSink();
        var engine = new ScanEngine(root, sink, own ?? NoOwnReports);
        engine.Run(token);
        return (engine, sink);
    }

    private static string DirOf(string relativePath)
    {
        var dir = Path.GetDirectoryName(relativePath);
        return string.IsNullOrEmpty(dir) ? "." : dir;
    }

    [Test]
    public static void Observed_files_match_ground_truth_exactly()
    {
        var (engine, sink) = Scan(Fx.Root);
        var got = sink.Files.Select(f => $"{f.File.RelativePath}|{f.File.SizeBytes}").Order(StringComparer.Ordinal).ToList();
        var expected = Fx.Expected.Select(e => $"{e.RelativePath}|{e.Size}").Order(StringComparer.Ordinal).ToList();
        Assert.SequenceEqual(expected, got, "files");
        Assert.Equal((long)expected.Count, engine.FileCount);
        Assert.Equal(Fx.Expected.Sum(e => e.Size), engine.TotalBytes);
        foreach (var (file, folderIndex) in sink.Files)
        {
            Assert.True(File.Exists(file.FullPath), $"FullPath must exist: {file.FullPath}");
            Assert.Equal(DirOf(file.RelativePath), file.RelativeDirectory, file.RelativePath);
            Assert.Equal(file.RelativeDirectory, engine.Folders[folderIndex].RelativePath, "folder index");
            Assert.Equal(Path.Combine(Fx.Root, file.RelativePath), file.FullPath);
        }
    }

    [Test]
    public static void Folder_table_matches_an_independent_non_following_walk()
    {
        var expected = new List<string> { "." };
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
                expected.Add(childRel);
                if (!d.Attributes.HasFlag(FileAttributes.ReparsePoint)) stack.Push((d.FullName, childRel));
            }
        }
        var (engine, _) = Scan(Fx.Root);
        Assert.SequenceEqual(expected.Order(StringComparer.Ordinal), engine.Folders.Select(f => f.RelativePath).Order(StringComparer.Ordinal), "folders");
    }

    [Test]
    public static void Parents_precede_children_and_depth_and_paths_are_consistent()
    {
        var (engine, _) = Scan(Fx.Root);
        Assert.Equal(-1, engine.Folders[0].ParentIndex);
        Assert.Equal(".", engine.Folders[0].RelativePath);
        for (var i = 1; i < engine.Folders.Count; i++)
        {
            var f = engine.Folders[i];
            Assert.True(f.ParentIndex >= 0 && f.ParentIndex < i, $"{f.RelativePath}: parent index {f.ParentIndex} must be < {i}");
            var parent = engine.Folders[f.ParentIndex];
            Assert.Equal(parent.Depth + 1, f.Depth, f.RelativePath);
            Assert.Equal(parent.RelativePath == "." ? f.Name : parent.RelativePath + "\\" + f.Name, f.RelativePath);
            Assert.True(f.PendingFullPath is null, "pending paths are released after listing");
        }
    }

    [Test]
    public static void Direct_values_match_independent_recomputation_from_ground_truth()
    {
        var (engine, _) = Scan(Fx.Root);
        var byDir = Fx.Expected.GroupBy(e => DirOf(e.RelativePath)).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        foreach (var f in engine.Folders)
        {
            var files = byDir.GetValueOrDefault(f.RelativePath) ?? [];
            Assert.Equal(files.Sum(x => x.Size), f.DirectSizeBytes, f.RelativePath + " DirectSizeBytes");
            Assert.Equal((long)files.Count, f.DirectFileCount, f.RelativePath + " DirectFileCount");
            var children = engine.Folders.Count(c => c.ParentIndex >= 0 && engine.Folders[c.ParentIndex] == f);
            Assert.Equal((long)children, f.DirectSubfolderCount, f.RelativePath + " DirectSubfolderCount");
            var largest = files.Count == 0 ? -1 : files.Max(x => x.Size);
            Assert.Equal(largest, f.LargestFileBytes, f.RelativePath + " LargestFileBytes");
        }
    }

    [Test]
    public static void Unreadable_folders_are_marked_and_reported_but_the_scan_continues()
    {
        var (engine, sink) = Scan(Fx.Root);
        foreach (var rel in new[] { "Denied", @"Kpop\aespa\Private" })
        {
            var f = engine.Folders.Single(x => x.RelativePath == rel);
            Assert.Equal(FolderScanStatus.Unreadable, f.Status, rel);
            Assert.Equal(ScanErrorType.AccessDenied, f.StatusReason, rel);
            Assert.False(f.SubtreeComplete, rel);
            Assert.True(sink.Errors.Any(e => e.Type == ScanErrorType.AccessDenied && e.Path == Path.Combine(Fx.Root, rel)), rel + " error row");
        }
        Assert.True(engine.Folders.Single(x => x.RelativePath == @"Kpop\TWICE").Status == FolderScanStatus.Ok);
    }

    [Test]
    public static void Junctions_are_listed_as_skipped_and_never_entered()
    {
        var (engine, sink) = Scan(Fx.Root);
        foreach (var rel in new[] { "Loop", @"Kpop\LinkOut" })
        {
            var f = engine.Folders.Single(x => x.RelativePath == rel);
            Assert.Equal(FolderScanStatus.ReparsePointSkipped, f.Status, rel);
            Assert.True(f.SubtreeComplete, "a skipped link never makes a subtree incomplete");
            Assert.Equal(0L, f.DirectFileCount + f.DirectSubfolderCount, rel + " must not be entered");
            var row = sink.Errors.Single(e => e.Path == Path.Combine(Fx.Root, rel));
            Assert.Equal(ScanErrorType.ReparsePointSkipped, row.Type);
            Assert.Contains("Not followed: Link -> ", row.Message);
        }
        Assert.False(sink.Files.Any(f => f.File.FileName == "big-outside.bin"), "the file behind the outward junction must not be counted");
        Assert.False(engine.Folders.Any(f => f.RelativePath.StartsWith(@"Loop\", StringComparison.Ordinal)), "the loop must not be entered");
        Assert.Equal(2L + (Fx.SymlinksCreated ? 1 : 0), engine.ReparsePointsSkipped);
    }

    [Test]
    public static void Invalid_timestamp_is_reported_and_the_file_is_still_counted()
    {
        if (Fx.Features["InvalidTimestamp"] != "created") Assert.Skip("could not stamp an out-of-range FILETIME here");
        var (engine, sink) = Scan(Fx.Root);
        var file = sink.Files.Single(f => f.File.FileName == "bad-time.dat").File;
        Assert.Equal(321L, file.SizeBytes);
        Assert.True(file.ModifiedUtc is null, "an unrepresentable timestamp is null, not a made-up date");
        Assert.True(file.CreatedUtc is not null);
        var row = sink.Errors.Single(e => e.Type == ScanErrorType.InvalidTimestamp);
        Assert.Contains("Modified time: ", row.Message);
        Assert.Equal(FolderScanStatus.Ok, engine.Folders.Single(f => f.RelativePath == "Odd").Status);
        Assert.Equal(3L, engine.ScanErrorCount, "2 access denied + 1 invalid timestamp");
    }

    [Test]
    public static void Timestamps_are_UTC_and_extensions_follow_the_reference_rule()
    {
        var (_, sink) = Scan(Path.Combine(Fx.Root, @"Kpop\TWICE"));
        var file = sink.Files.Single().File;
        Assert.Equal(DateTimeKind.Utc, file.ModifiedUtc!.Value.Kind);
        Assert.Equal(new FileInfo(file.FullPath).LastWriteTimeUtc, file.ModifiedUtc.Value);
        Assert.Equal(".flac", ScanEngine.ExtensionOf("song.flac"));
        Assert.Equal(".gitignore", ScanEngine.ExtensionOf(".gitignore"));
        Assert.Equal("", ScanEngine.ExtensionOf("NoExtFile"));
        Assert.Equal("", ScanEngine.ExtensionOf("trailing."));
        Assert.Equal(".001", ScanEngine.ExtensionOf("archive.7z.001"));
    }

    [Test]
    public static void Long_path_files_beyond_260_characters_are_counted()
    {
        var (_, sink) = Scan(Fx.Root);
        var longFile = sink.Files.Single(f => f.File.FileName == "longfile.dat").File;
        Assert.True(longFile.FullPath.Length > 260, $"only {longFile.FullPath.Length} characters");
        Assert.Equal(777L, longFile.SizeBytes);
    }

    [Test]
    public static void Scanning_does_not_change_the_tree()
    {
        Snapshot.Settle(Fx.Root);
        var listing = Snapshot.Take(Fx.Root);
        var records = Snapshot.Take(Fx.Root, trueValues: true);
        Scan(Fx.Root);
        Assert.Equal(listing, Snapshot.Take(Fx.Root), "listing view");
        Assert.Equal(records, Snapshot.Take(Fx.Root, trueValues: true), "per-item records");
    }

    [Test]
    public static void Own_report_file_inside_the_tree_trips_the_tripwire()
    {
        var root = TestEnvironment.NewWorkFolder("tripwire");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "sub"));
            File.WriteAllText(Path.Combine(root, "sub", "Files_20990101_000000_abcdef.csv"), "x");
            var ex = Assert.Throws<OutputInsideScannedTreeException>(() =>
                Scan(root, own: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "FILES_20990101_000000_ABCDEF.CSV" }));
            Assert.Contains("sub", ex.FoundPath);
        }
        finally { TestEnvironment.RemoveTree(root); }
    }

    [Test]
    public static void Sink_failures_abort_the_scan_instead_of_becoming_scan_errors()
    {
        var sink = new CollectingSink { OnFileHook = _ => throw new IOException("simulated disk full") };
        var ex = Assert.Throws<IOException>(() => Scan(Path.Combine(Fx.Root, "Kpop"), sink));
        Assert.Equal("simulated disk full", ex.Message);
        Assert.Equal(0, sink.Errors.Count);
    }

    [Test]
    public static void Cancellation_stops_promptly_and_is_not_swallowed()
    {
        var root = TestEnvironment.NewWorkFolder("cancel");
        try
        {
            for (var i = 0; i < 2000; i++) File.WriteAllBytes(Path.Combine(root, $"f{i:D4}.bin"), []);
            using var cts = new CancellationTokenSource();
            var seenAfterCancel = 0;
            var sink = new CollectingSink { OnFileHook = _ => { if (cts.IsCancellationRequested) seenAfterCancel++; else if (_ is { FileName: "f0010.bin" }) cts.Cancel(); } };
            Assert.Throws<OperationCanceledException>(() => Scan(root, sink, token: cts.Token));
            Assert.True(seenAfterCancel <= 256, $"{seenAfterCancel} files processed after cancellation");

            using var already = new CancellationTokenSource();
            already.Cancel();
            var sink2 = new CollectingSink();
            Assert.Throws<OperationCanceledException>(() => Scan(root, sink2, token: already.Token));
            Assert.Equal(0, sink2.Files.Count);
        }
        finally { TestEnvironment.RemoveTree(root); }
    }

    [Test]
    public static void Very_deep_trees_are_walked_without_recursion()
    {
        var root = TestEnvironment.NewWorkFolder("deep");
        try
        {
            var deepest = Path.Combine([root, .. Enumerable.Repeat("d", 400)]);
            Directory.CreateDirectory(deepest);
            File.WriteAllBytes(Path.Combine(deepest, "bottom.bin"), new byte[42]);
            var (engine, sink) = Scan(root);
            Assert.Equal(401, engine.Folders.Count);
            Assert.Equal(400, engine.Folders.Max(f => f.Depth));
            Assert.Equal(42L, sink.Files.Single().File.SizeBytes);
        }
        finally { TestEnvironment.RemoveTree(root); }
    }

    [Test]
    public static void A_folder_swapped_for_a_junction_after_listing_is_not_followed()
    {
        // Improvement over the reference: the folder is re-checked right before it is entered.
        var root = TestEnvironment.NewWorkFolder("swap");
        var outside = TestEnvironment.NewWorkFolder("swap-outside");
        try
        {
            var swap = Path.Combine(root, "Swap");
            Directory.CreateDirectory(swap);
            File.WriteAllBytes(Path.Combine(swap, "inside.bin"), new byte[5]);
            File.WriteAllBytes(Path.Combine(outside, "outside-secret.bin"), new byte[999]);
            File.WriteAllBytes(Path.Combine(root, "z.txt"), new byte[1]);   // listed after "Swap"
            var swapped = false;
            var sink = new CollectingSink
            {
                OnFileHook = f =>
                {
                    if (f.FileName != "z.txt" || swapped) return;
                    Directory.Delete(swap, recursive: true);                 // test's own folder
                    swapped = TestEnvironment.CreateJunction(swap, outside);
                },
            };
            var (engine, _) = Scan(root, sink);
            Assert.True(swapped, "test could not create the junction");
            var folder = engine.Folders.Single(f => f.RelativePath == "Swap");
            Assert.Equal(FolderScanStatus.ReparsePointSkipped, folder.Status);
            Assert.False(sink.Files.Any(f => f.File.FileName == "outside-secret.bin"), "the junction target must not be scanned");
            Assert.Contains("Became a reparse point after it was listed", sink.Errors.Single(e => e.Type == ScanErrorType.ReparsePointSkipped).Message);
        }
        finally
        {
            TestEnvironment.RemoveTree(root);
            TestEnvironment.RemoveTree(outside);
        }
    }

    [Test]
    public static void Real_file_reparse_points_are_counted_with_listed_size_and_reported()
    {
        var aliases = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps");
        if (!Directory.Exists(aliases)) Assert.Skip("no WindowsApps folder");
        var independent = new List<FileInfo>();
        var stack = new Stack<DirectoryInfo>();
        stack.Push(new DirectoryInfo(aliases));
        while (stack.Count > 0)
        {
            FileSystemInfo[] items;
            try { items = stack.Pop().GetFileSystemInfos(); } catch (UnauthorizedAccessException) { continue; }
            foreach (var i in items)
            {
                if (i is FileInfo f && f.Attributes.HasFlag(FileAttributes.ReparsePoint)) independent.Add(f);
                else if (i is DirectoryInfo d && !d.Attributes.HasFlag(FileAttributes.ReparsePoint)) stack.Push(d);
            }
        }
        if (independent.Count == 0) Assert.Skip("no App Execution Alias reparse files here");
        var (engine, sink) = Scan(aliases);
        Assert.Equal((long)independent.Count, engine.FileReparsePoints);
        Assert.Equal(independent.Count, sink.Errors.Count(e => e.Type == ScanErrorType.ReparsePointFile));
        foreach (var real in independent)
        {
            var seen = sink.Files.Single(f => f.File.FullPath == real.FullName).File;
            Assert.Equal(real.Length, seen.SizeBytes, real.Name);
        }
    }
}

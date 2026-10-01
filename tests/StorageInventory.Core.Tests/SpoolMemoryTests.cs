using StorageInventory.Core.Scanning;
using StorageInventory.Core.Spool;
using StorageInventory.Testing;

namespace StorageInventory.Core.Tests;

/// <summary>
/// Bounded memory (SPOOL-21, INV-13): neither the spool writer nor the pass V keeps anything per file. Each test runs the
/// same shape at two file counts, ten times apart, with the folder count fixed, and requires the memory to stay the
/// same: an accidental per-file list would add at least 8 bytes per file (14 MB at 1.8M extra files).
/// </summary>
public static class SpoolMemoryTests
{
    private const int Folders = 1000;

    /// <summary>A write-only stream that keeps nothing (only counts), so the test measures the writer alone.</summary>
    private sealed class DiscardingStream : Stream
    {
        public long Written { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Written += count;
        public override void Write(ReadOnlySpan<byte> buffer) => Written += buffer.Length;
    }

    private static readonly FileInventoryRecord Track = new("track07 - a typical file name.mp3", ".mp3", "x", "x", @"C:\x", 4_000_000,
        new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc), null, FileAttributes.Archive);

    private static long RetainedByWriter(int files)
    {
        var stream = new DiscardingStream();
        using var writer = new SpoolWriter(stream, SpoolTestKit.Token);
        writer.OnScanStarted(new ScanStartInfo(ObservationScript.RunId, @"C:\x", null, new HashSet<string>()));
        var perFolder = files / Folders;
        var before = GC.GetTotalMemory(forceFullCollection: true);
        for (var i = 0; i < files; i++) writer.OnFile(Track, i / perFolder);
        var after = GC.GetTotalMemory(forceFullCollection: true);
        Assert.Equal(SpoolWriterState.Writing, writer.State);
        Assert.True(stream.Written > 0, "the writer streamed its records out");
        GC.KeepAlive(writer);
        return after - before;
    }

    [Test]
    public static void The_spool_writer_keeps_nothing_per_file()
    {
        var small = RetainedByWriter(200_000);
        var large = RetainedByWriter(2_000_000);
        Console.WriteLine($"      writer retained: {small:N0} B at 200k files, {large:N0} B at 2M files ({Folders} folders)");
        Assert.True(large - small < 512 * 1024, $"retained memory grew by {large - small:N0} bytes for 1.8M more files");
        Assert.True(large < 2 * 1024 * 1024, $"the writer retained {large:N0} bytes beyond its 1 MiB buffer");
    }

    private static long AllocatedByVerify(int files, out long spoolBytes)
    {
        var path = Path.Combine(Path.GetTempPath(), "si_spool_mem_" + Guid.NewGuid().ToString("N") + ".tmp");
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
        var perFolder = files / Folders;
        using (var writer = new SpoolWriter(file, SpoolTestKit.Token))
        {
            writer.OnScanStarted(new ScanStartInfo(ObservationScript.RunId, @"C:\x", null, new HashSet<string>()));
            for (var i = 0; i < files; i++) writer.OnFile(Track, i / perFolder);
            writer.OnFolderFinalised(0, -1, Folder(Folders - 1, Folders - 1, files, perFolder));
            for (var k = 1; k < Folders; k++) writer.OnFolderFinalised(k, 0, Folder(0, 0, perFolder, perFolder));
            writer.OnScanEnded(ScanEndInfo.Finished(new ScanTotals { Files = files, Folders = Folders, Bytes = (long)files * Track.SizeBytes }));
            Assert.Equal(SpoolWriterState.Sealed, writer.State);
        }
        spoolBytes = file.Length;
        var before = GC.GetAllocatedBytesForCurrentThread();
        var v = SpoolReader.Verify(file, SpoolTestKit.Token, null);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(v.Passed, $"{v.Defect} {v.Detail}");
        return allocated;
    }

    private static FolderInventoryRecord Folder(long directSub, long totalSub, long totalFiles, long directFiles) => new()
    {
        Name = "folder", RelativePath = "f", ParentRelativePath = "", FullPath = @"C:\x", Depth = 0,
        DirectSizeBytes = directFiles * Track.SizeBytes, TotalSizeBytes = totalFiles * Track.SizeBytes,
        DirectFileCount = directFiles, TotalFileCount = totalFiles, DirectSubfolderCount = directSub, TotalSubfolderCount = totalSub,
        LargestFileBytes = Track.SizeBytes, PercentOfRoot = 0, Attributes = FileAttributes.Directory, Status = FolderScanStatus.Ok, SubtreeComplete = true,
    };

    [Test]
    public static void The_verification_pass_allocates_nothing_per_file()
    {
        var small = AllocatedByVerify(100_000, out var smallBytes);
        var large = AllocatedByVerify(1_000_000, out var largeBytes);
        Console.WriteLine($"      V allocated: {small:N0} B for a {smallBytes:N0}-byte spool, {large:N0} B for a {largeBytes:N0}-byte spool ({Folders} folders)");
        Assert.True(large - small < 256 * 1024, $"V allocated {large - small:N0} more bytes for 900k more files");
    }
}

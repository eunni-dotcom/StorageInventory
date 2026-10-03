using StorageInventory.Core;
using StorageInventory.Core.Identity;
using StorageInventory.History.Identity;
using StorageInventory.History.Library;

namespace StorageInventory.Library.Tests;

/// <summary>
/// A snapshot whose ONE folder (the root) holds <c>names</c> files, the shape that makes the name interning of a folder a long
/// uninterrupted stretch (C4R-M01: a mail spool, a thumbnail cache, a photo dump); with <c>folders</c> above 1 it is a root and that many
/// minus one children, EACH holding <c>names</c> files (many small folders, to show that a folder boundary resets nothing). The file lists count every element the
/// importer reads (<see cref="Reads"/>), so a test can say how far the importer had got when a check or a cancellation happened, with no
/// reliance on elapsed time, and it can advance a fake clock by a fixed step per element read (<see cref="Clock"/>), which makes the
/// time bound of CAN-03 deterministic. All names are exact UTF-16 and distinct; a second snapshot with the same <c>stem</c> holds the
/// SAME names, so that its interning meets a source whose dictionary already has them (look-ups, no inserts).
/// </summary>
internal sealed class BigFolderSnapshot : ISnapshotRowSource
{
    private readonly int _names;
    private readonly int _folders;
    private readonly string _stem;
    private readonly long _stepTicks;
    private readonly Action<long>? _onRead;
    private long _reads;
    private long _clock = 1_000_000;

    /// <param name="names">Files in each folder.</param>
    /// <param name="stem">The names' common prefix: two snapshots with one stem hold the same names.</param>
    /// <param name="stepTicks">How far <see cref="Clock"/> advances per element the importer reads (0: the clock stands still).</param>
    /// <param name="onRead">Called with the running count of elements read, before the element is returned.</param>
    /// <param name="folders">Folders: the root and <c>folders - 1</c> children of the root.</param>
    internal BigFolderSnapshot(int names, string stem = "n", long stepTicks = 0, Action<long>? onRead = null, int folders = 1)
    {
        _names = names;
        _folders = folders;
        _stem = stem;
        _stepTicks = stepTicks;
        _onRead = onRead;
    }

    /// <summary>Elements of the folder's file list read so far (the interning loop reads each name once, then the insertion loop reads
    /// each file once, so while the count is at most the folder's size the importer is still interning).</summary>
    internal long Reads => Volatile.Read(ref _reads);

    /// <summary>The fake clock, in <see cref="System.Diagnostics.Stopwatch"/> ticks: it advances <c>stepTicks</c> per element read.</summary>
    internal long Clock() => Volatile.Read(ref _clock);

    internal long Files => (long)_names * _folders;

    public int FolderCount => _folders;

    private string NameOf(int folder, int k) => $"{_stem}{folder:D4}_{k:D7}.dat";

    private long SizeOf(int k) => 1 + k % 1000;

    internal ImportSnapshotHeader Header(string runId) => new()
    {
        Completion = ScanCompletionState.Complete,
        RunId = runId,
        StartedUtcTicks = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks,
        FinishedUtcTicks = new DateTime(2026, 1, 1, 0, 1, 0, DateTimeKind.Utc).Ticks,
        RootPathAsEntered = Utf16.ToBytes(@"D:\Big"),
        CanonicalRoot = Utf16.ToBytes(@"D:\Big"),
        MountPoint = Utf16.ToBytes(@"D:\"),
        VolumeLabel = Utf16.ToBytes("Data"),
        CapacityBytes = 1_000_000_000,
        FreeBytes = 500_000_000,
        Confidence = IdentityConfidence.Strong,
        Basis = IdentityBasis.Evidence,
        CaptureKind = SourceKind.LocalVolume,
        FsName = "NTFS",
        Files = Files,
        Folders = _folders,
        Bytes = DirectBytes() * _folders,
    };

    private long DirectBytes()
    {
        long bytes = 0;
        for (var k = 0; k < _names; k++) bytes += SizeOf(k);
        return bytes;
    }

    public IEnumerable<ImportFolder> Folders()
    {
        var direct = DirectBytes();
        long? largest = _names == 0 ? null : Math.Min(1000, _names);
        yield return new ImportFolder(0, -1, [], FolderScanStatus.Ok, null, true, 0x10, 637_000_000_000_000_000L, 637_100_000_000_000_000L,
            direct, direct * _folders, _names, Files, _folders - 1, _folders - 1, _folders * _names == 0 ? null : largest);
        for (var i = 1; i < _folders; i++)
        {
            yield return new ImportFolder(i, 0, Utf16.ToBytes("d" + i), FolderScanStatus.Ok, null, true, 0x10, 637_000_000_000_000_000L + i, 637_100_000_000_000_000L + i,
                direct, direct, _names, _names, 0, 0, largest);
        }
    }

    public IReadOnlyList<ImportFile> FilesOfFolder(int folderIndex) => new CountingList(this, folderIndex);

    public IEnumerable<ImportError> Errors() => [];

    private void Touch()
    {
        var reads = Interlocked.Increment(ref _reads);
        Interlocked.Add(ref _clock, _stepTicks);
        _onRead?.Invoke(reads);
    }

    private sealed class CountingList(BigFolderSnapshot owner, int folder) : IReadOnlyList<ImportFile>
    {
        public int Count => owner._names;

        public ImportFile this[int index]
        {
            get
            {
                owner.Touch();
                return new ImportFile(Utf16.ToBytes(owner.NameOf(folder, index)), (long)folder * owner._names + index, owner.SizeOf(index), 637_100_000_000_000_000L + index, 637_000_000_000_000_000L, 637_200_000_000_000_000L, 0x20);
            }
        }

        public IEnumerator<ImportFile> GetEnumerator()
        {
            for (var i = 0; i < Count; i++) yield return this[i];
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

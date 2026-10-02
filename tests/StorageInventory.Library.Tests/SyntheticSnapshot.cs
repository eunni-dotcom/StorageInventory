using StorageInventory.Core;
using StorageInventory.Core.Identity;
using StorageInventory.History.Identity;
using StorageInventory.History.Library;

namespace StorageInventory.Library.Tests;

/// <summary>
/// A deterministic synthetic snapshot as an abstract row stream (not C5's spool): a tree of <c>folders</c> folders, each with
/// a few files (about 4.3 per folder on average, the reference drive's ratio, so 2,009,918 files lie in 467,481 folders), whose
/// totals are computed here so that the snapshot satisfies every §9.5 invariant. Names are exact UTF-16 code units; folder names
/// repeat across snapshots (so the dictionary is shared, as in a real Library) and file names partly repeat, partly do not.
/// <see cref="Mutation"/> injects one precise defect for the failure tests.
/// </summary>
internal sealed class SyntheticSnapshot : ISnapshotRowSource
{
    private readonly int _folders;
    private readonly int _seed;
    private readonly int _branching;
    private readonly int[] _directFiles;
    private readonly long[] _directBytes;
    private readonly long[] _totalFiles;
    private readonly long[] _totalBytes;
    private readonly long[] _directSubfolders;
    private readonly long[] _totalSubfolders;
    private readonly long[] _seqStart;
    private readonly string _label;

    /// <summary>A defect to plant, so that verification and rollback can be shown to catch it.</summary>
    internal enum Mutation { None, WrongFileSize, DuplicateSeq, MissingFolderRow, WrongSealedFileCount, ThrowAfterFolders }

    internal SyntheticSnapshot(int folders, int seed = 1, int branching = 6, string label = "s", Mutation mutation = Mutation.None, int averageFilesTimesTen = 43)
    {
        _folders = folders;
        _seed = seed;
        _branching = branching;
        _label = label;
        MutationKind_ = mutation;
        _directFiles = new int[folders];
        _directBytes = new long[folders];
        _totalFiles = new long[folders];
        _totalBytes = new long[folders];
        _directSubfolders = new long[folders];
        _totalSubfolders = new long[folders];
        _seqStart = new long[folders];
        long seq = 0;
        for (var i = 0; i < folders; i++)
        {
            // 0..8 files, mean about averageFilesTimesTen/10
            var count = (int)((uint)Hash(i, 17) % 9u);
            if (averageFilesTimesTen != 43) count = (int)((uint)Hash(i, 17) % (uint)(averageFilesTimesTen / 5 + 1));
            _directFiles[i] = count;
            _seqStart[i] = seq;
            seq += count;
            long bytes = 0;
            for (var k = 0; k < count; k++) bytes += SizeOf(i, k);
            _directBytes[i] = bytes;
            _totalFiles[i] = count;
            _totalBytes[i] = bytes;
            if (i > 0) _directSubfolders[ParentOf(i)]++;
        }
        for (var i = folders - 1; i >= 1; i--)
        {
            var parent = ParentOf(i);
            _totalFiles[parent] += _totalFiles[i];
            _totalBytes[parent] += _totalBytes[i];
            _totalSubfolders[parent] += 1 + _totalSubfolders[i];
        }
        Files = seq;
        Bytes = _totalBytes[0];
    }

    internal Mutation MutationKind_ { get; }

    internal long Files { get; }

    internal long Bytes { get; }

    public int FolderCount => _folders;

    private int ParentOf(int index) => (index - 1) / _branching;

    private int Hash(int a, int b) => unchecked((a * 73856093) ^ (b * 19349663) ^ (_seed * 83492791));

    private long SizeOf(int folder, int k) => 1 + (uint)Hash(folder, k + 1000) % 100_000u;

    /// <summary>The header with every sealed total computed from the stream.</summary>
    internal ImportSnapshotHeader Header(string runId = "run-1", ScanCompletionState completion = ScanCompletionState.Complete)
    {
        var sealedFiles = MutationKind_ == Mutation.WrongSealedFileCount ? Files + 1 : Files;
        return new ImportSnapshotHeader
        {
            Completion = completion,
            RunId = runId,
            StartedUtcTicks = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks,
            FinishedUtcTicks = new DateTime(2026, 1, 1, 0, 1, 0, DateTimeKind.Utc).Ticks,
            RootPathAsEntered = Utf16.ToBytes(@"D:\Media"),
            CanonicalRoot = Utf16.ToBytes(@"D:\Media"),
            MountPoint = Utf16.ToBytes(@"D:\"),
            VolumeLabel = Utf16.ToBytes("Data"),
            CapacityBytes = 1_000_000_000,
            FreeBytes = 500_000_000,
            Confidence = IdentityConfidence.Strong,
            Basis = IdentityBasis.Evidence,
            CaptureKind = SourceKind.LocalVolume,
            FsName = "NTFS",
            Files = sealedFiles,
            Folders = _folders,
            Bytes = Bytes,
        };
    }

    /// <summary>A new local source on a new volume (the first snapshot of a source).</summary>
    internal static ImportSourceSpec NewSource(string root = @"\", long serial = 0x1234ABCD) => new ImportSourceSpec.NewSource(
        SourceKind.LocalVolume, ExistingVolumeId: null,
        new ImportVolumeSpec("NTFS", serial, serial & 0xFFFFFFFF, IdentityConfidence.Strong, "Data", Utf16.ToBytes("Data"), 1_000_000_000),
        NetworkRoot: null, NetworkRootKey: null, Utf16.ToBytes(root), IdentityConfidence.Strong, IdentityBasis.Evidence, "Media");

    public IEnumerable<ImportFolder> Folders()
    {
        for (var i = 0; i < _folders; i++)
        {
            if (MutationKind_ == Mutation.MissingFolderRow && i == _folders - 1 && _folders > 2) continue;
            yield return new ImportFolder(i, i == 0 ? -1 : ParentOf(i), FolderName(i), FolderScanStatus.Ok, null, true, 0x10,
                CreatedTicks: 637_000_000_000_000_000L + i, ModifiedTicks: 637_100_000_000_000_000L + i,
                _directBytes[i], _totalBytes[i], _directFiles[i], _totalFiles[i], _directSubfolders[i], _totalSubfolders[i],
                LargestFileBytes: _totalFiles[i] == 0 ? null : 100_000);
        }
        if (MutationKind_ == Mutation.ThrowAfterFolders) throw new InvalidOperationException("planted failure after the folder section");
    }

    public IReadOnlyList<ImportFile> FilesOfFolder(int folderIndex)
    {
        var count = _directFiles[folderIndex];
        if (count == 0) return [];
        var files = new ImportFile[count];
        for (var k = 0; k < count; k++)
        {
            var size = SizeOf(folderIndex, k);
            if (MutationKind_ == Mutation.WrongFileSize && folderIndex == 0 && k == 0) size += 1;
            var seq = _seqStart[folderIndex] + k;
            if (MutationKind_ == Mutation.DuplicateSeq && folderIndex == 0 && k == 0 && Files > 1) seq = 1;
            files[k] = new ImportFile(FileName(folderIndex, k), seq, size, 637_100_000_000_000_000L + k, 637_000_000_000_000_000L, 637_200_000_000_000_000L, 0x20);
        }
        return files;
    }

    public IEnumerable<ImportError> Errors() => [];

    private byte[] FolderName(int index) => index == 0 ? [] : Utf16.ToBytes("dir" + (index % 997));

    private byte[] FileName(int folder, int k)
    {
        // About half the names repeat across folders and snapshots (dictionary sharing), the rest are unique to the snapshot.
        var unique = (uint)Hash(folder, k) % 2u == 0;
        var stem = unique ? $"file_{_label}_{folder}_{k}" : $"common_{(uint)Hash(folder, k) % 5000u}";
        return Utf16.ToBytes(stem + ".ext" + (k % 7));
    }
}

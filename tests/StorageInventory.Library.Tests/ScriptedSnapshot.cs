using StorageInventory.Core;
using StorageInventory.Core.Identity;
using StorageInventory.History.Identity;
using StorageInventory.History.Library;

namespace StorageInventory.Library.Tests;

/// <summary>
/// A small, explicit snapshot whose folders, files and errors a test writes down, with every sealed total computed from what is
/// written so that it satisfies §9.5. Unlike <see cref="SyntheticSnapshot"/> (a generator of uniform trees whose folders are all
/// <c>Ok</c> and complete), it can hold a <c>Partial</c>, an <c>Unreadable</c> and a <c>ReparsePointSkipped</c> folder, a folder whose
/// children were observed beside one that was not, informational reparse-point error rows and incomplete ancestors, which are the
/// paths of the importer and of the §9.5 verification that the generator never reaches (C4-M03).
/// </summary>
internal sealed class ScriptedSnapshot : ISnapshotRowSource
{
    internal sealed record Folder(string Name, int Parent, FolderScanStatus Status = FolderScanStatus.Ok, ScanErrorType? Reason = null);

    internal sealed record File(int Folder, string Name, long Size);

    internal sealed record Error(string RelativePath, ScanErrorType Type, string Message);

    private readonly List<Folder> _folders;
    private readonly List<File> _files;
    private readonly List<Error> _errors;
    private readonly int[] _directFiles;
    private readonly long[] _directBytes;
    private readonly long[] _directSubfolders;
    private readonly long[] _totalFiles;
    private readonly long[] _totalBytes;
    private readonly long[] _totalSubfolders;
    private readonly bool[] _subtreeComplete;

    internal ScriptedSnapshot(IEnumerable<Folder> folders, IEnumerable<File> files, IEnumerable<Error>? errors = null)
    {
        _folders = [.. folders];
        _files = [.. files];
        _errors = [.. errors ?? []];
        var n = _folders.Count;
        _directFiles = new int[n];
        _directBytes = new long[n];
        _directSubfolders = new long[n];
        _totalFiles = new long[n];
        _totalBytes = new long[n];
        _totalSubfolders = new long[n];
        _subtreeComplete = new bool[n];
        foreach (var file in _files)
        {
            _directFiles[file.Folder]++;
            _directBytes[file.Folder] += file.Size;
        }
        for (var i = 0; i < n; i++)
        {
            _totalFiles[i] = _directFiles[i];
            _totalBytes[i] = _directBytes[i];
            _subtreeComplete[i] = _folders[i].Status is FolderScanStatus.Ok or FolderScanStatus.ReparsePointSkipped;   // only Unreadable and Partial folders are incomplete (v1, invariant 9)
            if (i > 0) _directSubfolders[_folders[i].Parent]++;
        }
        for (var i = n - 1; i >= 1; i--)
        {
            var parent = _folders[i].Parent;
            _totalFiles[parent] += _totalFiles[i];
            _totalBytes[parent] += _totalBytes[i];
            _totalSubfolders[parent] += 1 + _totalSubfolders[i];
            if (!_subtreeComplete[i]) _subtreeComplete[parent] = false;
        }
        Files = _files.Count;
        Bytes = _totalBytes[0];
    }

    internal long Files { get; }

    internal long Bytes { get; }

    internal long RealErrors => _errors.Count(e => !e.Type.IsInformational());

    internal bool Complete => _subtreeComplete[0];

    public int FolderCount => _folders.Count;

    /// <summary>The rich fixture: a root with an ordinary folder <c>docs</c> (three files), a <c>Partial</c> folder <c>mixed</c> whose
    /// listed child <c>inner</c> holds two files, an <c>Unreadable</c> folder <c>locked</c>, and a <c>ReparsePointSkipped</c> folder
    /// <c>link</c>; one real error (access denied) and one informational reparse row. Folder indexes: root 0, docs 1, mixed 2, inner 3,
    /// locked 4, link 5. The scan is Incomplete (the root is not subtree-complete).</summary>
    internal static ScriptedSnapshot Rich(string tag = "") => new(
        [
            new Folder("", -1),
            new Folder("docs" + tag, 0),
            new Folder("mixed" + tag, 0, FolderScanStatus.Partial, ScanErrorType.IOError),
            new Folder("inner" + tag, 2),
            new Folder("locked" + tag, 0, FolderScanStatus.Unreadable, ScanErrorType.AccessDenied),
            new Folder("link" + tag, 0, FolderScanStatus.ReparsePointSkipped, ScanErrorType.ReparsePointSkipped),
        ],
        [
            new File(0, "readme.txt", 10), new File(1, "a.TXT", 100), new File(1, "b.txt", 200), new File(1, "c.bin", 300),
            new File(2, "partial.dat", 1000), new File(3, "x.log", 7), new File(3, "y.log", 8),
        ],
        [
            new Error("locked" + tag, ScanErrorType.AccessDenied, "Access to the path is denied."),
            new Error("link" + tag, ScanErrorType.ReparsePointSkipped, "Reparse point not followed."),
        ]);

    internal ImportSnapshotHeader Header(string runId = "run-1") => new()
    {
        Completion = Complete ? ScanCompletionState.Complete : ScanCompletionState.Incomplete,
        RunId = runId,
        StartedUtcTicks = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks,
        FinishedUtcTicks = new DateTime(2026, 1, 1, 0, 1, 0, DateTimeKind.Utc).Ticks,
        RootPathAsEntered = Utf16.ToBytes(@"D:\Scripted"),
        CanonicalRoot = Utf16.ToBytes(@"D:\Scripted"),
        MountPoint = Utf16.ToBytes(@"D:\"),
        VolumeLabel = Utf16.ToBytes("Data"),
        CapacityBytes = 1_000_000_000,
        FreeBytes = 500_000_000,
        Confidence = IdentityConfidence.Strong,
        Basis = IdentityBasis.Evidence,
        CaptureKind = SourceKind.LocalVolume,
        FsName = "NTFS",
        Files = Files,
        Folders = _folders.Count,
        Bytes = Bytes,
        ScanErrors = RealErrors,
        ReparsePointsSkipped = _folders.Count(f => f.Status == FolderScanStatus.ReparsePointSkipped),
        LocallyIncompleteFolders = _folders.Count(f => f.Status is FolderScanStatus.Unreadable or FolderScanStatus.Partial),
    };

    /// <summary>A new local source (own root and own volume serial), so that several sources can live in one Library.</summary>
    internal static ImportSourceSpec NewSource(string root, long serial) => SyntheticSnapshot.NewSource(root, serial);

    public IEnumerable<ImportFolder> Folders()
    {
        for (var i = 0; i < _folders.Count; i++)
        {
            var f = _folders[i];
            yield return new ImportFolder(i, i == 0 ? -1 : f.Parent, Utf16.ToBytes(f.Name), f.Status, f.Reason, _subtreeComplete[i], 0x10,
                CreatedTicks: 637_000_000_000_000_000L + i, ModifiedTicks: 637_100_000_000_000_000L + i,
                _directBytes[i], _totalBytes[i], _directFiles[i], _totalFiles[i], _directSubfolders[i], _totalSubfolders[i],
                LargestFileBytes: _totalFiles[i] == 0 ? null : _files.Where(x => IsInSubtree(x.Folder, i)).Max(x => x.Size));
        }
    }

    private bool IsInSubtree(int folder, int ancestor)
    {
        for (var f = folder; ; f = _folders[f].Parent)
        {
            if (f == ancestor) return true;
            if (f <= 0) return false;
        }
    }

    public IReadOnlyList<ImportFile> FilesOfFolder(int folderIndex)
    {
        var result = new List<ImportFile>();
        for (var seq = 0; seq < _files.Count; seq++)
        {
            var file = _files[seq];
            if (file.Folder == folderIndex) result.Add(new ImportFile(Utf16.ToBytes(file.Name), seq, file.Size, 637_100_000_000_000_000L + seq, 637_000_000_000_000_000L, 637_200_000_000_000_000L, 0x20));
        }
        return result;
    }

    public IEnumerable<ImportError> Errors()
    {
        var seq = 0;
        foreach (var error in _errors) yield return new ImportError(seq++, Utf16.ToBytes(error.RelativePath), error.Type, Utf16.ToBytes(error.Message));
    }
}

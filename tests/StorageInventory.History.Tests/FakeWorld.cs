using StorageInventory.Core.Identity;

namespace StorageInventory.History.Tests;

/// <summary>
/// A tiny fake namespace behind <see cref="IVolumeEvidenceSource"/>, so the lifecycle of ID-10 can be proved without a
/// filesystem and scenarios read like the real ones: a path resolves to a directory OBJECT; a handle binds to the object at
/// open time and keeps reporting on it wherever the path leads later; renaming changes the object's canonical path;
/// re-pointing changes where a path resolves; removing the medium invalidates handles. It records every open, read and
/// close, so a test can prove which path each open used and which handle each reading came through.
/// </summary>
internal sealed class FakeWorld : IVolumeEvidenceSource
{
    /// <summary>A directory object on some volume.</summary>
    public sealed class Dir
    {
        public required string Canonical { get; set; }
        public string? FileSystem { get; set; } = "NTFS";
        public uint? Serial32 { get; set; } = 0xFE561879;
        public ulong? Serial64 { get; set; } = 0x1AFE5644FE561879;
        public string? Label { get; set; } = "Data";
        public long? Capacity { get; set; } = 500_000_000_000;
        public long Free { get; set; } = 1_000_000;
        public FileId128? RootId { get; set; } = Ev.NtfsRoot;

        /// <summary>After a surprise removal every handle bound to the object fails each query (Win32 error 59, as measured
        /// on a forcibly disconnected network drive).</summary>
        public bool Invalidated { get; set; }
    }

    public enum Event { Open, Read, Close }

    public sealed record Entry(Event Kind, int HandleId, string? Path, EvidenceStage? Stage);

    private readonly Dictionary<string, Dir> _namespace = new(StringComparer.OrdinalIgnoreCase);
    private int _nextHandle = 1;

    /// <summary>Every open, read and close, in order.</summary>
    public List<Entry> Log { get; } = [];

    /// <summary>Handles opened and not yet closed (a leak if non-zero when a test ends).</summary>
    public int OpenHandles { get; private set; }

    /// <summary>Makes <paramref name="path"/> resolve to <paramref name="dir"/> (like a drive letter, SUBST or folder path).</summary>
    public Dir Map(string path, Dir dir)
    {
        _namespace[path] = dir;
        return dir;
    }

    /// <summary>The path stops resolving (the folder was deleted, the drive letter was removed).</summary>
    public void Unmap(string path) => _namespace.Remove(path);

    public IEvidenceHandle Open(string enumeratedPath)
    {
        var id = _nextHandle++;
        _namespace.TryGetValue(enumeratedPath, out var dir);
        Log.Add(new Entry(Event.Open, id, enumeratedPath, null));
        if (dir is not null) OpenHandles++;
        return new Handle(this, id, enumeratedPath, dir);
    }

    public IEnumerable<string> OpenedPaths => Log.Where(e => e.Kind == Event.Open).Select(e => e.Path!);

    public IEnumerable<Entry> Reads => Log.Where(e => e.Kind == Event.Read);

    private sealed class Handle(FakeWorld world, int id, string openedPath, Dir? dir) : IEvidenceHandle
    {
        private bool _disposed;

        public string OpenedPath => openedPath;

        public VolumeEvidence Read(EvidenceStage stage)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            world.Log.Add(new Entry(Event.Read, id, openedPath, stage));
            if (dir is null) return NoHandle(stage);
            if (dir.Invalidated) return Invalid(stage);
            return Ev.Reading(openedPath, dir.Canonical, dir.FileSystem, dir.Serial32, dir.Serial64, dir.Label, dir.Capacity, dir.RootId, stage, dir.Free);
        }

        private VolumeEvidence NoHandle(EvidenceStage stage)
        {
            const string why = "the directory handle could not be opened";
            return new VolumeEvidence(stage, openedPath)
            {
                HandleOpened = EvidenceItem<bool>.Failed("CreateFileW", 2),
                CanonicalPath = EvidenceItem<string>.Missing("GetFinalPathNameByHandleW", why),
                FileSystemName = EvidenceItem<string>.Missing("GetVolumeInformationByHandleW", why),
                VolumeSerial32 = EvidenceItem<uint>.Missing("GetVolumeInformationByHandleW", why),
                VolumeSerial64 = EvidenceItem<ulong>.Missing("GetFileInformationByHandleEx/FileIdInfo", why),
                RootDirectoryFileId = EvidenceItem<FileId128>.Missing("GetFileInformationByHandleEx/FileIdInfo", why),
            };
        }

        private VolumeEvidence Invalid(EvidenceStage stage) => new VolumeEvidence(stage, openedPath)
        {
            HandleOpened = EvidenceItem<bool>.Of(true, "CreateFileW"),
            CanonicalPath = EvidenceItem<string>.Failed("GetFinalPathNameByHandleW", 59),
            FileSystemName = EvidenceItem<string>.Failed("GetVolumeInformationByHandleW", 59),
            VolumeSerial32 = EvidenceItem<uint>.Failed("GetVolumeInformationByHandleW", 59),
            VolumeSerial64 = EvidenceItem<ulong>.Failed("GetFileInformationByHandleEx/FileIdInfo", 59),
            RootDirectoryFileId = EvidenceItem<FileId128>.Failed("GetFileInformationByHandleEx/FileIdInfo", 59),
        };

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            world.Log.Add(new Entry(Event.Close, id, openedPath, null));
            if (dir is not null) world.OpenHandles--;
        }
    }
}

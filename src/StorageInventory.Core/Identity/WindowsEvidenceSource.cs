using Microsoft.Win32.SafeHandles;
using StorageInventory.Core.Paths;

namespace StorageInventory.Core.Identity;

/// <summary>
/// The real <see cref="IVolumeEvidenceSource"/>: a zero-access directory handle on the enumerated path and the read-only
/// queries of <see cref="NativeMethods"/> (§7.2, §7.8). Nothing here reads file contents, lists a directory, writes or
/// changes anything; the handle carries no data rights.
/// </summary>
internal sealed class WindowsEvidenceSource : IVolumeEvidenceSource
{
    public static WindowsEvidenceSource Instance { get; } = new();

    private WindowsEvidenceSource()
    {
    }

    public IEvidenceHandle Open(string enumeratedPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(enumeratedPath);
        var handle = NativeMethods.TryOpenDirectoryZeroAccess(enumeratedPath, out var error);
        return new WindowsEvidenceHandle(enumeratedPath, handle, error);
    }
}

/// <summary>A zero-access directory handle opened on one path, owned by this object and closed by <see cref="Dispose"/>.</summary>
internal sealed class WindowsEvidenceHandle : IEvidenceHandle
{
    private const string OpenCall = "CreateFileW";
    private const string FinalPathCall = "GetFinalPathNameByHandleW";
    private const string VolumeInformationCall = "GetVolumeInformationByHandleW";
    private const string FileIdCall = "GetFileInformationByHandleEx/FileIdInfo";
    private const string VolumePathCall = "GetVolumePathNameW";
    private const string DiskSpaceCall = "GetDiskFreeSpaceExW";

    private readonly SafeFileHandle? _handle;
    private readonly int _openError;
    private bool _disposed;

    internal WindowsEvidenceHandle(string openedPath, SafeFileHandle? handle, int openError)
    {
        OpenedPath = openedPath;
        _handle = handle;
        _openError = openError;
    }

    public string OpenedPath { get; }

    /// <summary>False when the path could not be opened: every reading then reports why.</summary>
    public bool IsOpen => _handle is not null;

    public VolumeEvidence Read(EvidenceStage stage)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _handle is null ? NoHandle(stage) : ReadThrough(_handle, stage);
    }

    private VolumeEvidence NoHandle(EvidenceStage stage)
    {
        const string why = "the directory handle could not be opened";
        return new VolumeEvidence(stage, OpenedPath)
        {
            HandleOpened = EvidenceItem<bool>.Failed(OpenCall, _openError),
            Kind = SourceLocation.ClassifyKind(null, OpenedPath),
            CanonicalPath = EvidenceItem<string>.Missing(FinalPathCall, why),
            FileSystemName = EvidenceItem<string>.Missing(VolumeInformationCall, why),
            VolumeSerial32 = EvidenceItem<uint>.Missing(VolumeInformationCall, why),
            FileSystemFlags = EvidenceItem<uint>.Missing(VolumeInformationCall, why),
            VolumeLabel = EvidenceItem<string>.Missing(VolumeInformationCall, why),
            VolumeSerial64 = EvidenceItem<ulong>.Missing(FileIdCall, why),
            RootDirectoryFileId = EvidenceItem<FileId128>.Missing(FileIdCall, why),
            MountPoint = EvidenceItem<string>.Missing(VolumePathCall, why),
            CapacityBytes = EvidenceItem<long>.Missing(DiskSpaceCall, why),
            FreeBytes = EvidenceItem<long>.Missing(DiskSpaceCall, why),
        };
    }

    private VolumeEvidence ReadThrough(SafeFileHandle handle, EvidenceStage stage)
    {
        var canonical = NativeMethods.TryGetFinalPath(handle, out var path, out var pathError)
            ? EvidenceItem<string>.Of(path!, FinalPathCall)
            : EvidenceItem<string>.Failed(FinalPathCall, pathError);

        var evidence = new VolumeEvidence(stage, OpenedPath)
        {
            HandleOpened = EvidenceItem<bool>.Of(true, OpenCall),
            Kind = SourceLocation.ClassifyKind(canonical.IsAvailable ? canonical.Value : null, OpenedPath),
            CanonicalPath = canonical,
        };

        if (NativeMethods.TryGetVolumeInformation(handle, out var volume, out var volumeError))
        {
            evidence = evidence with
            {
                FileSystemName = volume.FileSystemName.Length > 0
                    ? EvidenceItem<string>.Of(volume.FileSystemName, VolumeInformationCall)
                    : EvidenceItem<string>.Missing(VolumeInformationCall, "the filesystem name is empty"),
                VolumeSerial32 = EvidenceItem<uint>.Of(volume.SerialNumber, VolumeInformationCall),
                FileSystemFlags = EvidenceItem<uint>.Of(volume.FileSystemFlags, VolumeInformationCall),
                VolumeLabel = EvidenceItem<string>.Of(volume.VolumeLabel, VolumeInformationCall),
            };
        }
        else
        {
            evidence = evidence with
            {
                FileSystemName = EvidenceItem<string>.Failed(VolumeInformationCall, volumeError),
                VolumeSerial32 = EvidenceItem<uint>.Failed(VolumeInformationCall, volumeError),
                FileSystemFlags = EvidenceItem<uint>.Failed(VolumeInformationCall, volumeError),
                VolumeLabel = EvidenceItem<string>.Failed(VolumeInformationCall, volumeError),
            };
        }

        if (NativeMethods.TryGetFileId(handle, out var fileId, out var fileIdError))
        {
            evidence = evidence with
            {
                VolumeSerial64 = EvidenceItem<ulong>.Of(fileId.VolumeSerialNumber, FileIdCall),
                RootDirectoryFileId = EvidenceItem<FileId128>.Of(new FileId128(fileId.FileIdLow, fileId.FileIdHigh), FileIdCall),
            };
        }
        else
        {
            evidence = evidence with
            {
                VolumeSerial64 = EvidenceItem<ulong>.Failed(FileIdCall, fileIdError),
                RootDirectoryFileId = EvidenceItem<FileId128>.Failed(FileIdCall, fileIdError),
            };
        }

        // The two path queries have no handle form. They are asked about the canonical path read from THIS handle, so
        // each reading reports the volume that holds the object it opened (not whatever the entered path means now).
        // Mount point, capacity and free space are recorded for display and audit only: they are never identity.
        var mountPoint = canonical.IsAvailable
            ? (NativeMethods.TryGetVolumePathName(canonical.Value!, out var mount, out var mountError)
                ? EvidenceItem<string>.Of(mount!, VolumePathCall)
                : EvidenceItem<string>.Failed(VolumePathCall, mountError))
            : EvidenceItem<string>.Missing(VolumePathCall, "there is no canonical path to ask about");
        evidence = evidence with { MountPoint = mountPoint };

        var spaceQuery = mountPoint.IsAvailable ? mountPoint.Value : canonical.IsAvailable ? canonical.Value : null;
        if (spaceQuery is null)
        {
            return evidence with
            {
                CapacityBytes = EvidenceItem<long>.Missing(DiskSpaceCall, "there is no path to ask about"),
                FreeBytes = EvidenceItem<long>.Missing(DiskSpaceCall, "there is no path to ask about"),
            };
        }
        return NativeMethods.TryGetDiskFreeSpace(spaceQuery, out var space, out var spaceError)
            ? evidence with
            {
                CapacityBytes = EvidenceItem<long>.Of(space.CapacityBytes, DiskSpaceCall),
                FreeBytes = EvidenceItem<long>.Of(space.FreeBytesAvailableToCaller, DiskSpaceCall),
            }
            : evidence with
            {
                CapacityBytes = EvidenceItem<long>.Failed(DiskSpaceCall, spaceError),
                FreeBytes = EvidenceItem<long>.Failed(DiskSpaceCall, spaceError),
            };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _handle?.Dispose();
    }
}

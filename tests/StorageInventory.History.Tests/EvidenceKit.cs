using StorageInventory.Core.Identity;
using StorageInventory.History.Identity;

namespace StorageInventory.History.Tests;

/// <summary>Builders for fake readings, shaped like what <see cref="WindowsEvidenceSource"/> returns on real volumes (the
/// NTFS values are those measured on Windows 11 in C3; the others are invented but plausible). No filesystem is involved.</summary>
internal static class Ev
{
    private const string OpenCall = "CreateFileW";
    private const string PathCall = "GetFinalPathNameByHandleW";
    private const string VolumeCall = "GetVolumeInformationByHandleW";
    private const string FileIdCall = "GetFileInformationByHandleEx/FileIdInfo";
    private const string MountCall = "GetVolumePathNameW";
    private const string SpaceCall = "GetDiskFreeSpaceExW";

    /// <summary>The root directory of an NTFS volume: MFT record 5, sequence 5 (the same on every NTFS volume, Q-13).</summary>
    public static readonly FileId128 NtfsRoot = new(0x0005000000000005, 0);

    /// <summary>One reading. A null argument means "the call for it failed" (Win32 error 5 for the volume query, 87 for
    /// <c>FileIdInfo</c>, as measured on a UDF volume).</summary>
    public static VolumeEvidence Reading(string entered, string canonical, string? fs, uint? serial32, ulong? serial64, string? label, long? capacity,
        FileId128? rootId = null, EvidenceStage stage = EvidenceStage.E0Preflight, long freeBytes = 1_000_000)
    {
        var located = SourceLocation.TryDerive(canonical, out var location);
        var mount = located ? (location.Kind == SourceKind.Network ? location.NetworkRoot + @"\" : canonical[..3]) : null;
        return new VolumeEvidence(stage, entered)
        {
            HandleOpened = EvidenceItem<bool>.Of(true, OpenCall),
            Kind = located ? location.Kind : SourceKind.LocalVolume,
            CanonicalPath = EvidenceItem<string>.Of(canonical, PathCall),
            FileSystemName = fs is null ? EvidenceItem<string>.Failed(VolumeCall, 5) : EvidenceItem<string>.Of(fs, VolumeCall),
            VolumeSerial32 = serial32 is null ? EvidenceItem<uint>.Failed(VolumeCall, 5) : EvidenceItem<uint>.Of(serial32.Value, VolumeCall),
            FileSystemFlags = EvidenceItem<uint>.Of(0x03E72EFF, VolumeCall),
            VolumeLabel = label is null ? EvidenceItem<string>.Failed(VolumeCall, 5) : EvidenceItem<string>.Of(label, VolumeCall),
            VolumeSerial64 = serial64 is null ? EvidenceItem<ulong>.Failed(FileIdCall, 87) : EvidenceItem<ulong>.Of(serial64.Value, FileIdCall),
            RootDirectoryFileId = rootId is null ? EvidenceItem<FileId128>.Failed(FileIdCall, 87) : EvidenceItem<FileId128>.Of(rootId.Value, FileIdCall),
            MountPoint = mount is null ? EvidenceItem<string>.Failed(MountCall, 5) : EvidenceItem<string>.Of(mount, MountCall),
            CapacityBytes = capacity is null ? EvidenceItem<long>.Failed(SpaceCall, 5) : EvidenceItem<long>.Of(capacity.Value, SpaceCall),
            FreeBytes = capacity is null ? EvidenceItem<long>.Failed(SpaceCall, 5) : EvidenceItem<long>.Of(freeBytes, SpaceCall),
        };
    }

    /// <summary>A local NTFS volume (or folder on it): Strong evidence. The 32-bit serial is the 64-bit one's low half, as on real NTFS.</summary>
    public static VolumeEvidence Ntfs(string canonical = @"C:\Media", ulong serial64 = 0x1AFE5644FE561879, string? label = "Data", long capacity = 500_000_000_000,
        string? entered = null, EvidenceStage stage = EvidenceStage.E0Preflight, FileId128? rootId = null) =>
        Reading(entered ?? canonical, canonical, "NTFS", unchecked((uint)serial64), serial64, label, capacity, rootId ?? NtfsRoot, stage);

    /// <summary>A local ReFS volume.</summary>
    public static VolumeEvidence Refs(string canonical = @"R:\", ulong serial64 = 0x5EF5EF5E00000042, string? label = "Dev", long capacity = 100_000_000_000,
        EvidenceStage stage = EvidenceStage.E0Preflight) =>
        Reading(canonical, canonical, "ReFS", unchecked((uint)serial64), serial64, label, capacity, new FileId128(0x0000000000000001, 0x0000000000000500), stage);

    /// <summary>A local FAT-family volume (FAT, FAT32, exFAT). <paramref name="serial64"/> is whatever <c>FileIdInfo</c>
    /// returned, null when it is not provided: it must never make the volume Strong.</summary>
    public static VolumeEvidence Fat(string fs = "FAT32", string canonical = @"E:\", uint serial32 = 0xA1B2C3D4, string? label = "USBSTICK", long capacity = 31_000_000_000,
        ulong? serial64 = null, EvidenceStage stage = EvidenceStage.E0Preflight) =>
        Reading(canonical, canonical, fs, serial32, serial64, label, capacity, serial64 is null ? null : new FileId128(1, 0), stage);

    /// <summary>A UDF volume (optical media): <c>FileIdInfo</c> is not provided, as measured on a real disc.</summary>
    public static VolumeEvidence Udf(string canonical = @"D:\", uint serial32 = 0xC1FB837E, string? label = "DISC", long capacity = 9_089_024,
        EvidenceStage stage = EvidenceStage.E0Preflight) =>
        Reading(canonical, canonical, "UDF", serial32, null, label, capacity, null, stage);

    /// <summary>A network share. The server, like the Windows SMB server measured in C3, reports the underlying volume's
    /// filesystem and serials; the source is still PathOnly because it is network.</summary>
    public static VolumeEvidence Share(string canonical = @"\\nas\media", string? entered = null, ulong serial64 = 0x1AFE5644FE561879, string? label = "",
        long capacity = 4_000_000_000_000, EvidenceStage stage = EvidenceStage.E0Preflight) =>
        Reading(entered ?? canonical, canonical, "NTFS", unchecked((uint)serial64), serial64, label, capacity, NtfsRoot, stage);

    public static VolumeEvidence AtStage(this VolumeEvidence e, EvidenceStage stage) => e with { Stage = stage };

    public static IdentityAssessment Assess(VolumeEvidence e0) => IdentityClassifier.Assess(e0);
}

/// <summary>A stand-in for the comparison candidate key (NAME-03), for tests only. The real, table-driven key is gate C7's;
/// C3 deliberately has none. This one folds ASCII letters and nothing else, which is all these tests' share and root names need.</summary>
internal sealed class AsciiFoldKey : IComparisonKey
{
    public static readonly AsciiFoldKey Instance = new();

    public string KeyOf(string text) => string.Create(text.Length, text, static (span, source) =>
    {
        for (var i = 0; i < span.Length; i++)
        {
            var c = source[i];
            span[i] = c is >= 'a' and <= 'z' ? (char)(c - 32) : c;
        }
    });
}

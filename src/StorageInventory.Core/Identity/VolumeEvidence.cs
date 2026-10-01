namespace StorageInventory.Core.Identity;

/// <summary>Which reading of the identity items a <see cref="VolumeEvidence"/> is (ID-10). The four readings are
/// compared item by item; only the identity items of ID-13 take part (never label, capacity or free space).</summary>
internal enum EvidenceStage
{
    /// <summary><b>E0</b>, at preflight (P0): through a handle opened on the enumerated path and closed again. Decides what
    /// is required later and whether the source can be saved at all.</summary>
    E0Preflight = 0,

    /// <summary><b>E1</b>, when the observation window opens (P2): through the handle opened on the enumerated path that is
    /// then HELD for the whole window.</summary>
    E1WindowStart = 1,

    /// <summary><b>E2</b>, when the window closes (P4): through that same held handle. Asks whether the object the window
    /// began with is still reachable, on the same volume, at the same canonical position.</summary>
    E2WindowEndHeld = 2,

    /// <summary><b>E3</b>, when the window closes (P4): through a fresh open of the enumerated path (never of the canonical
    /// path, which a re-pointed letter does not change). Asks whether the path the scanner used still leads to that
    /// object.</summary>
    E3WindowEndFresh = 3,
}

/// <summary>
/// Everything one reading learned about the source the scan enumerates (§7.2): the identity items (ID-13), and the
/// mutable information that is recorded but never compared (label, capacity, free space, flags, mount point). Every item
/// is an <see cref="EvidenceItem{T}"/>, so a value, a failed call, an unsupported query and "never attempted" stay
/// distinct, and a failure keeps its reason. Immutable.
/// </summary>
/// <param name="Stage">Which of the four readings this is.</param>
/// <param name="EnumeratedPath">The path that was opened: the scanner's own path (v1's <c>validation.RootFullPath</c>, the
/// normalised path as entered), never the canonical path read back from it.</param>
internal sealed record VolumeEvidence(EvidenceStage Stage, string EnumeratedPath)
{
    /// <summary>Whether the zero-access directory handle could be opened. When it could not, nothing that needs the handle
    /// was attempted, and each of those items says so.</summary>
    public EvidenceItem<bool> HandleOpened { get; init; }

    /// <summary>Local volume or network source, decided from where Windows says the opened object is (a mapped network
    /// letter resolves to a UNC path and so counts as network), never from the drive-letter syntax of the input.</summary>
    public SourceKind Kind { get; init; } = SourceKind.LocalVolume;

    /// <summary>The kind of source this reading describes, decided in ONE place so that ID-13 (what is required), ID-01 (the
    /// confidence) and ID-10 (what must not change) cannot disagree: from where Windows says the opened object is, a UNC
    /// canonical path being a network source and a drive-letter one a local volume; only when no canonical path was
    /// obtained, from the <see cref="Kind"/> the reading carries.</summary>
    public SourceKind ResolvedKind => CanonicalPath.IsAvailable && SourceLocation.TryDerive(CanonicalPath.Value!, out var location) ? location.Kind : Kind;

    // ---- identity items (ID-13) ----

    /// <summary>The canonical path of the opened object, from <c>GetFinalPathNameByHandleW</c> on the handle (ID-13 item 1).</summary>
    public EvidenceItem<string> CanonicalPath { get; init; }

    /// <summary>The filesystem name as the driver reports it (<c>NTFS</c>, <c>ReFS</c>, <c>exFAT</c>, <c>UDF</c>, ...).</summary>
    public EvidenceItem<string> FileSystemName { get; init; }

    /// <summary>The 32-bit volume serial from <c>GetVolumeInformationByHandleW</c>.</summary>
    public EvidenceItem<uint> VolumeSerial32 { get; init; }

    /// <summary>The 64-bit volume serial from <c>FileIdInfo</c>.</summary>
    public EvidenceItem<ulong> VolumeSerial64 { get; init; }

    /// <summary>The 128-bit file ID of the opened root directory from <c>FileIdInfo</c>. Information for subfolder
    /// sources; never volume identity (§7.2, Q-13).</summary>
    public EvidenceItem<FileId128> RootDirectoryFileId { get; init; }

    // ---- recorded, never compared ----

    public EvidenceItem<uint> FileSystemFlags { get; init; }
    public EvidenceItem<string> VolumeLabel { get; init; }

    /// <summary>The mount point of the volume that holds the canonical path (<c>GetVolumePathNameW</c>).</summary>
    public EvidenceItem<string> MountPoint { get; init; }

    public EvidenceItem<long> CapacityBytes { get; init; }

    /// <summary>Free bytes available to the calling account (<c>GetDiskFreeSpaceExW</c>).</summary>
    public EvidenceItem<long> FreeBytes { get; init; }
}

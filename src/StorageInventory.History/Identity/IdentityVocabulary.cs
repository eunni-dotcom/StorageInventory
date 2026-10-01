namespace StorageInventory.History.Identity;

/// <summary>How strong the evidence behind a source or capture was (ID-01, §7.3). The stable codes are the schema's
/// (§9.4) and the order is the order of strength: a smaller code is a stronger level. Describes identity; it is not
/// identity.</summary>
internal enum IdentityConfidence
{
    /// <summary>NTFS or ReFS, local, with a non-zero 64-bit serial from <c>FileIdInfo</c>: matched automatically on
    /// (filesystem, 64-bit serial) plus the exact root.</summary>
    Strong = 1,

    /// <summary>FAT, FAT32 or exFAT, local, with a non-zero 32-bit serial: matched automatically only when filesystem,
    /// serial, label and capacity (±1%) corroborate exactly one volume.</summary>
    Moderate = 2,

    /// <summary>Network sources; UDF or optical media; unknown filesystems; a missing or zero serial; a failed evidence
    /// call. Network sources match by location; local ones never match automatically.</summary>
    PathOnly = 3,
}

/// <summary>What a source's recognition, or a snapshot's attachment, rested on (§7.1, ID-06). Not the same thing as
/// <see cref="IdentityConfidence"/>. Fixed when a source is created (ID-12).</summary>
internal enum IdentityBasis
{
    /// <summary>Hardware evidence that is strong enough on its own: Strong, or Moderate with full corroboration.</summary>
    Evidence = 1,

    /// <summary>The user's explicit answer to the identity prompt (ID-04), because the evidence alone was not enough.</summary>
    UserAsserted = 2,

    /// <summary>The location only: a network share by its canonical name, or a local source for which no evidence exists.</summary>
    Location = 3,
}

/// <summary>The identity items that ID-13 requires and ID-10 compares. Label, capacity, free space, flags and the mount
/// point are deliberately not here: they change legitimately.</summary>
internal enum IdentityItem
{
    /// <summary>The canonical path of the opened object. Required for every source.</summary>
    CanonicalPath,

    /// <summary>Required for local volumes; for network sources when the provider returned it at E0.</summary>
    FileSystemName,

    /// <summary>The 32-bit volume serial. Same rule as the filesystem name.</summary>
    VolumeSerial32,

    /// <summary>The 64-bit volume serial, required from E1 on when <c>FileIdInfo</c> succeeded at E0.</summary>
    VolumeSerial64,

    /// <summary>The 128-bit file ID of the opened root directory, required from E1 on when <c>FileIdInfo</c> succeeded at E0.</summary>
    RootDirectoryFileId,

    /// <summary>Local or network, derived from the canonical path.</summary>
    SourceKind,
}

/// <summary>
/// The filesystem names the confidence rules know. Names are what the driver reports through
/// <c>GetVolumeInformationByHandleW</c> and are compared ignoring case (they are fixed ASCII keywords, never user text).
/// Confidence is keyed by the NAME, so a FAT-family volume can never reach Strong, whatever <c>FileIdInfo</c> returns on it
/// (ID-01, Q-02).
/// </summary>
internal static class FileSystemNames
{
    /// <summary>NTFS and ReFS: the only filesystems whose 64-bit serial makes a source Strong.</summary>
    public static bool IsStrongCapable(string? name) => Same(name, "NTFS") || Same(name, "ReFS");

    /// <summary>FAT, FAT32 and exFAT: Moderate at best.</summary>
    public static bool IsFatFamily(string? name) => Same(name, "FAT") || Same(name, "FAT32") || Same(name, "exFAT");

    /// <summary>The same filesystem type for matching: the reported names are equal, ignoring case.</summary>
    public static bool Same(string? a, string? b) => a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

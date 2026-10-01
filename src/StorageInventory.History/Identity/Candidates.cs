using StorageInventory.Core.Identity;

namespace StorageInventory.History.Identity;

/// <summary>
/// A volume the Library already knows: what §9.2's <c>volume</c> row holds that matching needs. The shape follows the
/// specification's columns and freezes nothing beyond them. Immutable; the label and capacity are the latest observed
/// (information, never identity), the confidence is fixed when the row is created.
/// </summary>
/// <param name="VolumeId">The row's key.</param>
/// <param name="FsType">The filesystem name as reported (<c>NTFS</c>, <c>exFAT</c>, ...).</param>
/// <param name="Serial64">The 64-bit serial (<c>FileIdInfo</c>), null when it was not usable.</param>
/// <param name="Serial32">The 32-bit serial, null when it was not usable.</param>
/// <param name="Confidence">The level of the evidence the volume was created from (fixed).</param>
/// <param name="Label">The latest volume label seen.</param>
/// <param name="CapacityBytes">The latest capacity seen.</param>
internal sealed record VolumeCandidate(long VolumeId, string FsType, ulong? Serial64, uint? Serial32, IdentityConfidence Confidence,
    string? Label, long? CapacityBytes);

/// <summary>
/// A source the Library already knows (§9.2's <c>source</c> row). Local sources belong to a volume; network sources are
/// keyed by the comparison key of their canonical <c>\\server\share</c>. Confidence and basis are fixed when the source is
/// created and never change (ID-12).
/// </summary>
/// <param name="SourceId">The row's key.</param>
/// <param name="Kind">Local volume or network.</param>
/// <param name="VolumeId">The volume, for a local source; otherwise null.</param>
/// <param name="NetworkRootKey">The comparison key of the canonical <c>\\server\share</c>, for a network source; otherwise null.</param>
/// <param name="RootInVolume">The root's exact path below the volume or share root (ID-02).</param>
/// <param name="Confidence">Fixed at creation.</param>
/// <param name="Basis">Fixed at creation.</param>
internal sealed record SourceCandidate(long SourceId, SourceKind Kind, long? VolumeId, string? NetworkRootKey, string RootInVolume,
    IdentityConfidence Confidence, IdentityBasis Basis);

/// <summary>
/// Another volume that is mounted RIGHT NOW (not the one being captured), with the evidence that identifies it. It lets the
/// matcher honour ID-05: when two currently mounted volumes report the same identity, ask, never assign silently. Whoever
/// calls the matcher builds the list (a later gate, on demand, from local fixed and removable drives only, ID-07) and leaves
/// the captured volume itself out; the matcher never probes anything.
/// </summary>
internal sealed record MountedVolume(string? FsType, uint? Serial32, ulong? Serial64, string? Label, long? CapacityBytes);

/// <summary>
/// What the matching algorithm (§7.5) asks of the Library: read-only lookups shaped like the schema's indexes
/// (<c>volume_by_serial</c>, <c>source_local_key</c>, <c>source_network_key</c>). The Library implements this over SQLite
/// in a later gate; <see cref="InMemoryIdentityStore"/> implements it for C3. Lookups never fail to find and never create.
/// </summary>
internal interface IIdentityCandidates
{
    /// <summary>Volumes with this filesystem type and 64-bit serial. Several mean clones that were both saved before.</summary>
    IReadOnlyList<VolumeCandidate> FindVolumesBySerial64(string fsType, ulong serial64);

    /// <summary>Volumes with this filesystem type and 32-bit serial.</summary>
    IReadOnlyList<VolumeCandidate> FindVolumesBySerial32(string fsType, uint serial32);

    /// <summary>One volume by key, or null.</summary>
    VolumeCandidate? GetVolume(long volumeId);

    /// <summary>Every local source on a volume. At most one has any given exact root.</summary>
    IReadOnlyList<SourceCandidate> SourcesOnVolume(long volumeId);

    /// <summary>Every local source (the candidates a PathOnly local capture may be explicitly attached to).</summary>
    IReadOnlyList<SourceCandidate> LocalSources();

    /// <summary>Every network source of the share whose comparison key is given. At most one has any given exact root.</summary>
    IReadOnlyList<SourceCandidate> NetworkSourcesWithKey(string networkRootKey);
}

/// <summary>
/// The comparison candidate key (NAME-03) as a seam. It is used only to SUGGEST matches (two roots that differ only under
/// the key, §7.4) and to key a network share's name; it never proves anything and never merges a source on its own.
/// C3 does not implement it: the table-driven Unicode key is gate C7's, and History will take that implementation as it
/// becomes available. Until then callers (and C3's tests) inject their own, and this assembly contains no case-folding or
/// normalisation of any kind.
/// </summary>
internal interface IComparisonKey
{
    /// <summary>The key of a name or path. Equal keys mean "could be the same name"; they are compared by UTF-16 code units.</summary>
    string KeyOf(string text);
}

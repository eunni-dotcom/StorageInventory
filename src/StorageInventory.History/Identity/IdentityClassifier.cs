using StorageInventory.Core.Identity;

namespace StorageInventory.History.Identity;

/// <summary>Why a source's confidence is not Strong. The matching layer carries these so the later UI can explain a reduced
/// confidence ("recognised by location only: this is a network share") without re-deriving it.</summary>
internal enum ConfidenceReasonKind
{
    /// <summary>A network source (ID-01: PathOnly, matched by location).</summary>
    NetworkSource,

    /// <summary>The filesystem name could not be obtained (the call failed, or the source reported none).</summary>
    FileSystemNameUnavailable,

    /// <summary>A filesystem that is neither NTFS or ReFS nor FAT, FAT32 or exFAT: UDF and optical media, or an unknown
    /// filesystem. <see cref="ConfidenceReason.Detail"/> names it.</summary>
    FileSystemNotEligible,

    /// <summary>NTFS or ReFS, but the 64-bit serial could not be obtained (<c>FileIdInfo</c> failed or is not provided).</summary>
    Serial64Unavailable,

    /// <summary>NTFS or ReFS, and the 64-bit serial is zero.</summary>
    Serial64Zero,

    /// <summary>FAT, FAT32 or exFAT, but the 32-bit serial could not be obtained.</summary>
    Serial32Unavailable,

    /// <summary>FAT, FAT32 or exFAT, and the 32-bit serial is zero.</summary>
    Serial32Zero,
}

/// <param name="Kind">What reduced the confidence.</param>
/// <param name="Detail">A short fact for display (the filesystem name, for <see cref="ConfidenceReasonKind.FileSystemNotEligible"/>).</param>
/// <param name="Failure">The evidence item whose call failed, when that is the reason.</param>
internal sealed record ConfidenceReason(ConfidenceReasonKind Kind, string? Detail = null, MissingEvidence? Failure = null);

/// <summary>
/// What the identity layer knows about a capture's source from its preflight reading E0: the kind, the confidence and
/// the values the matching rules use, with the reasons confidence is reduced and whether the source can be saved at all.
/// Mutable information (label, capacity) is carried for matching and display only; it is never part of identity.
/// </summary>
/// <param name="Kind">Local volume or network, from the resolved canonical path (a mapped network drive is network).</param>
/// <param name="Confidence">Strong, Moderate or PathOnly (ID-01).</param>
/// <param name="FileSystemName">As reported; null when it could not be obtained.</param>
/// <param name="Serial32">The 32-bit serial as read (0 is kept as 0 but never matched on); null when unavailable.</param>
/// <param name="Serial64">The 64-bit serial as read (0 is kept as 0 but never matched on); null when unavailable.</param>
/// <param name="VolumeLabel">Exact label; null when unavailable.</param>
/// <param name="CapacityBytes">Capacity; null when unavailable.</param>
/// <param name="NetworkRoot">For network sources the exact canonical <c>\\server\share</c>; otherwise null.</param>
/// <param name="RootInVolume">The root's exact path inside its volume or share (<c>\</c>, <c>\Media\Music</c>); null only when
/// the source cannot be saved.</param>
/// <param name="Reasons">Why the confidence is below Strong; empty for Strong.</param>
/// <param name="Minimum">ID-13: whether the source has the minimum evidence for re-verification.</param>
internal sealed record IdentityAssessment(
    SourceKind Kind,
    IdentityConfidence Confidence,
    string? FileSystemName,
    uint? Serial32,
    ulong? Serial64,
    string? VolumeLabel,
    long? CapacityBytes,
    string? NetworkRoot,
    string? RootInVolume,
    IReadOnlyList<ConfidenceReason> Reasons,
    MinimumEvidenceResult Minimum)
{
    /// <summary>False when Save to History is unavailable for this source (ID-13, O24): reports only.</summary>
    public bool CanSave => Minimum.Sufficient;

    /// <summary>The 32-bit serial if it is usable for matching: available and not zero.</summary>
    public uint? UsableSerial32 => Serial32 is > 0u ? Serial32 : null;

    /// <summary>The 64-bit serial if it is usable for matching: available and not zero.</summary>
    public ulong? UsableSerial64 => Serial64 is > 0ul ? Serial64 : null;
}

/// <summary>
/// ID-01: decides the confidence of a source from its preflight reading (§7.3). Confidence is keyed by the filesystem
/// NAME and the source kind, so evidence a filesystem happens to return never lifts it above its class: a FAT-family
/// volume is at best Moderate even when <c>FileIdInfo</c> hands back a 64-bit value.
/// </summary>
/// <remarks>
/// <para><b>"Any evidence call failed" means a call whose value the applicable rule needs.</b> Strong needs the filesystem
/// name and the 64-bit serial; Moderate the filesystem name and the 32-bit serial. If one of those could not be obtained
/// the source is PathOnly, with the failure kept as the reason. A failure of purely informational items (label, capacity,
/// free space, flags, mount point) does not change the level (a Moderate volume whose label or capacity is missing simply
/// cannot be corroborated, which the matcher answers by asking). Reading the sentence as "any call at all" would make
/// Moderate unreachable on every FAT volume where <c>FileIdInfo</c> is not provided, and would give an NTFS volume a new
/// source each time an informational query such as <c>GetDiskFreeSpaceExW</c> failed once.</para>
/// </remarks>
internal static class IdentityClassifier
{
    public static IdentityAssessment Assess(VolumeEvidence e0)
    {
        ArgumentNullException.ThrowIfNull(e0);
        var minimum = MinimumEvidence.Check(e0);

        SourceLocation? location = e0.CanonicalPath.IsAvailable && SourceLocation.TryDerive(e0.CanonicalPath.Value!, out var derived) ? derived : null;
        var kind = location?.Kind ?? e0.Kind;

        var fileSystem = e0.FileSystemName.IsAvailable ? e0.FileSystemName.Value : null;
        var reasons = new List<ConfidenceReason>();
        var confidence = Decide(kind, fileSystem, e0, reasons);

        return new IdentityAssessment(
            kind,
            confidence,
            fileSystem,
            e0.VolumeSerial32.IsAvailable ? e0.VolumeSerial32.Value : null,
            e0.VolumeSerial64.IsAvailable ? e0.VolumeSerial64.Value : null,
            e0.VolumeLabel.IsAvailable ? e0.VolumeLabel.Value : null,
            e0.CapacityBytes.IsAvailable ? e0.CapacityBytes.Value : null,
            location?.NetworkRoot,
            location?.RootInVolume,
            reasons,
            minimum);
    }

    private static IdentityConfidence Decide(SourceKind kind, string? fileSystem, VolumeEvidence e0, List<ConfidenceReason> reasons)
    {
        if (kind == SourceKind.Network)
        {
            reasons.Add(new ConfidenceReason(ConfidenceReasonKind.NetworkSource));
            return IdentityConfidence.PathOnly;
        }

        if (fileSystem is null)
        {
            reasons.Add(new ConfidenceReason(ConfidenceReasonKind.FileSystemNameUnavailable, null,
                MissingEvidence.Of(IdentityItem.FileSystemName, e0.FileSystemName)));
            return IdentityConfidence.PathOnly;
        }

        if (FileSystemNames.IsStrongCapable(fileSystem))
        {
            if (!e0.VolumeSerial64.IsAvailable)
            {
                reasons.Add(new ConfidenceReason(ConfidenceReasonKind.Serial64Unavailable, null,
                    MissingEvidence.Of(IdentityItem.VolumeSerial64, e0.VolumeSerial64)));
                return IdentityConfidence.PathOnly;
            }
            if (e0.VolumeSerial64.Value == 0)
            {
                reasons.Add(new ConfidenceReason(ConfidenceReasonKind.Serial64Zero));
                return IdentityConfidence.PathOnly;
            }
            return IdentityConfidence.Strong;
        }

        if (FileSystemNames.IsFatFamily(fileSystem))
        {
            if (!e0.VolumeSerial32.IsAvailable)
            {
                reasons.Add(new ConfidenceReason(ConfidenceReasonKind.Serial32Unavailable, null,
                    MissingEvidence.Of(IdentityItem.VolumeSerial32, e0.VolumeSerial32)));
                return IdentityConfidence.PathOnly;
            }
            if (e0.VolumeSerial32.Value == 0)
            {
                reasons.Add(new ConfidenceReason(ConfidenceReasonKind.Serial32Zero));
                return IdentityConfidence.PathOnly;
            }
            return IdentityConfidence.Moderate;
        }

        reasons.Add(new ConfidenceReason(ConfidenceReasonKind.FileSystemNotEligible, fileSystem));
        return IdentityConfidence.PathOnly;
    }
}

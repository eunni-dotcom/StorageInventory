using StorageInventory.Core.Identity;

namespace StorageInventory.History.Identity;

/// <summary>One identity item that was not obtained, with why (the status, the call, the Win32 error or the detail), so a
/// later gate can tell the user what Windows could not tell us.</summary>
internal sealed record MissingEvidence(IdentityItem Item, EvidenceStatus Status, string? Call, int Win32Error, string? Detail)
{
    internal static MissingEvidence Of<T>(IdentityItem item, EvidenceItem<T> evidence) =>
        new(item, evidence.Status, evidence.Call, evidence.Win32Error, evidence.Detail);
}

/// <summary>Whether a preflight reading contains the minimum evidence for end-of-capture re-verification (ID-13).</summary>
/// <param name="Sufficient">True when Save to History is available for this source as far as evidence goes.</param>
/// <param name="Missing">What is missing, empty when <paramref name="Sufficient"/>.</param>
internal sealed record MinimumEvidenceResult(bool Sufficient, IReadOnlyList<MissingEvidence> Missing);

/// <summary>
/// ID-13: the minimum re-verification evidence per source type. If the canonical path (required for EVERY source), or, for
/// a local volume, the filesystem name and the 32-bit serial, cannot be obtained at preflight (E0), Windows cannot provide
/// enough evidence to re-verify the source at the end of the scan, and Save to History is unavailable for it (O24): the
/// scan still runs with reports only. A capture never falls back to trusting the path.
/// </summary>
/// <remarks>
/// A zero serial IS obtained: it passes this rule (the equality of four zeros is still checked) and costs the source its
/// confidence instead (<see cref="IdentityClassifier"/>). The 64-bit serial and the root directory ID are never required
/// at E0; they become required at E1 to E3 only when <c>FileIdInfo</c> succeeded at E0 (<see cref="Reverification"/>).
/// One conservative addition: a canonical path that has neither a drive-letter nor a UNC form cannot be split into a root
/// inside a volume or share, so no source key can be formed and the item counts as missing.
/// </remarks>
internal static class MinimumEvidence
{
    public static MinimumEvidenceResult Check(VolumeEvidence e0)
    {
        ArgumentNullException.ThrowIfNull(e0);
        var missing = new List<MissingEvidence>();

        if (!e0.CanonicalPath.IsAvailable)
        {
            missing.Add(MissingEvidence.Of(IdentityItem.CanonicalPath, e0.CanonicalPath));
        }
        else if (!SourceLocation.TryDerive(e0.CanonicalPath.Value!, out _))
        {
            missing.Add(new MissingEvidence(IdentityItem.CanonicalPath, EvidenceStatus.Unavailable, e0.CanonicalPath.Call, 0,
                "the canonical path has neither a drive-letter nor a UNC form, so the root inside its volume cannot be determined"));
        }

        if (e0.Kind == SourceKind.LocalVolume)
        {
            if (!e0.FileSystemName.IsAvailable) missing.Add(MissingEvidence.Of(IdentityItem.FileSystemName, e0.FileSystemName));
            if (!e0.VolumeSerial32.IsAvailable) missing.Add(MissingEvidence.Of(IdentityItem.VolumeSerial32, e0.VolumeSerial32));
        }

        return new MinimumEvidenceResult(missing.Count == 0, missing);
    }
}

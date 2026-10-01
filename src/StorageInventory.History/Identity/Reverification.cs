using StorageInventory.Core.Identity;

namespace StorageInventory.History.Identity;

/// <summary>The verdict of end-of-capture re-verification (ID-10).</summary>
internal enum ReverificationOutcome
{
    /// <summary>E0, E1, E2 and E3 provide every required identity item and are equal on each: the scan finished observing
    /// the same source namespace it began observing, through the path it enumerated. Identity does not block saving.</summary>
    Verified,

    /// <summary>E0 lacks the minimum evidence of ID-13, so this source can never be re-verified: Save to History is
    /// unavailable for it, decided at preflight, before the scan starts (O24). The reports are unaffected.</summary>
    SaveUnavailable,

    /// <summary>A required item is missing from E1, E2 or E3, or differs from E0: the capture is
    /// <c>NotEligible (IdentityChangedDuringScan)</c> (103, O11). The reports stay valid; nothing is published.</summary>
    IdentityChangedDuringScan,
}

internal enum ReverificationFailureKind
{
    /// <summary>The item was obtained at E0 but could not be obtained in this reading (the handle was invalidated, the path
    /// could not be opened, a call failed).</summary>
    Missing,

    /// <summary>The item was obtained but differs from E0.</summary>
    Different,
}

/// <summary>One reason a capture is not eligible.</summary>
/// <param name="Stage">Which reading (E1, E2 or E3).</param>
/// <param name="Item">Which identity item.</param>
/// <param name="Kind">Missing or different.</param>
/// <param name="Expected">The E0 value, as text (for diagnostics, never for display wording).</param>
/// <param name="Actual">This reading's value as text; null when it is missing.</param>
/// <param name="Missing">For <see cref="ReverificationFailureKind.Missing"/>, why the item could not be read.</param>
internal sealed record ReverificationFailure(EvidenceStage Stage, IdentityItem Item, ReverificationFailureKind Kind, string? Expected,
    string? Actual, MissingEvidence? Missing);

/// <summary>The result of <see cref="Reverification.Evaluate"/>.</summary>
/// <param name="Outcome">The verdict.</param>
/// <param name="MinimumMissing">For <see cref="ReverificationOutcome.SaveUnavailable"/>, what E0 lacked.</param>
/// <param name="Failures">For <see cref="ReverificationOutcome.IdentityChangedDuringScan"/>, every difference found, in
/// reading order (E1, E2, E3) and item order.</param>
internal sealed record ReverificationResult(ReverificationOutcome Outcome, IReadOnlyList<MissingEvidence> MinimumMissing,
    IReadOnlyList<ReverificationFailure> Failures)
{
    /// <summary>True only for <see cref="ReverificationOutcome.Verified"/>.</summary>
    public bool Eligible => Outcome == ReverificationOutcome.Verified;
}

/// <summary>
/// ID-10 and ID-13 as a pure function over the four readings of a capture. The question it answers is: <b>did the scan
/// finish observing the same source namespace it began observing, through the path it enumerated?</b>, not "what does this
/// path point to now?".
/// </summary>
/// <remarks>
/// <para>The required items are exactly those of ID-13, decided by E0: the canonical path of the opened object (every
/// source); for a local volume the filesystem name and the 32-bit serial (their absence at E0 is
/// <see cref="ReverificationOutcome.SaveUnavailable"/>); the 64-bit serial and the root directory's file ID when
/// <c>FileIdInfo</c> succeeded at E0; and, for a network source, the filesystem name and serial when the provider returned
/// them at E0. Each of them must be obtained and equal (UTF-16 code units for text) at E1, E2 and E3. An item that E0
/// could not obtain is not required later, whatever a later reading shows. The source kind must not change either.</para>
/// <para><b>Never compared:</b> label, capacity, free space, flags and the mount point. They change legitimately (a label
/// is renamed, a file is written elsewhere on the volume), and none of them is identity.</para>
/// <para><b>Not detectable, and not pretended to be</b> (limitation L-ID2): a change that is UNDONE before the window closes
/// leaves four equal readings. Two cases are documented. A letter (a drive letter, SUBST or a mapped letter) re-pointed
/// away and back between E1 and the end, while the original volume stays mounted. And, the same limitation applied to the
/// source folder itself (G0F-O05), the source folder renamed away, another folder given its name, and both put back. In both
/// the scan may have listed a different namespace in between. A replacement that is left in place is detected: the held
/// handle then reports a different canonical path (E2) and a fresh open reaches a different folder (E3).</para>
/// </remarks>
internal static class Reverification
{
    public static ReverificationResult Evaluate(VolumeEvidence e0, VolumeEvidence e1, VolumeEvidence e2, VolumeEvidence e3)
    {
        Require(e0, EvidenceStage.E0Preflight, nameof(e0));
        Require(e1, EvidenceStage.E1WindowStart, nameof(e1));
        Require(e2, EvidenceStage.E2WindowEndHeld, nameof(e2));
        Require(e3, EvidenceStage.E3WindowEndFresh, nameof(e3));
        foreach (var later in new[] { e1, e2, e3 })
        {
            if (!string.Equals(later.EnumeratedPath, e0.EnumeratedPath, StringComparison.Ordinal))
            {
                throw new ArgumentException($"The {later.Stage} reading was taken on '{later.EnumeratedPath}', not on the enumerated path '{e0.EnumeratedPath}' that E0 used. All four readings must use the path the scanner enumerates (ID-10).");
            }
        }

        var minimum = MinimumEvidence.Check(e0);
        if (!minimum.Sufficient) return new ReverificationResult(ReverificationOutcome.SaveUnavailable, minimum.Missing, []);

        var failures = new List<ReverificationFailure>();
        foreach (var later in new[] { e1, e2, e3 }) Compare(e0, later, failures);
        return new ReverificationResult(failures.Count == 0 ? ReverificationOutcome.Verified : ReverificationOutcome.IdentityChangedDuringScan, [], failures);
    }

    private static void Compare(VolumeEvidence e0, VolumeEvidence later, List<ReverificationFailure> failures)
    {
        if (later.ResolvedKind != e0.ResolvedKind)
        {
            failures.Add(new ReverificationFailure(later.Stage, IdentityItem.SourceKind, ReverificationFailureKind.Different, e0.ResolvedKind.ToString(), later.ResolvedKind.ToString(), null));
        }

        Check(IdentityItem.CanonicalPath, e0.CanonicalPath, later.CanonicalPath, later.Stage, failures);
        Check(IdentityItem.FileSystemName, e0.FileSystemName, later.FileSystemName, later.Stage, failures);
        Check(IdentityItem.VolumeSerial32, e0.VolumeSerial32, later.VolumeSerial32, later.Stage, failures);
        Check(IdentityItem.VolumeSerial64, e0.VolumeSerial64, later.VolumeSerial64, later.Stage, failures);
        Check(IdentityItem.RootDirectoryFileId, e0.RootDirectoryFileId, later.RootDirectoryFileId, later.Stage, failures);
    }

    /// <summary>An item obtained at E0 is required, and equal, in every later reading. An item E0 did not obtain is not
    /// required (ID-13 items 3 and 4: "when it succeeds at E0").</summary>
    private static void Check<T>(IdentityItem item, EvidenceItem<T> baseline, EvidenceItem<T> reading, EvidenceStage stage, List<ReverificationFailure> failures)
    {
        if (!baseline.IsAvailable) return;

        if (!reading.IsAvailable)
        {
            failures.Add(new ReverificationFailure(stage, item, ReverificationFailureKind.Missing, $"{baseline.Value}", null, MissingEvidence.Of(item, reading)));
            return;
        }

        if (!EqualityComparer<T>.Default.Equals(baseline.Value, reading.Value))
        {
            failures.Add(new ReverificationFailure(stage, item, ReverificationFailureKind.Different, $"{baseline.Value}", $"{reading.Value}", null));
        }
    }

    private static void Require(VolumeEvidence reading, EvidenceStage expected, string name)
    {
        ArgumentNullException.ThrowIfNull(reading, name);
        if (reading.Stage != expected) throw new ArgumentException($"{name} must be the {expected} reading but is {reading.Stage}.", name);
    }
}

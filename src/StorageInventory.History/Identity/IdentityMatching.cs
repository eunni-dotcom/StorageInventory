using StorageInventory.Core.Identity;

namespace StorageInventory.History.Identity;

/// <summary>
/// The identity matching algorithm (§7.5; ID-01 to ID-06 and ID-12) as pure functions: the same inputs always give the same
/// outcome, nothing is read from the filesystem, the clock or the process, and nothing is written. The Library's rows
/// arrive through <see cref="IIdentityCandidates"/>; the comparison key arrives through <see cref="IComparisonKey"/>.
/// </summary>
/// <remarks>
/// <para><b>Never silent on weak evidence (ID-06).</b> A capture is attached automatically only on Strong evidence, on
/// Moderate evidence that fully corroborates exactly one volume, or on a network location. Everything else asks, creates a
/// new source, or needs an explicit answer. Local PathOnly captures never match automatically. A drive letter and a mount
/// point are not inputs at all (ID-03).</para>
/// <para><b>Not heuristics.</b> The root directory's file ID, the capacity of a Strong volume and the drive letter play no
/// part in matching: they were considered and rejected (ID-05, §7.2).</para>
/// </remarks>
internal static class IdentityMatching
{
    /// <summary>ID-01: Moderate corroboration needs the capacity within this many percent of the recorded one (inclusive).</summary>
    internal const int CapacityTolerancePercent = 1;

    /// <summary>
    /// Step 1 to 5 of §7.5 for one capture.
    /// </summary>
    /// <param name="capture">The classified preflight reading (<see cref="IdentityClassifier.Assess"/>).</param>
    /// <param name="store">The Library's volumes and sources.</param>
    /// <param name="key">The comparison candidate key, used only to suggest case-variant roots and to key a network share.</param>
    /// <param name="otherMountedVolumes">The OTHER volumes mounted right now, if the caller knows them (ID-05); never probed here.</param>
    public static MatchOutcome Match(IdentityAssessment capture, IIdentityCandidates store, IComparisonKey key,
        IReadOnlyList<MountedVolume>? otherMountedVolumes = null)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(key);

        // ID-13: without the minimum evidence the source can never be re-verified, so it cannot be saved. Matching is moot.
        if (!capture.CanSave) return new MatchOutcome.SaveUnavailable(capture.Minimum);

        var others = otherMountedVolumes ?? [];
        if (capture.Kind == SourceKind.Network) return MatchNetwork(capture, store, key);
        return capture.Confidence switch
        {
            IdentityConfidence.Strong => MatchStrong(capture, store, key, others),
            IdentityConfidence.Moderate => MatchModerate(capture, store, key, others),
            _ => MatchPathOnlyLocal(capture, store, key),
        };
    }

    /// <summary>
    /// Turns the user's answer to a prompt into the next outcome (ID-04). "Yes, the same" attaches with basis
    /// <see cref="IdentityBasis.UserAsserted"/> (the evidence alone was not enough); "No, a different drive or folder" creates
    /// the prompt's new source; "Don't save" decides nothing is saved. A volume answer continues with the root step, which can
    /// itself end in a case-variant question.
    /// </summary>
    /// <exception cref="ArgumentException">The answer names a candidate the prompt did not offer.</exception>
    public static MatchOutcome Answer(IdentityPrompt prompt, IdentityAnswer answer, IdentityAssessment capture, IIdentityCandidates store, IComparisonKey key)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(key);

        switch (answer)
        {
            case IdentityAnswer.SkipSaving:
                return new MatchOutcome.Decided(new IdentityDecision.DoNotSave());

            case IdentityAnswer.DifferentSource:
                return new MatchOutcome.Decided(prompt.IfDifferent);

            case IdentityAnswer.SameSource same:
                if (!prompt.Sources.Contains(same.Source)) throw new ArgumentException("That source was not one of the prompt's candidates.", nameof(answer));
                return new MatchOutcome.Decided(new IdentityDecision.AttachToSource(same.Source, capture.Confidence, IdentityBasis.UserAsserted));

            case IdentityAnswer.SameVolume same:
                if (!prompt.Volumes.Contains(same.Volume)) throw new ArgumentException("That volume was not one of the prompt's candidates.", nameof(answer));
                return ResolveRoot(capture, same.Volume.VolumeId, null, store.SourcesOnVolume(same.Volume.VolumeId), key, IdentityBasis.UserAsserted);

            default:
                throw new ArgumentException("Unknown answer.", nameof(answer));
        }
    }

    // ---- Strong (ID-01, §7.5 step 2) ----

    private static MatchOutcome MatchStrong(IdentityAssessment capture, IIdentityCandidates store, IComparisonKey key, IReadOnlyList<MountedVolume> others)
    {
        var volumes = store.FindVolumesBySerial64(capture.FileSystemName!, capture.UsableSerial64!.Value);
        if (volumes.Count == 0) return new MatchOutcome.Decided(NewLocalSource(capture, null, IdentityBasis.Evidence));
        if (volumes.Count > 1) return Ask(PromptReason.AmbiguousVolumes, volumes, [], NewLocalSource(capture, null, IdentityBasis.Evidence));
        return OnMatchedVolume(capture, volumes[0], store, key, others);
    }

    // ---- Moderate (ID-01, §7.5 step 3) ----

    private static MatchOutcome MatchModerate(IdentityAssessment capture, IIdentityCandidates store, IComparisonKey key, IReadOnlyList<MountedVolume> others)
    {
        var volumes = store.FindVolumesBySerial32(capture.FileSystemName!, capture.UsableSerial32!.Value);
        if (volumes.Count == 0) return new MatchOutcome.Decided(NewLocalSource(capture, null, IdentityBasis.Evidence));

        var corroborated = volumes.Where(v => Corroborates(v, capture)).ToList();
        if (corroborated.Count == 1) return OnMatchedVolume(capture, corroborated[0], store, key, others);

        // Corroboration failed (no volume, or more than one, satisfies filesystem, serial, label and capacity together).
        return Ask(PromptReason.ModerateNotCorroborated, volumes, [], NewLocalSource(capture, null, IdentityBasis.Evidence));
    }

    /// <summary>The volume is settled by evidence. If another mounted volume reports the same identity the user is asked
    /// (ID-05); otherwise the exact root decides (step 5).</summary>
    private static MatchOutcome OnMatchedVolume(IdentityAssessment capture, VolumeCandidate volume, IIdentityCandidates store, IComparisonKey key,
        IReadOnlyList<MountedVolume> others)
    {
        if (others.Any(m => SameIdentity(capture, m)))
        {
            return Ask(PromptReason.CoMountedClones, [volume], [], NewLocalSource(capture, null, IdentityBasis.Evidence));
        }
        return ResolveRoot(capture, volume.VolumeId, null, store.SourcesOnVolume(volume.VolumeId), key, IdentityBasis.Evidence);
    }

    // ---- PathOnly (ID-01, §7.5 step 4) ----

    private static MatchOutcome MatchNetwork(IdentityAssessment capture, IIdentityCandidates store, IComparisonKey key)
    {
        // A network source is its location: (comparison key of \\server\share, exact root inside the share). Different
        // spellings of a server are different sources; no name is resolved.
        var shareKey = key.KeyOf(capture.NetworkRoot!);
        return ResolveRoot(capture, null, shareKey, store.NetworkSourcesWithKey(shareKey), key, IdentityBasis.Location);
    }

    private static MatchOutcome MatchPathOnlyLocal(IdentityAssessment capture, IIdentityCandidates store, IComparisonKey key)
    {
        // Never automatic. The candidates for an explicit pick are the existing local sources this capture could be: the
        // same root (exactly or apart from letter case), and the same filesystem when this capture's is known. No serial,
        // label or capacity is consulted: that would be inventing a heuristic where the specification asks for the user.
        var root = capture.RootInVolume!;
        var rootKey = key.KeyOf(root);
        var candidates = new List<SourceCandidate>();
        foreach (var source in store.LocalSources())
        {
            if (!SameRoot(source.RootInVolume, root, rootKey, key)) continue;
            var volume = source.VolumeId is { } id ? store.GetVolume(id) : null;
            if (capture.FileSystemName is not null && volume is not null && !FileSystemNames.Same(volume.FsType, capture.FileSystemName)) continue;
            candidates.Add(source);
        }

        var asNew = NewLocalSource(capture, null, IdentityBasis.Location);
        return candidates.Count == 0
            ? new MatchOutcome.Decided(asNew)
            : Ask(PromptReason.PathOnlyMayBelong, [], candidates, asNew);
    }

    // ---- the root inside the volume or share (§7.5 step 5, ID-02, ID-12) ----

    /// <summary>
    /// Finds the source with the same exact root among <paramref name="sources"/> (those of the matched volume, or of the
    /// network share). Exact: UTF-16 code units, no case folding, so <c>\Media</c> and <c>\media</c> are two sources. A root
    /// that differs only under the comparison key is SUGGESTED, never merged. A found source is attached automatically only
    /// when this capture's evidence is not weaker than the source's own (ID-12) or the user has already confirmed.
    /// </summary>
    private static MatchOutcome ResolveRoot(IdentityAssessment capture, long? volumeId, string? networkRootKey, IReadOnlyList<SourceCandidate> sources,
        IComparisonKey key, IdentityBasis basis)
    {
        var root = capture.RootInVolume!;
        var create = new IdentityDecision.CreateSource(capture.Kind, volumeId, networkRootKey, root, capture.Confidence, basis);

        var exact = sources.Where(s => string.Equals(s.RootInVolume, root, StringComparison.Ordinal)).ToList();
        if (exact.Count > 1) throw new InvalidOperationException("The candidate store returned two sources with the same exact root; the schema's unique indexes make that impossible.");
        if (exact.Count == 1)
        {
            var source = exact[0];
            if (basis != IdentityBasis.UserAsserted && IsWeaker(capture.Confidence, source.Confidence))
            {
                // ID-12: never attached automatically; the user decides. "No" starts a fresh source rather than adding a
                // weaker capture's source to a stronger volume: a new volume for a local capture, the share itself for a
                // network one.
                var fresh = capture.Kind == SourceKind.Network
                    ? new IdentityDecision.CreateSource(SourceKind.Network, null, networkRootKey, root, capture.Confidence, IdentityBasis.Location)
                    : NewLocalSource(capture, null, DefaultBasis(capture));
                return Ask(PromptReason.WeakerThanSource, [], [source], fresh);
            }
            return new MatchOutcome.Decided(new IdentityDecision.AttachToSource(source, capture.Confidence, basis));
        }

        var rootKey = key.KeyOf(root);
        var variants = sources.Where(s => SameRoot(s.RootInVolume, root, rootKey, key)).ToList();
        return variants.Count == 0
            ? new MatchOutcome.Decided(create)
            : Ask(PromptReason.CaseVariantRoot, [], variants, create);
    }

    // ---- helpers ----

    /// <summary>The new local source of a capture, on an existing volume or (null) a new one. Its basis is fixed here, for good.</summary>
    private static IdentityDecision.CreateSource NewLocalSource(IdentityAssessment capture, long? existingVolumeId, IdentityBasis basis) =>
        new(SourceKind.LocalVolume, existingVolumeId, null, capture.RootInVolume!, capture.Confidence, basis);

    /// <summary>What a fresh source of this capture rests on when nobody has confirmed anything: the hardware evidence for
    /// Strong and Moderate, otherwise only where it was seen.</summary>
    private static IdentityBasis DefaultBasis(IdentityAssessment capture) =>
        capture.Kind == SourceKind.Network || capture.Confidence == IdentityConfidence.PathOnly ? IdentityBasis.Location : IdentityBasis.Evidence;

    private static MatchOutcome Ask(PromptReason reason, IReadOnlyList<VolumeCandidate> volumes, IReadOnlyList<SourceCandidate> sources,
        IdentityDecision.CreateSource ifDifferent) =>
        new MatchOutcome.Ask(new IdentityPrompt(reason, volumes, sources, ifDifferent));

    /// <summary>The codes run from strong (1) to weak (3), so a larger code is weaker evidence.</summary>
    private static bool IsWeaker(IdentityConfidence capture, IdentityConfidence source) => capture > source;

    /// <summary>Equal exactly, or equal under the comparison key (the suggestion case).</summary>
    private static bool SameRoot(string candidateRoot, string root, string rootKey, IComparisonKey key) =>
        string.Equals(candidateRoot, root, StringComparison.Ordinal) || string.Equals(key.KeyOf(candidateRoot), rootKey, StringComparison.Ordinal);

    /// <summary>
    /// Full Moderate corroboration (ID-01): this volume already matches on filesystem and 32-bit serial (the lookup), and its
    /// label is the same and its capacity is within 1%. A label or capacity that is unknown on either side cannot corroborate.
    /// </summary>
    private static bool Corroborates(VolumeCandidate volume, IdentityAssessment capture) =>
        SameLabel(volume.Label, capture.VolumeLabel) && CapacityWithinTolerance(volume.CapacityBytes, capture.CapacityBytes);

    /// <summary>Another mounted volume has the capture's identity: Strong is (filesystem, 64-bit serial); Moderate is the full
    /// corroboration set (filesystem, 32-bit serial, label, capacity within 1%). PathOnly has no identity to share.</summary>
    private static bool SameIdentity(IdentityAssessment capture, MountedVolume other)
    {
        if (!FileSystemNames.Same(other.FsType, capture.FileSystemName)) return false;
        return capture.Confidence switch
        {
            IdentityConfidence.Strong => capture.UsableSerial64 is { } s64 && other.Serial64 == s64,
            IdentityConfidence.Moderate => capture.UsableSerial32 is { } s32 && other.Serial32 == s32
                && SameLabel(other.Label, capture.VolumeLabel) && CapacityWithinTolerance(other.CapacityBytes, capture.CapacityBytes),
            _ => false,
        };
    }

    /// <summary>Exact label equality (UTF-16 code units). Both must be known; an empty label equals an empty label.</summary>
    private static bool SameLabel(string? recorded, string? observed) => recorded is not null && observed is not null && string.Equals(recorded, observed, StringComparison.Ordinal);

    /// <summary>
    /// ID-01's "capacity within ±1%": the difference is at most 1% of the RECORDED capacity, inclusive, in exact integer
    /// arithmetic (<c>|observed − recorded| × 100 ≤ recorded</c>). Both capacities must be known and positive.
    /// </summary>
    internal static bool CapacityWithinTolerance(long? recorded, long? observed)
    {
        if (recorded is not > 0 || observed is not > 0) return false;
        var difference = (Int128)Math.Abs(observed.Value - recorded.Value);
        return difference * 100 <= recorded.Value * (Int128)CapacityTolerancePercent;
    }
}

using StorageInventory.Core.Identity;

namespace StorageInventory.History.Identity;

/// <summary>What the capture is attached to, once the identity question is settled. A decision changes nothing by itself:
/// the Library applies it inside T-IMPORT, which re-reads the chosen rows under the write lock (§7.5 step 6). No text here is
/// user-facing; the UI words these in a later gate.</summary>
internal abstract record IdentityDecision
{
    private IdentityDecision()
    {
    }

    /// <summary>The capture belongs to this existing source (and so to its volume).</summary>
    /// <param name="Source">The source.</param>
    /// <param name="CaptureConfidence">The level of THIS capture's evidence (recorded on the snapshot; the source keeps its own).</param>
    /// <param name="Basis">What the attachment rested on: <see cref="IdentityBasis.Evidence"/> for Strong evidence or full
    /// Moderate corroboration, <see cref="IdentityBasis.Location"/> for a network location,
    /// <see cref="IdentityBasis.UserAsserted"/> for an explicit answer.</param>
    internal sealed record AttachToSource(SourceCandidate Source, IdentityConfidence CaptureConfidence, IdentityBasis Basis) : IdentityDecision;

    /// <summary>A new source is created (in T-IMPORT, never before). Its confidence and basis are fixed from here on (ID-12).</summary>
    /// <param name="Kind">Local volume or network.</param>
    /// <param name="ExistingVolumeId">The volume the source goes on; null means a new volume row is created too (local only).</param>
    /// <param name="NetworkRootKey">For a network source, the comparison key of its canonical <c>\\server\share</c>.</param>
    /// <param name="RootInVolume">The root's exact path inside its volume or share.</param>
    /// <param name="Confidence">The source's confidence, fixed at creation.</param>
    /// <param name="Basis">The source's basis, fixed at creation.</param>
    internal sealed record CreateSource(SourceKind Kind, long? ExistingVolumeId, string? NetworkRootKey, string RootInVolume,
        IdentityConfidence Confidence, IdentityBasis Basis) : IdentityDecision;

    /// <summary>"Don't save this scan": the scan runs with reports only and no attempt is recorded (O25).</summary>
    internal sealed record DoNotSave : IdentityDecision;
}

/// <summary>Why the user is asked (ID-04). The prompt is shown BEFORE the scan starts; the answer never changes a stored row
/// until T-IMPORT.</summary>
internal enum PromptReason
{
    /// <summary>Several volumes share the capture's Strong identity (clones that were both saved before, ID-05).</summary>
    AmbiguousVolumes = 1,

    /// <summary>Volumes share the capture's filesystem and serial, but label and capacity do not corroborate exactly one.</summary>
    ModerateNotCorroborated = 2,

    /// <summary>Another volume that is mounted right now reports the same identity: a clone beside its original (ID-05).</summary>
    CoMountedClones = 3,

    /// <summary>A saved source has this root apart from letter case (§7.4): a suggestion only, nothing merges by itself.</summary>
    CaseVariantRoot = 4,

    /// <summary>A PathOnly local capture could belong to an existing source. It is never attached automatically.</summary>
    PathOnlyMayBelong = 5,

    /// <summary>The matched source was created from stronger evidence than this capture has; never attached silently (ID-12).</summary>
    WeakerThanSource = 6,
}

/// <summary>The question of ID-04, as data.</summary>
/// <param name="Reason">Why it is asked.</param>
/// <param name="Volumes">The candidate volumes, for the volume questions (<see cref="PromptReason.AmbiguousVolumes"/>,
/// <see cref="PromptReason.ModerateNotCorroborated"/>, <see cref="PromptReason.CoMountedClones"/>); otherwise empty.</param>
/// <param name="Sources">The candidate sources, for the source questions; otherwise empty.</param>
/// <param name="IfDifferent">What "No, a different drive or folder" creates.</param>
internal sealed record IdentityPrompt(PromptReason Reason, IReadOnlyList<VolumeCandidate> Volumes, IReadOnlyList<SourceCandidate> Sources,
    IdentityDecision.CreateSource IfDifferent);

/// <summary>The user's answer to an <see cref="IdentityPrompt"/> (ID-04).</summary>
internal abstract record IdentityAnswer
{
    private IdentityAnswer()
    {
    }

    /// <summary>"Yes, the same" to a volume question: the capture is on this volume. The source is then found (or created) on
    /// it, with basis <see cref="IdentityBasis.UserAsserted"/>.</summary>
    internal sealed record SameVolume(VolumeCandidate Volume) : IdentityAnswer;

    /// <summary>"Yes, the same" to a source question, and the explicit pick of an existing source for a PathOnly local
    /// capture: the capture is attached with basis <see cref="IdentityBasis.UserAsserted"/>.</summary>
    internal sealed record SameSource(SourceCandidate Source) : IdentityAnswer;

    /// <summary>"No, a different drive or folder": the prompt's <see cref="IdentityPrompt.IfDifferent"/> source is created.</summary>
    internal sealed record DifferentSource : IdentityAnswer;

    /// <summary>"Don't save this scan".</summary>
    internal sealed record SkipSaving : IdentityAnswer;

    public static IdentityAnswer Different { get; } = new DifferentSource();

    public static IdentityAnswer Skip { get; } = new SkipSaving();
}

/// <summary>The outcome of matching a capture's identity against the Library (§7.5).</summary>
internal abstract record MatchOutcome
{
    private MatchOutcome()
    {
    }

    /// <summary>Nothing to ask: this is what the capture is attached to or creates.</summary>
    internal sealed record Decided(IdentityDecision Decision) : MatchOutcome;

    /// <summary>The user must answer before the scan starts (ID-04). <see cref="IdentityMatching.Answer"/> turns the answer
    /// into the next outcome (usually <see cref="Decided"/>; a volume answer can lead to a source question).</summary>
    internal sealed record Ask(IdentityPrompt Prompt) : MatchOutcome;

    /// <summary>Save to History is unavailable for this source because E0 lacks the minimum evidence (ID-13, O24). The scan
    /// can still run with reports only.</summary>
    internal sealed record SaveUnavailable(MinimumEvidenceResult Evidence) : MatchOutcome;
}

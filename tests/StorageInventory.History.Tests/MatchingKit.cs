using StorageInventory.History.Identity;
using StorageInventory.Testing;

namespace StorageInventory.History.Tests;

/// <summary>Assertions and scenario helpers shared by the matching tests.</summary>
internal static class Out
{
    public static IdentityDecision.AttachToSource Attached(MatchOutcome outcome) =>
        outcome is MatchOutcome.Decided { Decision: IdentityDecision.AttachToSource attach } ? attach : throw new AssertionException($"expected an automatic attach but was {Describe(outcome)}");

    public static IdentityDecision.CreateSource Created(MatchOutcome outcome) =>
        outcome is MatchOutcome.Decided { Decision: IdentityDecision.CreateSource create } ? create : throw new AssertionException($"expected a new source but was {Describe(outcome)}");

    public static IdentityPrompt Asked(MatchOutcome outcome) =>
        outcome is MatchOutcome.Ask ask ? ask.Prompt : throw new AssertionException($"expected the identity prompt but was {Describe(outcome)}");

    public static bool IsAutomaticAttach(MatchOutcome outcome) => outcome is MatchOutcome.Decided { Decision: IdentityDecision.AttachToSource };

    public static string Describe(MatchOutcome outcome) => outcome switch
    {
        MatchOutcome.Decided { Decision: IdentityDecision.AttachToSource a } => $"attach to source {a.Source.SourceId} ({a.Basis}, capture {a.CaptureConfidence})",
        MatchOutcome.Decided { Decision: IdentityDecision.CreateSource c } => $"create {c.Kind} source '{c.RootInVolume}' on volume {(c.ExistingVolumeId?.ToString() ?? "NEW")} ({c.Confidence}/{c.Basis})",
        MatchOutcome.Decided { Decision: IdentityDecision.DoNotSave } => "do not save",
        MatchOutcome.Ask a => $"ask ({a.Prompt.Reason}; {a.Prompt.Volumes.Count} volumes, {a.Prompt.Sources.Count} sources)",
        MatchOutcome.SaveUnavailable s => $"save unavailable ({string.Join(", ", s.Evidence.Missing.Select(m => m.Item))})",
        _ => outcome.ToString() ?? "?",
    };
}

/// <summary>Runs the capture path of §7.5 against a store: classify, match, and (when nothing needs asking) apply.</summary>
internal sealed class Scenario
{
    public InMemoryIdentityStore Store { get; } = new();
    public IComparisonKey Key { get; init; } = AsciiFoldKey.Instance;

    public IdentityAssessment Assess(Core.Identity.VolumeEvidence e0) => IdentityClassifier.Assess(e0);

    /// <summary>The matcher requires the mounted volumes; these helpers default to "no other volume is mounted" so the many
    /// tests that are not about clones need not say so.</summary>
    public MatchOutcome Match(Core.Identity.VolumeEvidence e0, IReadOnlyList<MountedVolume>? others = null) =>
        IdentityMatching.Match(IdentityClassifier.Assess(e0), Store, Key, others ?? []);

    /// <summary>A capture that the algorithm settles without asking: applied to the store, returning the rows.</summary>
    public AppliedIdentity Save(Core.Identity.VolumeEvidence e0, IReadOnlyList<MountedVolume>? others = null)
    {
        var capture = IdentityClassifier.Assess(e0);
        var outcome = IdentityMatching.Match(capture, Store, Key, others ?? []);
        if (outcome is not MatchOutcome.Decided decided) throw new AssertionException($"the scenario expected no prompt but got {Out.Describe(outcome)}");
        return Store.Apply(decided.Decision, capture) ?? throw new AssertionException("the decision saved nothing");
    }

    /// <summary>The capture goes through a prompt: asked, answered, and (when that settles it) applied.</summary>
    public AppliedIdentity SaveAnswering(Core.Identity.VolumeEvidence e0, Func<IdentityPrompt, IdentityAnswer> answer, IReadOnlyList<MountedVolume>? others = null)
    {
        var capture = IdentityClassifier.Assess(e0);
        var outcome = IdentityMatching.Match(capture, Store, Key, others ?? []);
        while (outcome is MatchOutcome.Ask ask) outcome = IdentityMatching.Answer(ask.Prompt, answer(ask.Prompt), capture, Store, Key);
        if (outcome is not MatchOutcome.Decided decided) throw new AssertionException($"expected a decision but got {Out.Describe(outcome)}");
        return Store.Apply(decided.Decision, capture) ?? throw new AssertionException("the decision saved nothing");
    }

    /// <summary>What a currently mounted volume with this reading looks like to the matcher (ID-05), its mount point read the
    /// way the capture's was.</summary>
    public static MountedVolume Mounted(Core.Identity.VolumeEvidence e) => MountedVolume.From(e);
}

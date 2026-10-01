using StorageInventory.Core.Identity;
using StorageInventory.History.Identity;
using StorageInventory.Testing;

namespace StorageInventory.History.Tests;

/// <summary>
/// C3 repair, finding C3-M02: the behaviour TEST-I3, ID-02, ID-05 and ID-13 state in words and the first C3 suite did not
/// pin. Each test names the independent mutant (T1 to T8, Appendix A of the C3 review) that survived without it and is killed
/// with it; <c>tests/mutation/Invoke-C3Mutants.ps1</c> re-runs them. No filesystem is involved.
/// </summary>
public static class IdentityGapTests
{
    private const ulong S1 = 0x1AFE5644FE561879;

    /// <summary>A comparison key that calls every name the same. A hostile or buggy key must only ever cost a question: it can
    /// suggest a candidate, it can never prove identity (§7.4).</summary>
    private sealed class EverythingIsEqualKey : IComparisonKey
    {
        public string KeyOf(string text) => string.Empty;
    }

    private static MountedVolume FatMounted(string fs = "exFAT", string mount = @"H:\", uint serial32 = 0xA1B2C3D4, string? label = "USBSTICK", long capacity = 31_000_000_000) =>
        Scenario.Mounted(Ev.Fat(fs, mount, serial32, label, capacity));

    // ---- ID-05, the Moderate arm of the co-mounted check (T1) and the filesystem comparison (T2) ----

    [Test]
    public static void A_Moderate_clone_mounted_beside_its_original_asks()
    {
        // ID-05: "If two currently mounted volumes report the same Strong or Moderate identity, StorageInventory asks".
        var s = new Scenario();
        var saved = s.Save(Ev.Fat("exFAT", @"E:\Photos"));
        var capture = Ev.Fat("exFAT", @"G:\Photos");

        var prompt = Out.Asked(s.Match(capture, [FatMounted("exFAT", @"H:\")]));
        Assert.Equal(PromptReason.CoMountedClones, prompt.Reason);
        Assert.SequenceEqual(new[] { saved.Volume!.VolumeId }, prompt.Volumes.Select(v => v.VolumeId));

        // the captured volume's own entry (same mount point) is not a clone
        Assert.Equal(saved.Source.SourceId, Out.Attached(s.Match(capture, [FatMounted("exFAT", @"G:\")])).Source.SourceId);
    }

    [Test]
    public static void A_mounted_volume_is_a_Moderate_clone_only_when_every_corroborating_item_matches()
    {
        var s = new Scenario();
        var saved = s.Save(Ev.Fat("exFAT", @"E:\Photos"));
        var capture = Ev.Fat("exFAT", @"G:\Photos");

        void AttachesDespite(string why, MountedVolume other) =>
            Assert.Equal(saved.Source.SourceId, Out.Attached(s.Match(capture, [other])).Source.SourceId, why);

        AttachesDespite("another 32-bit serial", FatMounted(serial32: 0x11112222));
        AttachesDespite("another label", FatMounted(label: "OTHER"));
        AttachesDespite("a label that differs only in case (labels compare exactly)", FatMounted(label: "usbstick"));
        AttachesDespite("an unknown label", FatMounted(label: null));
        AttachesDespite("a capacity 5% away", FatMounted(capacity: 32_550_000_000));
        AttachesDespite("an unknown capacity", FatMounted() with { CapacityBytes = null });

        // the capacity tolerance is the same 1% as the corroboration rule, inclusive
        Assert.Equal(PromptReason.CoMountedClones, Out.Asked(s.Match(capture, [FatMounted(capacity: 31_310_000_000)])).Reason, "exactly 1% away is still the same identity");
        Assert.Equal(PromptReason.CoMountedClones, Out.Asked(s.Match(capture, [FatMounted(capacity: 31_000_000_000)])).Reason, "all four items match");
    }

    [Test]
    public static void A_mounted_volume_with_another_filesystem_is_never_a_clone_of_either_strength()
    {
        // Moderate: the same serial, label and capacity on a FAT32 volume do not make an exFAT capture a clone
        var moderate = new Scenario();
        var saved = moderate.Save(Ev.Fat("exFAT", @"E:\Photos"));
        var sameNumbersOtherFilesystem = FatMounted("FAT32");
        Assert.Equal(saved.Source.SourceId, Out.Attached(moderate.Match(Ev.Fat("exFAT", @"G:\Photos"), [sameNumbersOtherFilesystem])).Source.SourceId, "Moderate: another filesystem is not a clone");

        // Strong: the same 64-bit serial on a ReFS volume does not make an NTFS capture a clone
        var strong = new Scenario();
        var savedStrong = strong.Save(Ev.Ntfs(@"E:\", S1));
        var refsSameSerial = Scenario.Mounted(Ev.Reading(@"F:\", @"F:\", "ReFS", unchecked((uint)S1), S1, "Data", 500_000_000_000, Ev.NtfsRoot));
        Assert.Equal(savedStrong.Source.SourceId, Out.Attached(strong.Match(Ev.Ntfs(@"E:\", S1), [refsSameSerial])).Source.SourceId, "Strong: another filesystem is not a clone");

        // the filesystem name is compared ignoring case, as everywhere else in matching
        var lowerCase = Scenario.Mounted(Ev.Reading(@"F:\", @"F:\", "ntfs", unchecked((uint)S1), S1, "Data", 500_000_000_000, Ev.NtfsRoot));
        Assert.Equal(PromptReason.CoMountedClones, Out.Asked(strong.Match(Ev.Ntfs(@"E:\", S1), [lowerCase])).Reason);
        Assert.Equal(PromptReason.CoMountedClones, Out.Asked(moderate.Match(Ev.Fat("exFAT", @"G:\Photos"), [FatMounted("EXFAT")])).Reason);

        // an entry whose filesystem is unknown is never counted
        var unknown = FatMounted() with { FsType = null };
        Assert.Equal(saved.Source.SourceId, Out.Attached(moderate.Match(Ev.Fat("exFAT", @"G:\Photos"), [unknown])).Source.SourceId);
    }

    // ---- ID-02, exact UTF-16 roots (T3) ----

    [Test]
    public static void Roots_are_compared_by_UTF_16_code_units_not_by_culture()
    {
        // Each pair is "equal" to a culture-sensitive comparer (an ignorable soft hyphen; a precomposed letter against its
        // decomposition) and different to ID-02. Both folders exist and are two sources.
        var pairs = new (string Saved, string Scanned, string Why)[]
        {
            (@"C:\Media", "C:\\Me\u00ADdia", "U+00AD is a different character"),
            ("C:\\Caf\u00E9", "C:\\Cafe\u0301", "precomposed and decomposed are different strings"),
            ("C:\\Korean\uAC00", "C:\\Korean\u1100\u1161", "a precomposed Hangul syllable and its jamo"),
        };
        foreach (var (saved, scanned, why) in pairs)
        {
            var s = new Scenario();
            var first = s.Save(Ev.Ntfs(saved, S1));
            var created = Out.Created(s.Match(Ev.Ntfs(scanned, S1)));
            Assert.Equal(first.Volume!.VolumeId, created.ExistingVolumeId, why + ": same volume");
            Assert.Equal(scanned[2..], created.RootInVolume, why + ": a new source with the exact scanned root");

            // and each one matches only itself
            var second = s.Save(Ev.Ntfs(scanned, S1));
            Assert.True(first.Source.SourceId != second.Source.SourceId, why + ": two sources");
            Assert.Equal(first.Source.SourceId, Out.Attached(s.Match(Ev.Ntfs(saved, S1))).Source.SourceId, why);
            Assert.Equal(second.Source.SourceId, Out.Attached(s.Match(Ev.Ntfs(scanned, S1))).Source.SourceId, why);
        }
    }

    [Test]
    public static void An_unpaired_surrogate_in_a_root_is_kept_and_compared_exactly()
    {
        var s = new Scenario();
        var high = s.Save(Ev.Ntfs("C:\\Media\\\uD800", S1));
        Assert.Equal("\\Media\\\uD800", high.Source.RootInVolume, "stored exactly, not replaced or dropped");
        Assert.Equal(high.Source.SourceId, Out.Attached(s.Match(Ev.Ntfs("C:\\Media\\\uD800", S1))).Source.SourceId);

        var low = Out.Created(s.Match(Ev.Ntfs("C:\\Media\\\uDC00", S1)));
        Assert.Equal("\\Media\\\uDC00", low.RootInVolume, "another lone surrogate is another root");
    }

    [Test]
    public static void A_key_that_calls_every_root_equal_can_only_make_the_matcher_ask_never_attach_a_local_root()
    {
        var s = new Scenario { Key = new EverythingIsEqualKey() };
        var media = s.Save(Ev.Ntfs(@"C:\Media", S1));

        // a different root is only SUGGESTED
        var prompt = Out.Asked(s.Match(Ev.Ntfs(@"C:\Docs", S1)));
        Assert.Equal(PromptReason.CaseVariantRoot, prompt.Reason);
        Assert.SequenceEqual(new[] { media.Source.SourceId }, prompt.Sources.Select(x => x.SourceId));
        Assert.Equal(@"\Docs", prompt.IfDifferent.RootInVolume);
        Assert.Equal(media.Volume!.VolumeId, prompt.IfDifferent.ExistingVolumeId);

        // the exact root still attaches, because the exact comparison does not consult the key
        Assert.Equal(media.Source.SourceId, Out.Attached(s.Match(Ev.Ntfs(@"C:\Media", S1))).Source.SourceId);

        // a PathOnly local capture is never attached either
        var udf = new Scenario { Key = new EverythingIsEqualKey() };
        udf.Save(Ev.Udf(@"D:\Photos"));
        Assert.Equal(PromptReason.PathOnlyMayBelong, Out.Asked(udf.Match(Ev.Udf(@"D:\Other"))).Reason);
    }

    // ---- ID-04, the user's volume answer (T4) ----

    [Test]
    public static void Answering_that_it_is_the_same_volume_creates_a_source_there_resting_on_the_users_word()
    {
        var s = new Scenario();
        s.Save(Ev.Ntfs(@"E:\Media", S1));
        var clone = s.Store.AddVolume("NTFS", S1, unchecked((uint)S1), IdentityConfidence.Strong, "Data", 500_000_000_000);
        var capture = s.Assess(Ev.Ntfs(@"E:\Docs", S1));
        var prompt = Out.Asked(IdentityMatching.Match(capture, s.Store, s.Key, []));
        Assert.Equal(PromptReason.AmbiguousVolumes, prompt.Reason);

        // the chosen volume has no source with this root: a new source there, whose basis is the user's assertion for good
        var created = Out.Created(IdentityMatching.Answer(prompt, new IdentityAnswer.SameVolume(clone), capture, s.Store, s.Key));
        Assert.Equal(clone.VolumeId, created.ExistingVolumeId);
        Assert.Equal(@"\Docs", created.RootInVolume);
        Assert.Equal(IdentityBasis.UserAsserted, created.Basis);
        Assert.Equal(IdentityConfidence.Strong, created.Confidence);
    }

    [Test]
    public static void A_volume_answer_can_lead_on_to_the_case_variant_question_and_keeps_the_users_basis()
    {
        var s = new Scenario();
        s.Save(Ev.Ntfs(@"E:\Media", S1));
        var clone = s.Store.AddVolume("NTFS", S1, unchecked((uint)S1), IdentityConfidence.Strong, "Data", 500_000_000_000);
        var variant = s.Store.AddLocalSource(clone.VolumeId, @"\media", IdentityConfidence.Strong, IdentityBasis.Evidence);
        var capture = s.Assess(Ev.Ntfs(@"E:\Media", S1));
        var prompt = Out.Asked(IdentityMatching.Match(capture, s.Store, s.Key, []));

        var next = Out.Asked(IdentityMatching.Answer(prompt, new IdentityAnswer.SameVolume(clone), capture, s.Store, s.Key));
        Assert.Equal(PromptReason.CaseVariantRoot, next.Reason);
        Assert.SequenceEqual(new[] { variant.SourceId }, next.Sources.Select(x => x.SourceId));
        Assert.Equal(clone.VolumeId, next.IfDifferent.ExistingVolumeId);
        Assert.Equal(IdentityBasis.UserAsserted, next.IfDifferent.Basis, "the volume was chosen by the user, so the source made on it rests on that");
    }

    // ---- ID-13 and ID-01, zero serials (T5) ----

    [Test]
    public static void A_zero_serial_is_evidence_that_was_obtained_so_the_source_can_still_be_saved()
    {
        var s = new Scenario();

        var zero32 = Ev.Fat(serial32: 0);
        Assert.True(MinimumEvidence.Check(zero32).Sufficient, "a zero 32-bit serial is a value (ID-13), not a missing item");
        Assert.True(s.Assess(zero32).CanSave);
        var created = Out.Created(s.Match(zero32));
        Assert.Equal(IdentityConfidence.PathOnly, created.Confidence, "it costs the source its confidence instead");
        Assert.Equal(IdentityBasis.Location, created.Basis);

        var zero64 = Ev.Reading(@"C:\Media", @"C:\Media", "NTFS", 0x1234, 0, "Data", 500_000_000_000, Ev.NtfsRoot);
        Assert.True(s.Assess(zero64).CanSave);
        Assert.Equal(IdentityConfidence.PathOnly, s.Assess(zero64).Confidence, "a zero 64-bit serial is never Strong");
        Assert.True(s.Match(zero64) is MatchOutcome.Decided, "and the capture is matched, not refused");
    }

    // ---- ID-01, labels (T6, T7) ----

    [Test]
    public static void The_Moderate_label_must_match_exactly_including_case()
    {
        var s = new Scenario();
        s.Save(Ev.Fat(label: "USBSTICK"));
        Assert.Equal(PromptReason.ModerateNotCorroborated, Out.Asked(s.Match(Ev.Fat(label: "usbstick"))).Reason, "a label that differs only in case is not the same label");
        Assert.True(Out.IsAutomaticAttach(s.Match(Ev.Fat(label: "USBSTICK"))));
    }

    [Test]
    public static void Two_unknown_labels_do_not_corroborate_each_other()
    {
        var s = new Scenario();
        s.Save(Ev.Fat(label: null));                                   // the label query failed when it was saved
        Assert.Equal(PromptReason.ModerateNotCorroborated, Out.Asked(s.Match(Ev.Fat(label: null))).Reason, "unknown is not equal to unknown");

        // an empty label is a known value, and equals another empty label
        var empty = new Scenario();
        empty.Save(Ev.Fat(label: ""));
        Assert.True(Out.IsAutomaticAttach(empty.Match(Ev.Fat(label: ""))));
        Assert.Equal(PromptReason.ModerateNotCorroborated, Out.Asked(empty.Match(Ev.Fat(label: null))).Reason, "empty against unknown is not a match either");

        // nor do two unknown labels make two mounted volumes clones
        var mounted = new Scenario();
        var saved = mounted.Save(Ev.Fat("exFAT", @"E:\Photos", label: null));
        var other = FatMounted(label: null);
        Assert.Equal(PromptReason.ModerateNotCorroborated, Out.Asked(mounted.Match(Ev.Fat("exFAT", @"G:\Photos", label: null), [other])).Reason);
        Assert.Equal(1, mounted.Store.Volumes.Count);
        Assert.NotNull(saved.Volume);
    }

    // ---- ID-13, a canonical path with no usable shape (T8) ----

    [Test]
    public static void A_canonical_path_that_is_neither_a_drive_letter_nor_a_UNC_path_makes_the_source_unsaveable()
    {
        var shapes = new[]
        {
            @"Volume{4b9a7c6e-0000-0000-0000-100000000000}\Media",   // a volume GUID path without its prefix
            @"\\.\x",                                                // the device namespace
            @"\\?\x",
            @"\\.\C:\Media",
            @"\\server",                                             // no share
            @"Media",                                                // relative
            "",                                                      // nothing at all
        };
        foreach (var canonical in shapes)
        {
            foreach (var kind in new[] { SourceKind.LocalVolume, SourceKind.Network })
            {
                var e0 = Ev.Reading(canonical, canonical, "NTFS", 0x1234, 0x5678, "Data", 500_000_000_000, Ev.NtfsRoot) with { Kind = kind };
                var check = MinimumEvidence.Check(e0);
                var label = $"'{canonical}' as {kind}";
                Assert.False(check.Sufficient, label);
                var missing = check.Missing.Single();
                Assert.Equal(IdentityItem.CanonicalPath, missing.Item, label);
                Assert.Equal(EvidenceStatus.Unavailable, missing.Status, label);
                Assert.Contains("neither a drive-letter nor a UNC form", missing.Detail);

                var s = new Scenario();
                Assert.False(s.Assess(e0).CanSave, label);
                var outcome = s.Match(e0);
                Assert.True(outcome is MatchOutcome.SaveUnavailable, $"{label}: expected SaveUnavailable but was {Out.Describe(outcome)}");
                Assert.Equal(0, s.Store.Volumes.Count);
            }
        }
    }
}

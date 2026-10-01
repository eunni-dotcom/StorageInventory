using StorageInventory.Core.Identity;
using StorageInventory.History.Identity;
using StorageInventory.Testing;

namespace StorageInventory.History.Tests;

/// <summary>
/// TEST-I3: the matching algorithm of §7.5 (ID-01 to ID-06, ID-12) over fake evidence and the in-memory candidate store.
/// No filesystem is involved. Real-media evidence is a different question (Q-02, TEST-I4): a fake answers nothing about it.
/// </summary>
public static class MatchingTests
{
    private const ulong S1 = 0x1AFE5644FE561879;
    private const ulong S2 = 0x40AAE8B5AAE8A89C;

    // ---- Strong ----

    [Test]
    public static void Strong_with_no_candidate_creates_a_new_volume_and_source_on_evidence()
    {
        var s = new Scenario();
        var e0 = Ev.Ntfs(@"C:\Media");
        Assert.Equal(IdentityConfidence.Strong, s.Assess(e0).Confidence);

        var created = Out.Created(s.Match(e0));
        Assert.Null(created.ExistingVolumeId, "a new volume");
        Assert.Equal(SourceKind.LocalVolume, created.Kind);
        Assert.Equal(@"\Media", created.RootInVolume);
        Assert.Equal(IdentityConfidence.Strong, created.Confidence);
        Assert.Equal(IdentityBasis.Evidence, created.Basis);
    }

    [Test]
    public static void Strong_with_one_candidate_and_the_exact_root_attaches_automatically_on_evidence()
    {
        var s = new Scenario();
        var first = s.Save(Ev.Ntfs(@"C:\Media"));
        var attached = Out.Attached(s.Match(Ev.Ntfs(@"C:\Media")));
        Assert.Equal(first.Source.SourceId, attached.Source.SourceId);
        Assert.Equal(IdentityBasis.Evidence, attached.Basis);
        Assert.Equal(IdentityConfidence.Strong, attached.CaptureConfidence);
    }

    [Test]
    public static void Strong_with_one_candidate_and_another_root_adds_a_source_to_that_volume()
    {
        var s = new Scenario();
        var first = s.Save(Ev.Ntfs(@"C:\Media"));
        var created = Out.Created(s.Match(Ev.Ntfs(@"C:\Docs")));
        Assert.Equal(first.Volume!.VolumeId, created.ExistingVolumeId, "the same volume");
        Assert.Equal(@"\Docs", created.RootInVolume);
        Assert.Equal(IdentityBasis.Evidence, created.Basis);
    }

    [Test]
    public static void Strong_ignores_the_drive_letter_the_label_and_the_capacity()
    {
        // TEST-I2's logic without the media: the same volume at another letter, relabelled and resized, is the same source (ID-03).
        var s = new Scenario();
        var first = s.Save(Ev.Ntfs(@"E:\Music", S2, "Backup", 2_000_000_000_000));
        var later = Out.Attached(s.Match(Ev.Ntfs(@"F:\Music", S2, "Renamed", 2_500_000_000_000)));
        Assert.Equal(first.Source.SourceId, later.Source.SourceId);
        Assert.Equal(IdentityBasis.Evidence, later.Basis);
    }

    [Test]
    public static void Strong_needs_the_same_filesystem_type_not_just_the_same_serial()
    {
        var s = new Scenario();
        s.Save(Ev.Ntfs(@"D:\x", S1));
        var refs = Ev.Reading(@"D:\x", @"D:\x", "ReFS", unchecked((uint)S1), S1, "Data", 500_000_000_000, new FileId128(1, 0));
        Assert.Null(Out.Created(s.Match(refs)).ExistingVolumeId, "ReFS with an NTFS volume's serial is a different volume");
    }

    [Test]
    public static void The_root_directory_file_id_plays_no_part_in_matching()
    {
        // Different root IDs, same serial and root: still the same source. Equal root IDs, different serials: different volumes.
        var s = new Scenario();
        var first = s.Save(Ev.Ntfs(@"C:\Media", S1, rootId: new FileId128(0x0001000000000099, 0)));
        var again = Out.Attached(s.Match(Ev.Ntfs(@"C:\Media", S1, rootId: new FileId128(0x0007000000000042, 0))));
        Assert.Equal(first.Source.SourceId, again.Source.SourceId);

        // every NTFS volume has the same root (MFT record 5): that must not make two volumes one
        var other = Out.Created(s.Match(Ev.Ntfs(@"C:\Media", S2, rootId: Ev.NtfsRoot)));
        Assert.Null(other.ExistingVolumeId, "a different serial is a different volume whatever the root IDs");
    }

    [Test]
    public static void Strong_candidates_that_are_saved_clones_ask_and_never_assign_silently()
    {
        var s = new Scenario();
        var original = s.Save(Ev.Ntfs(@"E:\", S1));
        // the clone was saved too, as a separate volume with the same serial (the user said "different" when it was mounted beside the original)
        var cloneVolume = s.Store.AddVolume("NTFS", S1, unchecked((uint)S1), IdentityConfidence.Strong, "Data", 500_000_000_000);
        s.Store.AddLocalSource(cloneVolume.VolumeId, @"\", IdentityConfidence.Strong, IdentityBasis.Evidence);

        var prompt = Out.Asked(s.Match(Ev.Ntfs(@"E:\", S1)));
        Assert.Equal(PromptReason.AmbiguousVolumes, prompt.Reason);
        Assert.SequenceEqual(new[] { original.Volume!.VolumeId, cloneVolume.VolumeId }, prompt.Volumes.Select(v => v.VolumeId).Order());
        Assert.Equal(0, prompt.Sources.Count);
    }

    [Test]
    public static void Answering_an_ambiguous_prompt_attaches_on_the_users_word_or_starts_a_new_volume_or_skips()
    {
        var s = new Scenario();
        s.Save(Ev.Ntfs(@"E:\", S1));
        var cloneVolume = s.Store.AddVolume("NTFS", S1, unchecked((uint)S1), IdentityConfidence.Strong, "Data", 500_000_000_000);
        var cloneSource = s.Store.AddLocalSource(cloneVolume.VolumeId, @"\", IdentityConfidence.Strong, IdentityBasis.Evidence);
        var capture = s.Assess(Ev.Ntfs(@"E:\", S1));
        var prompt = Out.Asked(IdentityMatching.Match(capture, s.Store, s.Key, []));

        var yes = IdentityMatching.Answer(prompt, new IdentityAnswer.SameVolume(cloneVolume), capture, s.Store, s.Key);
        var attached = Out.Attached(yes);
        Assert.Equal(cloneSource.SourceId, attached.Source.SourceId);
        Assert.Equal(IdentityBasis.UserAsserted, attached.Basis);   // the evidence alone was not enough

        var no = Out.Created(IdentityMatching.Answer(prompt, IdentityAnswer.Different, capture, s.Store, s.Key));
        Assert.Null(no.ExistingVolumeId, "a different drive is a new volume");
        Assert.Equal(IdentityBasis.Evidence, no.Basis);

        var skip = IdentityMatching.Answer(prompt, IdentityAnswer.Skip, capture, s.Store, s.Key);
        Assert.True(skip is MatchOutcome.Decided { Decision: IdentityDecision.DoNotSave }, "Don't save this scan");

        // an answer that names a candidate the prompt did not offer is refused
        Assert.Throws<ArgumentException>(() => IdentityMatching.Answer(prompt, new IdentityAnswer.SameVolume(cloneVolume with { VolumeId = 99 }), capture, s.Store, s.Key));
        Assert.Throws<ArgumentException>(() => IdentityMatching.Answer(prompt, new IdentityAnswer.SameSource(cloneSource), capture, s.Store, s.Key));
    }

    [Test]
    public static void A_clone_mounted_beside_its_original_asks_even_though_only_one_volume_is_saved()
    {
        // ID-05: "If two currently mounted volumes report the same Strong or Moderate identity, StorageInventory asks".
        var s = new Scenario();
        var original = s.Save(Ev.Ntfs(@"E:\", S1));
        var clone = Ev.Ntfs(@"F:\", S1);
        var others = new[] { Scenario.Mounted(Ev.Ntfs(@"E:\", S1)) };

        var prompt = Out.Asked(s.Match(clone, others));
        Assert.Equal(PromptReason.CoMountedClones, prompt.Reason);
        Assert.SequenceEqual(new[] { original.Volume!.VolumeId }, prompt.Volumes.Select(v => v.VolumeId));
    }

    [Test]
    public static void A_clone_that_is_never_mounted_with_its_original_is_attached_to_the_same_source_limitation_L_ID1()
    {
        // L-ID1 (expected, documented): nothing StorageInventory reads distinguishes a clone that is not co-mounted, so it
        // is the same source. This test asserts the limitation exactly as the specification states it.
        var s = new Scenario();
        var original = s.Save(Ev.Ntfs(@"E:\", S1));
        var clone = Out.Attached(s.Match(Ev.Ntfs(@"F:\", S1)));
        Assert.Equal(original.Source.SourceId, clone.Source.SourceId);
        Assert.Equal(IdentityBasis.Evidence, clone.Basis);
    }

    [Test]
    public static void A_different_volume_mounted_beside_it_does_not_make_a_Strong_match_ask()
    {
        var s = new Scenario();
        var first = s.Save(Ev.Ntfs(@"E:\", S1));
        var outcome = s.Match(Ev.Ntfs(@"E:\", S1), [Scenario.Mounted(Ev.Ntfs(@"F:\", S2))]);
        Assert.Equal(first.Source.SourceId, Out.Attached(outcome).Source.SourceId);
    }

    [Test]
    public static void Treat_as_a_new_source_is_always_available_for_a_capture_that_can_be_saved_locally()
    {
        // ID-06: "Treat as a new source is always available". Prompts carry it as IfDifferent; for a capture the matcher would
        // attach automatically this is the same decision, so a later screen can offer it beside "attached to ...".
        var s = new Scenario();
        var saved = s.Save(Ev.Ntfs(@"E:\", S1));
        var capture = s.Assess(Ev.Ntfs(@"E:\", S1));
        Assert.Equal(saved.Source.SourceId, Out.Attached(IdentityMatching.Match(capture, s.Store, s.Key, [])).Source.SourceId, "the matcher would attach");

        var asNew = Assert.NotNull(IdentityMatching.TreatAsNewSource(capture));
        Assert.Null(asNew.ExistingVolumeId, "a new volume");
        Assert.Equal(@"\", asNew.RootInVolume);
        Assert.Equal(IdentityConfidence.Strong, asNew.Confidence);
        Assert.Equal(IdentityBasis.Evidence, asNew.Basis);

        var applied = s.Store.Apply(asNew, capture)!;
        Assert.True(applied.Volume!.VolumeId != saved.Volume!.VolumeId && applied.Source.SourceId != saved.Source.SourceId, "a second volume and source were created");
        // two volume rows with one serial are what clones look like: the next capture is asked, never assigned silently
        Assert.Equal(PromptReason.AmbiguousVolumes, Out.Asked(IdentityMatching.Match(capture, s.Store, s.Key, [])).Reason);

        // Moderate and PathOnly rest on the same bases as any other new source of theirs
        Assert.Equal(IdentityBasis.Evidence, IdentityMatching.TreatAsNewSource(s.Assess(Ev.Fat()))!.Basis);
        Assert.Equal(IdentityBasis.Location, IdentityMatching.TreatAsNewSource(s.Assess(Ev.Udf()))!.Basis);
    }

    [Test]
    public static void Treat_as_a_new_source_has_nothing_to_create_for_a_network_share_or_an_unsaveable_source()
    {
        var s = new Scenario();
        Assert.Null(IdentityMatching.TreatAsNewSource(s.Assess(Ev.Share(@"\\nas\media"))), "a share is its location: a second source with the same key cannot exist");
        var noPath = Ev.Ntfs(@"C:\x") with { CanonicalPath = EvidenceItem<string>.Failed("GetFinalPathNameByHandleW", 5) };
        Assert.Null(IdentityMatching.TreatAsNewSource(s.Assess(noPath)), "ID-13: a source that cannot be saved has nothing to treat as new");
    }

    [Test]
    public static void Another_name_for_the_captured_volume_is_not_a_second_volume()
    {
        // A SUBST letter for a folder of C: lists as a drive of its own with C:'s identity. Read the way the capture was read
        // (open it, take the canonical path, ask for the mount point of that path) it reports C:\, which is the capture's own
        // mount point: one volume under two names, not two volumes (ID-05 asks about two).
        var s = new Scenario();
        var saved = s.Save(Ev.Ntfs(@"C:\Media", S1));
        var capture = Ev.Ntfs(@"C:\Media", S1, entered: @"S:\");                 // scanned through the SUBST letter S:
        var itself = Scenario.Mounted(Ev.Ntfs(@"C:\", S1));                       // C: in the drive list
        var substName = Scenario.Mounted(Ev.Ntfs(@"C:\Media", S1, entered: @"S:\")); // S: in the drive list, resolved to C:

        Assert.Equal(saved.Source.SourceId, Out.Attached(s.Match(capture, [itself, substName])).Source.SourceId, "its own names are not clones");

        // a real clone has a mount point of its own: still asked about, with the captured volume's names in the list as well
        var realClone = Scenario.Mounted(Ev.Ntfs(@"F:\", S1));
        Assert.Equal(PromptReason.CoMountedClones, Out.Asked(s.Match(capture, [itself, substName, realClone])).Reason);
    }

    [Test]
    public static void A_mounted_volume_whose_mount_point_is_unknown_is_never_taken_for_the_captured_one()
    {
        // The exclusion rests on two known, exactly equal mount points. Anything less leaves the entry in the list, which can
        // only cost an extra question and never a silent assignment.
        var s = new Scenario();
        s.Save(Ev.Ntfs(@"E:\", S1));
        var unknown = Scenario.Mounted(Ev.Ntfs(@"F:\", S1)) with { MountPoint = null };
        Assert.Equal(PromptReason.CoMountedClones, Out.Asked(s.Match(Ev.Ntfs(@"E:\", S1), [unknown])).Reason, "unknown mount point: kept, so asked");

        var spelledDifferently = Scenario.Mounted(Ev.Ntfs(@"F:\", S1)) with { MountPoint = @"e:\" };   // not exactly the capture's E:\
        Assert.Equal(PromptReason.CoMountedClones, Out.Asked(s.Match(Ev.Ntfs(@"E:\", S1), [spelledDifferently])).Reason, "a different spelling is not the same mount point");

        var noMountPointOnTheCapture = Ev.Ntfs(@"E:\", S1) with { MountPoint = EvidenceItem<string>.Failed("GetVolumePathNameW", 5) };
        Assert.Equal(PromptReason.CoMountedClones, Out.Asked(s.Match(noMountPointOnTheCapture, [Scenario.Mounted(Ev.Ntfs(@"E:\", S1))])).Reason, "the capture's own mount point is unknown: nothing is dropped");
    }

    [Test]
    public static void A_drive_list_entry_built_from_a_reading_keeps_what_was_read_and_leaves_the_rest_unknown()
    {
        var entry = MountedVolume.From(Ev.Ntfs(@"E:\Media", S1, "Data", 1_000_000));
        Assert.Equal("NTFS", entry.FsType);
        Assert.Equal(unchecked((uint)S1), entry.Serial32!.Value);
        Assert.Equal(S1, entry.Serial64!.Value);
        Assert.Equal("Data", entry.Label);
        Assert.Equal(1_000_000L, entry.CapacityBytes!.Value);
        Assert.Equal(@"E:\", entry.MountPoint, "the mount point of the volume holding the canonical path");

        var degraded = MountedVolume.From(Ev.Ntfs(@"E:\Media", S1) with
        {
            FileSystemName = EvidenceItem<string>.Failed("GetVolumeInformationByHandleW", 5),
            VolumeLabel = EvidenceItem<string>.Failed("GetVolumeInformationByHandleW", 5),
            MountPoint = EvidenceItem<string>.Failed("GetVolumePathNameW", 5),
        });
        Assert.Null(degraded.FsType, "an item that could not be read is unknown, never invented");
        Assert.Null(degraded.Label);
        Assert.Null(degraded.MountPoint);
        Assert.Equal(S1, degraded.Serial64!.Value, "what was read is kept");
    }

    [Test]
    public static void Two_clones_that_were_never_saved_start_as_one_volume_and_the_second_scan_asks()
    {
        // §7.5 step 2: with no saved volume there is nothing to attach to and nothing to ask about ("None: a new volume").
        // Once the first clone's row exists, scanning the second while the first is still mounted finds it and asks (ID-05).
        var s = new Scenario();
        var first = Ev.Ntfs(@"E:\", S1);
        var second = Ev.Ntfs(@"F:\", S1);

        var saved = s.Save(first, [Scenario.Mounted(second)]);
        Assert.NotNull(saved.Volume, "the first scan created its volume and source");
        Assert.Equal(1, s.Store.Volumes.Count);

        var prompt = Out.Asked(s.Match(second, [Scenario.Mounted(first)]));
        Assert.Equal(PromptReason.CoMountedClones, prompt.Reason);
        Assert.SequenceEqual(new[] { saved.Volume!.VolumeId }, prompt.Volumes.Select(v => v.VolumeId));
    }

    [Test]
    public static void The_matcher_requires_the_mounted_volumes_so_clone_protection_cannot_be_left_off_by_omission()
    {
        var s = new Scenario();
        var capture = s.Assess(Ev.Ntfs(@"E:\", S1));
        Assert.Throws<ArgumentNullException>(() => IdentityMatching.Match(capture, s.Store, s.Key, null!));
        // the parameter has no default value: leaving it out does not compile, and that is the point
        var parameter = typeof(IdentityMatching).GetMethod(nameof(IdentityMatching.Match))!.GetParameters().Last();
        Assert.False(parameter.HasDefaultValue, "no default: a caller must state the mounted volumes, empty or not");
    }

    [Test]
    public static void A_zero_64_bit_serial_is_not_a_Strong_identity()
    {
        var zero = Ev.Reading(@"C:\Media", @"C:\Media", "NTFS", 0x1234, 0, "Data", 500_000_000_000, Ev.NtfsRoot);
        var capture = IdentityClassifier.Assess(zero);
        Assert.Equal(IdentityConfidence.PathOnly, capture.Confidence);
        Assert.Contains(nameof(ConfidenceReasonKind.Serial64Zero), string.Join(",", capture.Reasons.Select(r => r.Kind)));
    }

    // ---- Moderate ----

    [Test]
    public static void A_FAT_family_volume_is_never_Strong_even_when_FileIdInfo_returns_a_64_bit_serial()
    {
        foreach (var fs in new[] { "FAT", "FAT32", "exFAT" })
        {
            // FileIdInfo succeeding on FAT is possible (Q-02): the 32-bit serial zero-extended, or anything else
            foreach (var serial64 in new ulong?[] { null, 0x00000000A1B2C3D4, 0xDEADBEEFA1B2C3D4 })
            {
                var capture = IdentityClassifier.Assess(Ev.Fat(fs, serial64: serial64));
                Assert.Equal(IdentityConfidence.Moderate, capture.Confidence, $"{fs} with serial64 {serial64:X}");
            }
        }
    }

    [Test]
    public static void Moderate_with_full_corroboration_attaches_automatically_on_evidence()
    {
        var s = new Scenario();
        var first = s.Save(Ev.Fat("exFAT", @"E:\Photos"));
        var attached = Out.Attached(s.Match(Ev.Fat("exFAT", @"G:\Photos")));   // the stick came back at another letter
        Assert.Equal(first.Source.SourceId, attached.Source.SourceId);
        Assert.Equal(IdentityBasis.Evidence, attached.Basis);
        Assert.Equal(IdentityConfidence.Moderate, attached.CaptureConfidence);
    }

    [Test]
    public static void Moderate_with_a_changed_label_asks_instead_of_attaching()
    {
        var s = new Scenario();
        s.Save(Ev.Fat("FAT32", label: "USBSTICK"));
        var capture = s.Assess(Ev.Fat("FAT32", label: "RENAMED"));
        var prompt = Out.Asked(IdentityMatching.Match(capture, s.Store, s.Key, []));
        Assert.Equal(PromptReason.ModerateNotCorroborated, prompt.Reason);
        Assert.Equal(1, prompt.Volumes.Count);

        var same = Out.Attached(IdentityMatching.Answer(prompt, new IdentityAnswer.SameVolume(prompt.Volumes[0]), capture, s.Store, s.Key));
        Assert.Equal(IdentityBasis.UserAsserted, same.Basis);
        var different = Out.Created(IdentityMatching.Answer(prompt, IdentityAnswer.Different, capture, s.Store, s.Key));
        Assert.Null(different.ExistingVolumeId, "a different drive is a new volume");
        Assert.Equal(IdentityConfidence.Moderate, different.Confidence);
        Assert.Equal(IdentityBasis.Evidence, different.Basis);
    }

    [Test]
    public static void Moderate_capacity_must_be_within_one_percent_of_the_recorded_capacity_inclusive()
    {
        const long recorded = 1_000_000_000;
        Assert.True(IdentityMatching.CapacityWithinTolerance(recorded, recorded), "equal");
        Assert.True(IdentityMatching.CapacityWithinTolerance(recorded, 1_010_000_000), "exactly +1%");
        Assert.False(IdentityMatching.CapacityWithinTolerance(recorded, 1_010_000_001), "one byte over +1%");
        Assert.True(IdentityMatching.CapacityWithinTolerance(recorded, 990_000_000), "exactly -1%");
        Assert.False(IdentityMatching.CapacityWithinTolerance(recorded, 989_999_999), "one byte under -1%");
        Assert.False(IdentityMatching.CapacityWithinTolerance(recorded, null), "an unknown capacity cannot corroborate");
        Assert.False(IdentityMatching.CapacityWithinTolerance(null, recorded), "an unknown recorded capacity cannot corroborate");
        Assert.False(IdentityMatching.CapacityWithinTolerance(0, 0), "zero is not a capacity");
        Assert.False(IdentityMatching.CapacityWithinTolerance(recorded, 0));
        Assert.True(IdentityMatching.CapacityWithinTolerance(long.MaxValue, long.MaxValue - 1), "no overflow near the top of the range");
        Assert.False(IdentityMatching.CapacityWithinTolerance(1, long.MaxValue), "no overflow, and far outside");
    }

    [Test]
    public static void Moderate_capacity_inside_and_outside_one_percent_decides_between_attach_and_ask()
    {
        var s = new Scenario();
        s.Save(Ev.Fat(capacity: 31_000_000_000));
        Assert.True(Out.IsAutomaticAttach(s.Match(Ev.Fat(capacity: 31_310_000_000))), "+1% exactly: still the same drive");
        Assert.Equal(PromptReason.ModerateNotCorroborated, Out.Asked(s.Match(Ev.Fat(capacity: 31_310_000_001))).Reason);
        Assert.Equal(PromptReason.ModerateNotCorroborated, Out.Asked(s.Match(Ev.Fat(capacity: 62_000_000_000))).Reason, "a different size entirely");
    }

    [Test]
    public static void Moderate_collision_where_two_volumes_corroborate_asks()
    {
        var s = new Scenario();
        s.Save(Ev.Fat());
        var second = s.Store.AddVolume("FAT32", null, 0xA1B2C3D4, IdentityConfidence.Moderate, "USBSTICK", 31_000_000_000);
        s.Store.AddLocalSource(second.VolumeId, @"\", IdentityConfidence.Moderate, IdentityBasis.Evidence);

        var prompt = Out.Asked(s.Match(Ev.Fat()));
        Assert.Equal(PromptReason.ModerateNotCorroborated, prompt.Reason);
        Assert.Equal(2, prompt.Volumes.Count);
    }

    [Test]
    public static void Moderate_attaches_when_exactly_one_of_several_serial_matches_fully_corroborates()
    {
        var s = new Scenario();
        s.Save(Ev.Fat(label: "USBSTICK"));
        var other = s.Store.AddVolume("FAT32", null, 0xA1B2C3D4, IdentityConfidence.Moderate, "OTHERNAME", 31_000_000_000);
        s.Store.AddLocalSource(other.VolumeId, @"\", IdentityConfidence.Moderate, IdentityBasis.Evidence);

        var attached = Out.Attached(s.Match(Ev.Fat(label: "USBSTICK")));
        Assert.Equal(1, attached.Source.VolumeId, "the volume whose label matches");
    }

    [Test]
    public static void Moderate_with_no_serial_match_creates_a_new_volume_without_asking()
    {
        var s = new Scenario();
        s.Save(Ev.Fat(serial32: 0x11111111));
        Assert.Null(Out.Created(s.Match(Ev.Fat(serial32: 0x22222222))).ExistingVolumeId);
    }

    [Test]
    public static void Moderate_needs_the_same_filesystem_type()
    {
        var s = new Scenario();
        s.Save(Ev.Fat("FAT32"));
        // same serial, label and capacity but exFAT: not the same volume
        Assert.Null(Out.Created(s.Match(Ev.Fat("exFAT"))).ExistingVolumeId);
    }

    [Test]
    public static void Moderate_with_an_unknown_label_or_capacity_cannot_corroborate()
    {
        var s = new Scenario();
        s.Save(Ev.Fat());
        Assert.Equal(PromptReason.ModerateNotCorroborated, Out.Asked(s.Match(Ev.Reading(@"E:\", @"E:\", "FAT32", 0xA1B2C3D4, null, null, 31_000_000_000))).Reason, "no label");
        Assert.Equal(PromptReason.ModerateNotCorroborated, Out.Asked(s.Match(Ev.Reading(@"E:\", @"E:\", "FAT32", 0xA1B2C3D4, null, "USBSTICK", null))).Reason, "no capacity");
    }

    [Test]
    public static void A_zero_32_bit_serial_is_not_a_Moderate_identity()
    {
        var capture = IdentityClassifier.Assess(Ev.Fat(serial32: 0));
        Assert.Equal(IdentityConfidence.PathOnly, capture.Confidence);
        Assert.Equal(ConfidenceReasonKind.Serial32Zero, capture.Reasons.Single().Kind);
    }

    // ---- PathOnly, local ----

    [Test]
    public static void Optical_media_is_PathOnly_and_never_matches_automatically()
    {
        var s = new Scenario();
        var disc = Ev.Udf();
        Assert.Equal(IdentityConfidence.PathOnly, s.Assess(disc).Confidence);

        var first = Out.Created(s.Match(disc));
        Assert.Equal(IdentityConfidence.PathOnly, first.Confidence);
        Assert.Equal(IdentityBasis.Location, first.Basis);
        var saved = s.Save(disc);

        // the very same disc again: never silently attached, however identical the evidence is
        var capture = s.Assess(disc);
        var prompt = Out.Asked(IdentityMatching.Match(capture, s.Store, s.Key, []));
        Assert.Equal(PromptReason.PathOnlyMayBelong, prompt.Reason);
        Assert.SequenceEqual(new[] { saved.Source.SourceId }, prompt.Sources.Select(x => x.SourceId));

        // only an explicit pick attaches it, and records that it rested on the user's word
        var picked = Out.Attached(IdentityMatching.Answer(prompt, new IdentityAnswer.SameSource(saved.Source), capture, s.Store, s.Key));
        Assert.Equal(IdentityBasis.UserAsserted, picked.Basis);
        Assert.Equal(IdentityConfidence.PathOnly, picked.CaptureConfidence);

        // "No, a different drive" starts another source
        var fresh = Out.Created(IdentityMatching.Answer(prompt, IdentityAnswer.Different, capture, s.Store, s.Key));
        Assert.Null(fresh.ExistingVolumeId);
        Assert.Equal(IdentityBasis.Location, fresh.Basis);
    }

    [Test]
    public static void An_unknown_filesystem_is_PathOnly_with_the_reason()
    {
        var capture = IdentityClassifier.Assess(Ev.Reading(@"Z:\", @"Z:\", "FUSEFS", 0x1234, 0x5678, "x", 1_000_000));
        Assert.Equal(IdentityConfidence.PathOnly, capture.Confidence);
        Assert.Equal(ConfidenceReasonKind.FileSystemNotEligible, capture.Reasons.Single().Kind);
        Assert.Equal("FUSEFS", capture.Reasons.Single().Detail);
    }

    [Test]
    public static void An_NTFS_volume_whose_FileIdInfo_failed_is_PathOnly_and_never_attached_silently_to_its_Strong_source()
    {
        // ID-12: weaker later evidence never attaches to a stronger source. The same drive, but FileIdInfo failed this time.
        var s = new Scenario();
        var strong = s.Save(Ev.Ntfs(@"E:\Data", S2));
        Assert.Equal(IdentityConfidence.Strong, strong.Source.Confidence);

        var weaker = Ev.Reading(@"E:\Data", @"E:\Data", "NTFS", unchecked((uint)S2), null, "Data", 500_000_000_000, null);
        var capture = s.Assess(weaker);
        Assert.Equal(IdentityConfidence.PathOnly, capture.Confidence);
        Assert.Equal(ConfidenceReasonKind.Serial64Unavailable, capture.Reasons.Single().Kind);
        Assert.Equal(87, capture.Reasons.Single().Failure!.Win32Error, "the failure is kept");
        Assert.Equal(EvidenceStatus.NotProvided, capture.Reasons.Single().Failure!.Status);

        var outcome = IdentityMatching.Match(capture, s.Store, s.Key, []);
        Assert.False(Out.IsAutomaticAttach(outcome), "never silently");
        var prompt = Out.Asked(outcome);
        Assert.Equal(PromptReason.PathOnlyMayBelong, prompt.Reason);
        Assert.SequenceEqual(new[] { strong.Source.SourceId }, prompt.Sources.Select(x => x.SourceId));

        // when the user does attach it, the source keeps the strength it was created with
        var applied = s.Store.Apply(Out.Attached(IdentityMatching.Answer(prompt, new IdentityAnswer.SameSource(strong.Source), capture, s.Store, s.Key)), capture)!;
        Assert.Equal(IdentityConfidence.Strong, applied.Source.Confidence);
        Assert.Equal(IdentityBasis.Evidence, applied.Source.Basis);
    }

    [Test]
    public static void A_PathOnly_local_capture_with_no_candidate_is_a_new_source_resting_on_its_location()
    {
        var s = new Scenario();
        var created = Out.Created(s.Match(Ev.Reading(@"Z:\x", @"Z:\x", "FUSEFS", 0x1234, null, "x", 1_000_000)));
        Assert.Null(created.ExistingVolumeId);
        Assert.Equal(IdentityConfidence.PathOnly, created.Confidence);
        Assert.Equal(IdentityBasis.Location, created.Basis);
    }

    [Test]
    public static void The_candidates_for_a_PathOnly_pick_share_the_root_and_the_filesystem()
    {
        // Only existing sources that this capture could be are offered: the same root (exactly, or apart from letter case)
        // on a volume of the same filesystem. Serial, label and capacity are not consulted (no invented heuristic).
        var s = new Scenario();
        var udfWhole = s.Save(Ev.Udf(@"D:\", label: "ONE"));
        s.Save(Ev.Udf(@"D:\Sub", label: "ONE"));        // another root: not a candidate for a whole-disc capture
        s.Save(Ev.Ntfs(@"C:\", S1));                    // the same root `\`, but NTFS: not a candidate for a UDF disc

        var disc = Out.Asked(s.Match(Ev.Udf(@"D:\", serial32: 0x99999999, label: "TWO")));
        Assert.SequenceEqual(new[] { udfWhole.Source.SourceId }, disc.Sources.Select(x => x.SourceId));

        // a PathOnly NTFS capture of the whole volume (FileIdInfo failed): the NTFS whole-volume source, not the UDF one
        var weakNtfs = Ev.Reading(@"C:\", @"C:\", "NTFS", unchecked((uint)S1), null, "Data", 500_000_000_000, null);
        var ntfs = Out.Asked(s.Match(weakNtfs));
        Assert.Equal(1, ntfs.Sources.Count);
        Assert.Equal(s.Store.GetVolume(ntfs.Sources[0].VolumeId!.Value)!.FsType, "NTFS");

        // a PathOnly capture whose root matches only apart from letter case is a candidate too
        var lower = Out.Asked(s.Match(Ev.Udf(@"D:\sub", serial32: 0x77777777, label: "THREE")));
        Assert.Equal(@"\Sub", lower.Sources.Single().RootInVolume);
    }

    // ---- network ----

    [Test]
    public static void A_network_source_matches_by_location_and_rests_on_it()
    {
        var s = new Scenario();
        var e0 = Ev.Share(@"\\nas\media\Music");
        var capture = s.Assess(e0);
        Assert.Equal(SourceKind.Network, capture.Kind);
        Assert.Equal(IdentityConfidence.PathOnly, capture.Confidence);
        Assert.Equal(@"\\nas\media", capture.NetworkRoot);
        Assert.Equal(@"\Music", capture.RootInVolume);

        var created = Out.Created(s.Match(e0));
        Assert.Equal(SourceKind.Network, created.Kind);
        Assert.Equal(@"\\NAS\MEDIA", created.NetworkRootKey, "the share is keyed by its comparison key");
        Assert.Equal(IdentityBasis.Location, created.Basis);
        var saved = s.Save(e0);

        var again = Out.Attached(s.Match(e0));
        Assert.Equal(saved.Source.SourceId, again.Source.SourceId);
        Assert.Equal(IdentityBasis.Location, again.Basis);
        Assert.Equal(IdentityConfidence.PathOnly, again.CaptureConfidence);
    }

    [Test]
    public static void A_network_capture_attaches_to_its_share_and_root_whatever_confidence_the_stored_source_carries()
    {
        // ID-12's "weaker than the source" is about local sources: a network capture is always PathOnly, so is every network
        // source, and the location IS the identity. Even a (wrongly) stronger stored row never produces a question whose "No"
        // would try to create a second source with the same share and root, which the schema's unique key forbids.
        var s = new Scenario();
        var e0 = Ev.Share(@"\\nas\media\Music");
        var stored = s.Store.AddNetworkSource(@"\\NAS\MEDIA", @"\Music", IdentityConfidence.Strong, IdentityBasis.Evidence);

        var attached = Out.Attached(s.Match(e0));
        Assert.Equal(stored.SourceId, attached.Source.SourceId);
        Assert.Equal(IdentityBasis.Location, attached.Basis);
    }

    [Test]
    public static void A_server_that_reports_NTFS_and_a_serial_still_gives_a_PathOnly_network_source()
    {
        // measured in C3: a Windows SMB server returns the underlying NTFS volume's filesystem and both serials
        var capture = IdentityClassifier.Assess(Ev.Share());
        Assert.Equal(IdentityConfidence.PathOnly, capture.Confidence);
        Assert.Equal(ConfidenceReasonKind.NetworkSource, capture.Reasons.Single().Kind);
        Assert.Equal(S1, capture.Serial64!.Value, "the evidence is recorded, never used to match a network source");
    }

    [Test]
    public static void A_mapped_drive_letter_is_the_network_source_it_resolves_to()
    {
        var s = new Scenario();
        var viaUnc = s.Save(Ev.Share(@"\\nas\media\Music"));
        var viaLetter = Ev.Reading(@"X:\Music", @"\\nas\media\Music", "NTFS", unchecked((uint)S1), S1, "", 4_000_000_000_000, Ev.NtfsRoot);

        var capture = s.Assess(viaLetter);
        Assert.Equal(SourceKind.Network, capture.Kind, "the drive-letter syntax of the input is not identity");
        Assert.Equal(viaUnc.Source.SourceId, Out.Attached(s.Match(viaLetter)).Source.SourceId, "another letter, same share");
    }

    [Test]
    public static void Different_server_spellings_are_different_sources_and_no_name_is_resolved()
    {
        var s = new Scenario();
        s.Save(Ev.Share(@"\\nas\media"));
        foreach (var spelling in new[] { @"\\nas.local\media", @"\\192.168.1.5\media", @"\\nas2\media" })
        {
            Assert.Equal(SourceKind.Network, Out.Created(s.Match(Ev.Share(spelling))).Kind, spelling);
        }
    }

    [Test]
    public static void Server_and_share_names_match_through_the_comparison_key()
    {
        var s = new Scenario();
        var first = s.Save(Ev.Share(@"\\NAS\Media"));
        Assert.Equal(first.Source.SourceId, Out.Attached(s.Match(Ev.Share(@"\\nas\media"))).Source.SourceId);
    }

    [Test]
    public static void A_network_source_never_matches_a_local_volume_and_a_local_one_never_matches_a_share()
    {
        var s = new Scenario();
        s.Save(Ev.Ntfs(@"C:\Music", S1));
        // the server reports the same filesystem and serials as the local volume: it is still not that volume
        Assert.Equal(SourceKind.Network, Out.Created(s.Match(Ev.Share(@"\\nas\media\Music", serial64: S1))).Kind);

        var t = new Scenario();
        t.Save(Ev.Share(@"\\nas\media\Music", serial64: S1));
        Assert.Null(Out.Created(t.Match(Ev.Ntfs(@"C:\Music", S1))).ExistingVolumeId);
    }

    [Test]
    public static void A_network_root_that_differs_only_in_letter_case_is_suggested_never_merged()
    {
        var s = new Scenario();
        var saved = s.Save(Ev.Share(@"\\nas\media\Music"));
        var capture = s.Assess(Ev.Share(@"\\nas\media\music"));
        var prompt = Out.Asked(IdentityMatching.Match(capture, s.Store, s.Key, []));
        Assert.Equal(PromptReason.CaseVariantRoot, prompt.Reason);
        Assert.SequenceEqual(new[] { saved.Source.SourceId }, prompt.Sources.Select(x => x.SourceId));
        Assert.Equal(SourceKind.Network, prompt.IfDifferent.Kind);
        Assert.Equal(IdentityBasis.Location, prompt.IfDifferent.Basis);
    }

    // ---- the root inside the volume (ID-02) ----

    [Test]
    public static void A_root_that_differs_only_in_letter_case_is_suggested_never_merged_and_both_can_exist()
    {
        var s = new Scenario();
        var upper = s.Save(Ev.Ntfs(@"C:\Media", S1));
        var capture = s.Assess(Ev.Ntfs(@"C:\media", S1));

        var prompt = Out.Asked(IdentityMatching.Match(capture, s.Store, s.Key, []));
        Assert.Equal(PromptReason.CaseVariantRoot, prompt.Reason);
        Assert.SequenceEqual(new[] { upper.Source.SourceId }, prompt.Sources.Select(x => x.SourceId));
        Assert.Equal(upper.Volume!.VolumeId, prompt.IfDifferent.ExistingVolumeId, "a new source on the same volume");

        // "Yes": the suggestion is accepted, resting on the user's word
        var yes = Out.Attached(IdentityMatching.Answer(prompt, new IdentityAnswer.SameSource(upper.Source), capture, s.Store, s.Key));
        Assert.Equal(IdentityBasis.UserAsserted, yes.Basis);

        // "No": in a case-sensitive directory \Media and \media are two folders, so two sources
        var lower = s.SaveAnswering(Ev.Ntfs(@"C:\media", S1), _ => IdentityAnswer.Different);
        Assert.Equal(@"\media", lower.Source.RootInVolume);
        Assert.Equal(upper.Volume.VolumeId, lower.Volume!.VolumeId);
        Assert.Equal(upper.Source.SourceId == lower.Source.SourceId, false);
        // and each now matches exactly
        Assert.Equal(lower.Source.SourceId, Out.Attached(s.Match(Ev.Ntfs(@"C:\media", S1))).Source.SourceId);
        Assert.Equal(upper.Source.SourceId, Out.Attached(s.Match(Ev.Ntfs(@"C:\Media", S1))).Source.SourceId);
    }

    [Test]
    public static void A_whole_volume_and_a_folder_of_it_are_different_sources()
    {
        var s = new Scenario();
        var whole = s.Save(Ev.Ntfs(@"C:\", S1));
        Assert.Equal(@"\", whole.Source.RootInVolume);
        var folder = Out.Created(s.Match(Ev.Ntfs(@"C:\Media\Music", S1)));
        Assert.Equal(whole.Volume!.VolumeId, folder.ExistingVolumeId);
        Assert.Equal(@"\Media\Music", folder.RootInVolume);
    }

    [Test]
    public static void The_root_comes_from_the_canonical_path_not_from_the_drive_letter_entered()
    {
        // a SUBST letter S: for C:\Users\Jack\Media: the source is that folder of that volume, not "S:\"
        var e0 = Ev.Reading(@"S:\", @"C:\Users\Jack\Media", "NTFS", unchecked((uint)S1), S1, "Data", 500_000_000_000, Ev.NtfsRoot);
        var capture = IdentityClassifier.Assess(e0);
        Assert.Equal(@"\Users\Jack\Media", capture.RootInVolume);

        var s = new Scenario();
        var saved = s.Save(e0);
        var direct = Out.Attached(s.Match(Ev.Ntfs(@"C:\Users\Jack\Media", S1)));
        Assert.Equal(saved.Source.SourceId, direct.Source.SourceId, "the same folder reached directly");
    }

    // ---- minimum evidence (ID-13) ----

    [Test]
    public static void Without_a_canonical_path_saving_is_unavailable_for_every_source_type()
    {
        var s = new Scenario();
        foreach (var e0 in new[] { Ev.Ntfs(@"C:\Media"), Ev.Fat(), Ev.Udf(), Ev.Share() })
        {
            var broken = e0 with { CanonicalPath = EvidenceItem<string>.Failed("GetFinalPathNameByHandleW", 5) };
            var outcome = s.Match(broken);
            var unavailable = outcome as MatchOutcome.SaveUnavailable ?? throw new AssertionException($"expected SaveUnavailable but was {Out.Describe(outcome)}");
            Assert.Equal(IdentityItem.CanonicalPath, unavailable.Evidence.Missing.Single().Item);
            Assert.Equal(5, unavailable.Evidence.Missing.Single().Win32Error, "the failure reason is kept");
        }
    }

    [Test]
    public static void A_local_volume_without_a_filesystem_name_or_a_32_bit_serial_cannot_be_saved()
    {
        var s = new Scenario();
        var noName = Ev.Reading(@"C:\x", @"C:\x", null, 0x1234, 0x5678, "x", 1_000_000, Ev.NtfsRoot);
        var noSerial = Ev.Reading(@"C:\x", @"C:\x", "NTFS", null, 0x5678, "x", 1_000_000, Ev.NtfsRoot);
        Assert.SequenceEqual(new[] { IdentityItem.FileSystemName }, ((MatchOutcome.SaveUnavailable)s.Match(noName)).Evidence.Missing.Select(m => m.Item));
        Assert.SequenceEqual(new[] { IdentityItem.VolumeSerial32 }, ((MatchOutcome.SaveUnavailable)s.Match(noSerial)).Evidence.Missing.Select(m => m.Item));
        Assert.False(s.Assess(noName).CanSave);
        Assert.Equal(IdentityConfidence.PathOnly, s.Assess(noName).Confidence);
        Assert.Equal(ConfidenceReasonKind.FileSystemNameUnavailable, s.Assess(noName).Reasons.Single().Kind);
    }

    [Test]
    public static void A_network_source_needs_only_the_canonical_path_to_be_saved()
    {
        var s = new Scenario();
        var bare = Ev.Reading(@"\\nas\media", @"\\nas\media", null, null, null, null, null, null);   // a provider that returns nothing but the path
        var capture = s.Assess(bare);
        Assert.True(capture.CanSave);
        Assert.Equal(IdentityConfidence.PathOnly, capture.Confidence);
        Assert.Equal(SourceKind.Network, Out.Created(s.Match(bare)).Kind);
    }

    // ---- fixed confidence and basis (ID-12) ----

    [Test]
    public static void Confidence_and_basis_are_fixed_when_a_source_is_created_and_never_change()
    {
        var s = new Scenario();
        s.Save(Ev.Ntfs(@"E:\", S1));                          // Strong, Evidence
        s.Save(Ev.Share(@"\\nas\media"));                     // PathOnly, Location
        s.Save(Ev.Udf());                                     // PathOnly, Location
        var before = s.Store.Sources.ToList();
        var volumesBefore = s.Store.Volumes.Select(v => (v.VolumeId, v.Confidence)).ToList();

        // every kind of later capture, automatic or explicit
        s.Save(Ev.Ntfs(@"F:\", S1, "Relabelled", 999_000_000_000));
        Assert.Equal("Relabelled", s.Store.Volumes[0].Label, "only information (label, capacity) moves");
        Assert.Equal(999_000_000_000, s.Store.Volumes[0].CapacityBytes);

        s.Save(Ev.Share(@"\\NAS\MEDIA"));
        s.SaveAnswering(Ev.Udf(), p => new IdentityAnswer.SameSource(p.Sources[0]));
        var weakerNtfs = Ev.Reading(@"E:\", @"E:\", "NTFS", unchecked((uint)S1), null, "Relabelled", 999_000_000_000, null);
        s.SaveAnswering(weakerNtfs, p => new IdentityAnswer.SameSource(p.Sources[0]));

        Assert.SequenceEqual(before, s.Store.Sources.Take(before.Count), "no source changed: confidence, basis and keys are fixed at creation");
        Assert.Equal(before.Count, s.Store.Sources.Count, "none of those captures created a source");
        Assert.SequenceEqual(volumesBefore, s.Store.Volumes.Select(v => (v.VolumeId, v.Confidence)).Take(volumesBefore.Count));
    }

    [Test]
    public static void A_capture_weaker_than_the_matched_source_is_never_attached_automatically()
    {
        // ID-12 as a guard, with a crafted store: a Moderate capture finds its volume by full corroboration, but the source
        // on it was created from Strong evidence. Whatever produced that state, a weaker later capture goes through ID-04.
        var s = new Scenario();
        var volume = s.Store.AddVolume("FAT32", null, 0xA1B2C3D4, IdentityConfidence.Moderate, "USBSTICK", 31_000_000_000);
        var source = s.Store.AddLocalSource(volume.VolumeId, @"\", IdentityConfidence.Strong, IdentityBasis.Evidence);

        var capture = s.Assess(Ev.Fat());
        var outcome = IdentityMatching.Match(capture, s.Store, s.Key, []);
        var prompt = Out.Asked(outcome);
        Assert.Equal(PromptReason.WeakerThanSource, prompt.Reason);
        Assert.SequenceEqual(new[] { source.SourceId }, prompt.Sources.Select(x => x.SourceId));

        // after the user's confirmation it attaches, resting on that
        var yes = Out.Attached(IdentityMatching.Answer(prompt, new IdentityAnswer.SameSource(source), capture, s.Store, s.Key));
        Assert.Equal(IdentityBasis.UserAsserted, yes.Basis);
        // and "No" does not add a weaker source to the stronger volume
        Assert.Null(Out.Created(IdentityMatching.Answer(prompt, IdentityAnswer.Different, capture, s.Store, s.Key)).ExistingVolumeId);
    }

    [Test]
    public static void Skipping_changes_nothing_and_a_stale_decision_fails_like_SourceChanged()
    {
        var s = new Scenario();
        var saved = s.Save(Ev.Ntfs(@"E:\", S1));
        var capture = s.Assess(Ev.Ntfs(@"E:\", S1));
        var rowsBefore = s.Store.Sources.ToList();

        Assert.Null(s.Store.Apply(new IdentityDecision.DoNotSave(), capture), "Don't save: nothing is applied");
        Assert.SequenceEqual(rowsBefore, s.Store.Sources);

        // a chosen row that no longer exists fails the import (SourceChanged); a taken key fails too
        var stale = new IdentityDecision.AttachToSource(saved.Source with { SourceId = 404 }, IdentityConfidence.Strong, IdentityBasis.Evidence);
        Assert.Throws<InvalidOperationException>(() => s.Store.Apply(stale, capture));
        var taken = new IdentityDecision.CreateSource(SourceKind.LocalVolume, saved.Volume!.VolumeId, null, @"\", IdentityConfidence.Strong, IdentityBasis.Evidence);
        Assert.Throws<InvalidOperationException>(() => s.Store.Apply(taken, capture));
    }

    [Test]
    public static void A_zero_serial_is_never_stored_as_an_identity()
    {
        var store = new InMemoryIdentityStore();
        Assert.Throws<ArgumentException>(() => store.AddVolume("NTFS", 0, 1, IdentityConfidence.Strong));
        Assert.Throws<ArgumentException>(() => store.AddVolume("FAT32", null, 0, IdentityConfidence.Moderate));
    }
}

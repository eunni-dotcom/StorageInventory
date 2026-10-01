using StorageInventory.Core.Identity;
using StorageInventory.History.Identity;
using StorageInventory.Testing;

namespace StorageInventory.History.Tests;

/// <summary>ID-01 as a table (§7.3): which evidence gives which confidence, and why it is not higher.</summary>
public static class ClassifierTests
{
    private static string Describe(IdentityAssessment a) => $"{a.Confidence} [{string.Join(", ", a.Reasons.Select(r => r.Kind))}]";

    [Test]
    public static void The_confidence_table()
    {
        // fs, serial32, serial64, expected, expected reason (null = none)
        var cases = new (string Name, VolumeEvidence Evidence, IdentityConfidence Expected, ConfidenceReasonKind? Reason)[]
        {
            ("NTFS with a 64-bit serial", Ev.Ntfs(), IdentityConfidence.Strong, null),
            ("ReFS with a 64-bit serial", Ev.Refs(), IdentityConfidence.Strong, null),
            ("NTFS, 64-bit serial zero", Ev.Reading(@"C:\x", @"C:\x", "NTFS", 1, 0, "x", 1000, Ev.NtfsRoot), IdentityConfidence.PathOnly, ConfidenceReasonKind.Serial64Zero),
            ("NTFS, FileIdInfo failed", Ev.Reading(@"C:\x", @"C:\x", "NTFS", 1, null, "x", 1000, null), IdentityConfidence.PathOnly, ConfidenceReasonKind.Serial64Unavailable),
            ("ReFS, FileIdInfo failed", Ev.Reading(@"R:\x", @"R:\x", "ReFS", 1, null, "x", 1000, null), IdentityConfidence.PathOnly, ConfidenceReasonKind.Serial64Unavailable),
            ("FAT", Ev.Fat("FAT"), IdentityConfidence.Moderate, null),
            ("FAT32", Ev.Fat("FAT32"), IdentityConfidence.Moderate, null),
            ("exFAT", Ev.Fat("exFAT"), IdentityConfidence.Moderate, null),
            ("FAT32 whose FileIdInfo succeeded (zero-extended)", Ev.Fat("FAT32", serial64: 0xA1B2C3D4), IdentityConfidence.Moderate, null),
            ("exFAT whose FileIdInfo succeeded (anything)", Ev.Fat("exFAT", serial64: 0x123456789ABCDEF0), IdentityConfidence.Moderate, null),
            ("FAT32, 32-bit serial zero", Ev.Fat("FAT32", serial32: 0), IdentityConfidence.PathOnly, ConfidenceReasonKind.Serial32Zero),
            ("FAT32, serial query failed", Ev.Reading(@"E:\x", @"E:\x", "FAT32", null, null, "x", 1000), IdentityConfidence.PathOnly, ConfidenceReasonKind.Serial32Unavailable),
            ("UDF", Ev.Udf(), IdentityConfidence.PathOnly, ConfidenceReasonKind.FileSystemNotEligible),
            ("an unknown filesystem", Ev.Reading(@"Z:\x", @"Z:\x", "FUSEFS", 1, 2, "x", 1000), IdentityConfidence.PathOnly, ConfidenceReasonKind.FileSystemNotEligible),
            ("the filesystem name could not be read", Ev.Reading(@"C:\x", @"C:\x", null, 1, 2, "x", 1000), IdentityConfidence.PathOnly, ConfidenceReasonKind.FileSystemNameUnavailable),
            ("a network share reporting NTFS and a serial", Ev.Share(), IdentityConfidence.PathOnly, ConfidenceReasonKind.NetworkSource),
            ("a network share reporting FAT32", Ev.Reading(@"\\nas\x", @"\\nas\x", "FAT32", 1, null, "", 1000), IdentityConfidence.PathOnly, ConfidenceReasonKind.NetworkSource),
        };
        foreach (var (name, evidence, expected, reason) in cases)
        {
            var assessment = IdentityClassifier.Assess(evidence);
            Assert.Equal(expected, assessment.Confidence, $"{name}: {Describe(assessment)}");
            Assert.SequenceEqual(reason is null ? [] : new[] { reason.Value }, assessment.Reasons.Select(r => r.Kind), $"{name}: reasons");
        }
    }

    [Test]
    public static void Filesystem_names_are_classified_ignoring_case_and_nothing_else_is_guessed()
    {
        Assert.Equal(IdentityConfidence.Strong, IdentityClassifier.Assess(Ev.Reading(@"C:\", @"C:\", "ntfs", 1, 2, "x", 1000, Ev.NtfsRoot)).Confidence);
        Assert.Equal(IdentityConfidence.Moderate, IdentityClassifier.Assess(Ev.Reading(@"E:\", @"E:\", "EXFAT", 1, null, "x", 1000)).Confidence);
        Assert.Equal(IdentityConfidence.PathOnly, IdentityClassifier.Assess(Ev.Reading(@"E:\", @"E:\", "NTFS ", 1, 2, "x", 1000, Ev.NtfsRoot)).Confidence, "no trimming or fuzzy matching");
        Assert.Equal(IdentityConfidence.PathOnly, IdentityClassifier.Assess(Ev.Reading(@"E:\", @"E:\", "CDFS", 1, null, "x", 1000)).Confidence);
        Assert.Equal(IdentityConfidence.PathOnly, IdentityClassifier.Assess(Ev.Reading(@"E:\", @"E:\", "ReFS2", 1, 2, "x", 1000, null)).Confidence);
    }

    [Test]
    public static void Informational_failures_do_not_change_the_confidence()
    {
        // label, capacity, free space, flags, the mount point and the root file ID are recorded, never identity (ID-13)
        var degraded = Ev.Ntfs() with
        {
            VolumeLabel = EvidenceItem<string>.Failed("GetVolumeInformationByHandleW", 5),
            CapacityBytes = EvidenceItem<long>.Failed("GetDiskFreeSpaceExW", 5),
            FreeBytes = EvidenceItem<long>.Failed("GetDiskFreeSpaceExW", 5),
            MountPoint = EvidenceItem<string>.Failed("GetVolumePathNameW", 5),
            FileSystemFlags = EvidenceItem<uint>.Failed("GetVolumeInformationByHandleW", 5),
            RootDirectoryFileId = EvidenceItem<FileId128>.Failed("GetFileInformationByHandleEx/FileIdInfo", 87),
        };
        var assessment = IdentityClassifier.Assess(degraded);
        Assert.Equal(IdentityConfidence.Strong, assessment.Confidence);
        Assert.True(assessment.CanSave, "none of them is a minimum-evidence item");
        Assert.Null(assessment.VolumeLabel);
        Assert.Null(assessment.CapacityBytes);
    }

    [Test]
    public static void The_kind_comes_from_the_resolved_canonical_path_not_from_the_letter_entered()
    {
        var mapped = Ev.Reading(@"X:\", @"\\nas\media", "NTFS", 1, 2, "", 1000, Ev.NtfsRoot);
        Assert.Equal(SourceKind.Network, IdentityClassifier.Assess(mapped).Kind);
        var subst = Ev.Reading(@"S:\", @"C:\Data", "NTFS", 1, 2, "Data", 1000, Ev.NtfsRoot);
        Assert.Equal(SourceKind.LocalVolume, IdentityClassifier.Assess(subst).Kind);
        Assert.Equal(@"\Data", IdentityClassifier.Assess(subst).RootInVolume);
        Assert.Null(IdentityClassifier.Assess(subst).NetworkRoot);
    }

    [Test]
    public static void One_answer_to_local_or_network_decides_both_the_minimum_evidence_and_the_confidence()
    {
        // The kind is derived in one place (VolumeEvidence.ResolvedKind: where the canonical path says the object is). A reading
        // whose own Kind was left at its default (LocalVolume) but whose canonical path is a UNC path is a network source for
        // ID-13 (no filesystem name or serial is needed to save it) and for ID-01 (PathOnly) alike, and the reverse.
        var failedName = EvidenceItem<string>.Failed("GetVolumeInformationByHandleW", 5);
        var failedSerial = EvidenceItem<uint>.Failed("GetVolumeInformationByHandleW", 5);

        var unc = Ev.Share(@"\\nas\media") with { Kind = SourceKind.LocalVolume, FileSystemName = failedName, VolumeSerial32 = failedSerial };
        var network = IdentityClassifier.Assess(unc);
        Assert.Equal(SourceKind.Network, unc.ResolvedKind);
        Assert.Equal(SourceKind.Network, network.Kind);
        Assert.Equal(IdentityConfidence.PathOnly, network.Confidence);
        Assert.True(network.CanSave, "a network source needs only its canonical path (ID-13)");

        var drive = Ev.Ntfs(@"C:\Media") with { Kind = SourceKind.Network, FileSystemName = failedName, VolumeSerial32 = failedSerial };
        var local = IdentityClassifier.Assess(drive);
        Assert.Equal(SourceKind.LocalVolume, drive.ResolvedKind);
        Assert.Equal(SourceKind.LocalVolume, local.Kind);
        Assert.False(local.CanSave, "a local volume needs its filesystem name and 32-bit serial (ID-13)");

        // with no canonical path at all the reading's own Kind is all there is
        var noPath = Ev.Ntfs(@"C:\Media") with { Kind = SourceKind.Network, CanonicalPath = EvidenceItem<string>.Failed("GetFinalPathNameByHandleW", 5) };
        Assert.Equal(SourceKind.Network, noPath.ResolvedKind);
        Assert.False(IdentityClassifier.Assess(noPath).CanSave, "no canonical path: not saveable whatever the kind");
    }

    [Test]
    public static void Evidence_failures_keep_their_reason_for_the_user_interface()
    {
        var failed = Ev.Reading(@"C:\x", @"C:\x", "NTFS", 1, null, "x", 1000, null);
        var reason = IdentityClassifier.Assess(failed).Reasons.Single();
        Assert.Equal(IdentityItem.VolumeSerial64, reason.Failure!.Item);
        Assert.Equal("GetFileInformationByHandleEx/FileIdInfo", reason.Failure.Call);
        Assert.Equal(87, reason.Failure.Win32Error);
        Assert.Equal(EvidenceStatus.NotProvided, reason.Failure.Status, "error 87 means the filesystem does not implement the query");

        var denied = Ev.Ntfs() with { VolumeSerial64 = EvidenceItem<ulong>.Failed("GetFileInformationByHandleEx/FileIdInfo", 5) };
        Assert.Equal(EvidenceStatus.CallFailed, IdentityClassifier.Assess(denied).Reasons.Single().Failure!.Status, "error 5 is a failure, not an unsupported query");
    }

    [Test]
    public static void The_four_evidence_states_are_distinct_and_the_default_invents_nothing()
    {
        var available = EvidenceItem<uint>.Of(7, "call");
        var unavailable = EvidenceItem<uint>.Missing("call", "no handle");
        var failed = EvidenceItem<uint>.Failed("call", 5);
        var notProvided = EvidenceItem<uint>.Failed("call", 87);
        Assert.SequenceEqual(
            new[] { EvidenceStatus.Available, EvidenceStatus.Unavailable, EvidenceStatus.CallFailed, EvidenceStatus.NotProvided },
            new[] { available.Status, unavailable.Status, failed.Status, notProvided.Status });
        Assert.Equal(7u, available.Value);
        Assert.Equal("no handle", unavailable.Detail);
        Assert.Equal(5, failed.Win32Error);
        Assert.Equal(87, notProvided.Win32Error);
        Assert.Equal(EvidenceStatus.Unavailable, default(EvidenceItem<uint>).Status, "an item nobody filled in is unavailable, never a value");
        Assert.False(default(EvidenceItem<string>).IsAvailable);
    }

    [Test]
    public static void Only_the_documented_errors_mean_the_query_is_not_provided()
    {
        foreach (var error in new[] { 1, 50, 87, 120, 124 }) Assert.True(NativeErrors.MeansQueryUnsupported(error), $"error {error}");
        foreach (var error in new[] { 0, 2, 3, 5, 6, 21, 59, 123, 1117 }) Assert.False(NativeErrors.MeansQueryUnsupported(error), $"error {error}");
    }
}

/// <summary>
/// A systematic sweep over many combinations of evidence and stored rows, checking the invariants that make "never silent on
/// weak evidence" (ID-06) true everywhere, not only in the examples.
/// </summary>
public static class MatchingInvariantTests
{
    private static readonly string?[] Filesystems = ["NTFS", "ReFS", "FAT", "FAT32", "exFAT", "UDF", "FUSEFS", "ntfs"];
    private static readonly uint?[] Serial32s = [null, 0, 0xA1B2C3D4];
    private static readonly ulong?[] Serial64s = [null, 0, 0x1AFE5644FE561879];
    private static readonly string[] Canonicals = [@"E:\", @"E:\Media", @"E:\media", @"\\nas\media", @"\\nas\media\Media", @"\\NAS\MEDIA\media"];

    private static IEnumerable<VolumeEvidence> EveryEvidence()
    {
        foreach (var fs in Filesystems)
        foreach (var s32 in Serial32s)
        foreach (var s64 in Serial64s)
        foreach (var canonical in Canonicals)
        {
            yield return Ev.Reading(canonical, canonical, fs, s32, s64, "Data", 31_000_000_000, s64 is null ? null : Ev.NtfsRoot);
        }
    }

    [Test]
    public static void No_combination_of_evidence_breaks_the_confidence_invariants()
    {
        var checkedCases = 0;
        foreach (var e0 in EveryEvidence())
        {
            var a = IdentityClassifier.Assess(e0);
            checkedCases++;

            // network sources are never Strong or Moderate, whatever the server reports
            if (a.Kind == SourceKind.Network) Assert.Equal(IdentityConfidence.PathOnly, a.Confidence, "network");
            // a FAT-family volume is never Strong
            if (FileSystemNames.IsFatFamily(a.FileSystemName)) Assert.True(a.Confidence != IdentityConfidence.Strong, "FAT-family is never Strong");
            // Strong implies local, NTFS or ReFS, and a non-zero 64-bit serial
            if (a.Confidence == IdentityConfidence.Strong)
            {
                Assert.True(a.Kind == SourceKind.LocalVolume && FileSystemNames.IsStrongCapable(a.FileSystemName) && a.UsableSerial64 is not null, "Strong needs local NTFS/ReFS and a non-zero serial64");
            }
            // Moderate implies local, FAT-family, and a non-zero 32-bit serial
            if (a.Confidence == IdentityConfidence.Moderate)
            {
                Assert.True(a.Kind == SourceKind.LocalVolume && FileSystemNames.IsFatFamily(a.FileSystemName) && a.UsableSerial32 is not null, "Moderate needs local FAT-family and a non-zero serial32");
            }
            // anything that is neither is PathOnly and says why
            if (a.Confidence == IdentityConfidence.PathOnly) Assert.True(a.Reasons.Count == 1, $"PathOnly without exactly one reason: {a.FileSystemName}/{a.Serial32}/{a.Serial64}");
            if (a.Confidence != IdentityConfidence.PathOnly) Assert.True(a.Reasons.Count == 0, "no reason without a reduction");
        }
        Assert.True(checkedCases > 400, $"the sweep covered {checkedCases} readings");
    }

    [Test]
    public static void No_combination_of_evidence_and_stored_rows_ever_attaches_weak_evidence_silently()
    {
        var automatic = 0;
        var total = 0;
        foreach (var e0 in EveryEvidence())
        {
            var capture = IdentityClassifier.Assess(e0);
            if (!capture.CanSave) continue;

            // three stores: empty; one holding exactly this capture's source; one holding two volumes with its identity
            foreach (var store in StoresFor(e0, capture))
            {
                var outcome = IdentityMatching.Match(capture, store, AsciiFoldKey.Instance, []);
                total++;
                if (outcome is not MatchOutcome.Decided { Decision: IdentityDecision.AttachToSource attach }) continue;
                automatic++;

                if (capture.Kind == SourceKind.LocalVolume)
                {
                    // ID-06: only Strong evidence, or Moderate with full corroboration. Local PathOnly never attaches by itself.
                    Assert.True(capture.Confidence != IdentityConfidence.PathOnly, $"a local PathOnly capture was attached automatically: {Out.Describe(outcome)}");
                    Assert.Equal(IdentityBasis.Evidence, attach.Basis);
                }
                else
                {
                    Assert.Equal(IdentityBasis.Location, attach.Basis);
                }
                Assert.Equal(capture.Confidence, attach.CaptureConfidence);
                Assert.True(capture.Confidence <= attach.Source.Confidence, "a capture weaker than the source is never attached automatically (ID-12)");
            }
        }
        Assert.True(total > 400 && automatic > 0, $"{total} combinations, {automatic} automatic");
    }

    private static IEnumerable<InMemoryIdentityStore> StoresFor(VolumeEvidence e0, IdentityAssessment capture)
    {
        yield return new InMemoryIdentityStore();

        var one = new InMemoryIdentityStore();
        var decision = (IdentityMatching.Match(capture, one, AsciiFoldKey.Instance, []) as MatchOutcome.Decided)?.Decision;
        if (decision is IdentityDecision.CreateSource) one.Apply(decision, capture);
        yield return one;

        if (capture.Kind == SourceKind.LocalVolume && capture.FileSystemName is not null)
        {
            var two = new InMemoryIdentityStore();
            for (var i = 0; i < 2; i++)
            {
                var volume = two.AddVolume(capture.FileSystemName, capture.UsableSerial64, capture.UsableSerial32, capture.Confidence, capture.VolumeLabel, capture.CapacityBytes);
                two.AddLocalSource(volume.VolumeId, capture.RootInVolume!, capture.Confidence, IdentityBasis.Evidence);
            }
            yield return two;
        }
    }

    [Test]
    public static void Two_saved_volumes_with_one_identity_always_ask_and_never_assign()
    {
        foreach (var e0 in new[] { Ev.Ntfs(@"E:\"), Ev.Fat(), Ev.Refs() })
        {
            var capture = IdentityClassifier.Assess(e0);
            var store = new InMemoryIdentityStore();
            for (var i = 0; i < 2; i++)
            {
                var volume = store.AddVolume(capture.FileSystemName!, capture.UsableSerial64, capture.UsableSerial32, capture.Confidence, capture.VolumeLabel, capture.CapacityBytes);
                store.AddLocalSource(volume.VolumeId, capture.RootInVolume!, capture.Confidence, IdentityBasis.Evidence);
            }
            var prompt = Out.Asked(IdentityMatching.Match(capture, store, AsciiFoldKey.Instance, []));
            Assert.Equal(2, prompt.Volumes.Count, e0.FileSystemName.Value);
        }
    }

    [Test]
    public static void The_drive_letter_and_the_mount_point_never_change_the_outcome()
    {
        // ID-03: only the canonical root's path inside the volume matters, so the same volume at any letter decides alike
        var baseline = new Scenario();
        baseline.Save(Ev.Ntfs(@"E:\Media", 0x40AAE8B5AAE8A89C));
        baseline.Save(Ev.Fat("FAT32", @"F:\Photos"));
        foreach (var letter in "DGHKXZ")
        {
            var ntfs = Out.Attached(baseline.Match(Ev.Ntfs($@"{letter}:\Media", 0x40AAE8B5AAE8A89C)));
            Assert.Equal(1, ntfs.Source.SourceId, $"NTFS at {letter}:");
            var fat = Out.Attached(baseline.Match(Ev.Fat("FAT32", $@"{letter}:\Photos")));
            Assert.Equal(2, fat.Source.SourceId, $"FAT32 at {letter}:");
        }
    }
}

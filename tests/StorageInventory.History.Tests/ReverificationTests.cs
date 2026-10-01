using StorageInventory.Core.Identity;
using StorageInventory.History.Identity;
using StorageInventory.Testing;

namespace StorageInventory.History.Tests;

/// <summary>
/// TEST-I5 (unit): ID-10 and ID-13 as a pure function over the four readings. Every identity item is changed, and made
/// missing, at each of E1, E2 and E3; the mutable items are changed freely; a required item missing at E0 blocks saving.
/// </summary>
public static class ReverificationTests
{
    private static readonly EvidenceStage[] Later = [EvidenceStage.E1WindowStart, EvidenceStage.E2WindowEndHeld, EvidenceStage.E3WindowEndFresh];

    /// <summary>The scanner enumerates <c>C:\Media</c> in every reading; the canonical path read from the opened object may differ.</summary>
    private static VolumeEvidence Baseline(EvidenceStage stage, string canonical = @"C:\Media") => Ev.Ntfs(canonical, stage: stage, entered: @"C:\Media");

    private static ReverificationResult Evaluate(VolumeEvidence e0, Func<EvidenceStage, VolumeEvidence> reading) =>
        Reverification.Evaluate(e0, reading(EvidenceStage.E1WindowStart), reading(EvidenceStage.E2WindowEndHeld), reading(EvidenceStage.E3WindowEndFresh));

    /// <summary>One change per identity item. A new <see cref="IdentityItem"/> without an entry here fails the exhaustiveness test.</summary>
    private static readonly Dictionary<IdentityItem, Func<VolumeEvidence, VolumeEvidence>> Changes = new()
    {
        [IdentityItem.CanonicalPath] = e => e with { CanonicalPath = EvidenceItem<string>.Of(@"C:\Other", "GetFinalPathNameByHandleW") },
        [IdentityItem.FileSystemName] = e => e with { FileSystemName = EvidenceItem<string>.Of("ReFS", "GetVolumeInformationByHandleW") },
        [IdentityItem.VolumeSerial32] = e => e with { VolumeSerial32 = EvidenceItem<uint>.Of(0x12345678, "GetVolumeInformationByHandleW") },
        [IdentityItem.VolumeSerial64] = e => e with { VolumeSerial64 = EvidenceItem<ulong>.Of(0x0123456789ABCDEF, "GetFileInformationByHandleEx/FileIdInfo") },
        [IdentityItem.RootDirectoryFileId] = e => e with { RootDirectoryFileId = EvidenceItem<FileId128>.Of(new FileId128(0x0001000000000777, 0), "GetFileInformationByHandleEx/FileIdInfo") },
        [IdentityItem.SourceKind] = e => e with { Kind = SourceKind.Network },
    };

    private static readonly Dictionary<IdentityItem, Func<VolumeEvidence, VolumeEvidence>> Removals = new()
    {
        [IdentityItem.CanonicalPath] = e => e with { CanonicalPath = EvidenceItem<string>.Failed("GetFinalPathNameByHandleW", 59) },
        [IdentityItem.FileSystemName] = e => e with { FileSystemName = EvidenceItem<string>.Failed("GetVolumeInformationByHandleW", 59) },
        [IdentityItem.VolumeSerial32] = e => e with { VolumeSerial32 = EvidenceItem<uint>.Failed("GetVolumeInformationByHandleW", 59) },
        [IdentityItem.VolumeSerial64] = e => e with { VolumeSerial64 = EvidenceItem<ulong>.Failed("GetFileInformationByHandleEx/FileIdInfo", 59) },
        [IdentityItem.RootDirectoryFileId] = e => e with { RootDirectoryFileId = EvidenceItem<FileId128>.Failed("GetFileInformationByHandleEx/FileIdInfo", 59) },
    };

    [Test]
    public static void Equal_readings_are_verified()
    {
        var e0 = Baseline(EvidenceStage.E0Preflight);
        var result = Evaluate(e0, s => Baseline(s));
        Assert.Equal(ReverificationOutcome.Verified, result.Outcome);
        Assert.True(result.Eligible);
        Assert.Equal(0, result.Failures.Count);
    }

    [Test]
    public static void Every_identity_item_is_checked_so_the_table_here_is_exhaustive()
    {
        foreach (var item in Enum.GetValues<IdentityItem>())
        {
            Assert.True(Changes.ContainsKey(item), $"no change is tested for {item}: a new identity item must be added to the re-verification tests");
        }
        Assert.Equal(6, Enum.GetValues<IdentityItem>().Length);   // ID-13: path, name, serial32, serial64, root file ID, + the kind
    }

    [Test]
    public static void Any_identity_item_changing_at_any_reading_makes_the_capture_not_eligible()
    {
        var e0 = Baseline(EvidenceStage.E0Preflight);
        foreach (var (item, change) in Changes)
        {
            foreach (var changedStage in Later)
            {
                var result = Evaluate(e0, s => s == changedStage ? change(Baseline(s)) : Baseline(s));
                Assert.Equal(ReverificationOutcome.IdentityChangedDuringScan, result.Outcome, $"{item} changed at {changedStage}");
                var failure = result.Failures.Single(f => f.Item == item);
                Assert.Equal(changedStage, failure.Stage);
                Assert.Equal(ReverificationFailureKind.Different, failure.Kind);
                Assert.False(result.Eligible);
            }
        }
    }

    [Test]
    public static void Any_required_item_missing_at_any_reading_makes_the_capture_not_eligible_and_keeps_the_reason()
    {
        var e0 = Baseline(EvidenceStage.E0Preflight);
        foreach (var (item, remove) in Removals)
        {
            foreach (var stage in Later)
            {
                var result = Evaluate(e0, s => s == stage ? remove(Baseline(s)) : Baseline(s));
                Assert.Equal(ReverificationOutcome.IdentityChangedDuringScan, result.Outcome, $"{item} missing at {stage}");
                var failure = result.Failures.Single();
                Assert.Equal(item, failure.Item);
                Assert.Equal(stage, failure.Stage);
                Assert.Equal(ReverificationFailureKind.Missing, failure.Kind);
                Assert.Equal(59, failure.Missing!.Win32Error, "why it could not be read is kept");
            }
        }
    }

    [Test]
    public static void The_mutable_items_are_recorded_but_never_compared()
    {
        var e0 = Baseline(EvidenceStage.E0Preflight);
        foreach (var stage in Later)
        {
            var result = Evaluate(e0, s => s == stage
                ? Baseline(s) with
                {
                    VolumeLabel = EvidenceItem<string>.Of("Renamed", "x"),
                    CapacityBytes = EvidenceItem<long>.Of(1, "x"),
                    FreeBytes = EvidenceItem<long>.Of(2, "x"),
                    MountPoint = EvidenceItem<string>.Of(@"Z:\", "x"),
                    FileSystemFlags = EvidenceItem<uint>.Of(7, "x"),
                }
                : Baseline(s));
            Assert.Equal(ReverificationOutcome.Verified, result.Outcome, $"label, capacity, free space, flags and mount point changed at {stage}");

            var gone = Evaluate(e0, s => s == stage
                ? Baseline(s) with
                {
                    VolumeLabel = EvidenceItem<string>.Failed("x", 5),
                    CapacityBytes = EvidenceItem<long>.Failed("x", 5),
                    FreeBytes = EvidenceItem<long>.Failed("x", 5),
                    MountPoint = EvidenceItem<string>.Failed("x", 5),
                    FileSystemFlags = EvidenceItem<uint>.Failed("x", 5),
                }
                : Baseline(s));
            Assert.Equal(ReverificationOutcome.Verified, gone.Outcome, $"the mutable items could not be read at {stage}");
        }
    }

    [Test]
    public static void The_canonical_path_is_compared_exactly_including_letter_case()
    {
        var e0 = Baseline(EvidenceStage.E0Preflight, @"C:\Media");
        var result = Evaluate(e0, s => Baseline(s, s == EvidenceStage.E2WindowEndHeld ? @"C:\media" : @"C:\Media"));
        Assert.Equal(ReverificationOutcome.IdentityChangedDuringScan, result.Outcome, "a case-only rename of the source folder is a rename");
        Assert.Equal(EvidenceStage.E2WindowEndHeld, result.Failures.Single().Stage);
    }

    [Test]
    public static void Every_difference_is_listed_in_reading_and_item_order()
    {
        var e0 = Baseline(EvidenceStage.E0Preflight);
        var result = Evaluate(e0, s => s switch
        {
            EvidenceStage.E1WindowStart => Baseline(s),
            EvidenceStage.E2WindowEndHeld => Changes[IdentityItem.CanonicalPath](Baseline(s)),
            _ => Changes[IdentityItem.VolumeSerial64](Changes[IdentityItem.CanonicalPath](Baseline(s))),
        });
        Assert.SequenceEqual(
            new[] { (EvidenceStage.E2WindowEndHeld, IdentityItem.CanonicalPath), (EvidenceStage.E3WindowEndFresh, IdentityItem.CanonicalPath), (EvidenceStage.E3WindowEndFresh, IdentityItem.VolumeSerial64) },
            result.Failures.Select(f => (f.Stage, f.Item)));
    }

    // ---- minimum evidence (ID-13) at E0 ----

    [Test]
    public static void A_required_item_missing_at_E0_means_saving_is_unavailable_and_nothing_is_compared()
    {
        var all = (EvidenceStage s) => Baseline(s);
        foreach (var (item, remove) in Removals.Where(r => r.Key is IdentityItem.CanonicalPath or IdentityItem.FileSystemName or IdentityItem.VolumeSerial32))
        {
            var result = Reverification.Evaluate(remove(Baseline(EvidenceStage.E0Preflight)), all(EvidenceStage.E1WindowStart), all(EvidenceStage.E2WindowEndHeld), all(EvidenceStage.E3WindowEndFresh));
            Assert.Equal(ReverificationOutcome.SaveUnavailable, result.Outcome, item.ToString());
            Assert.SequenceEqual(new[] { item }, result.MinimumMissing.Select(m => m.Item));
            Assert.Equal(0, result.Failures.Count);
            Assert.False(result.Eligible);
        }
    }

    [Test]
    public static void FileIdInfo_is_required_later_only_when_it_succeeded_at_E0()
    {
        // E0 without a 64-bit serial and root ID (a filesystem that does not provide FileIdInfo, like UDF)
        var e0 = Baseline(EvidenceStage.E0Preflight) with
        {
            VolumeSerial64 = EvidenceItem<ulong>.Failed("GetFileInformationByHandleEx/FileIdInfo", 87),
            RootDirectoryFileId = EvidenceItem<FileId128>.Failed("GetFileInformationByHandleEx/FileIdInfo", 87),
        };
        var result = Evaluate(e0, s => Baseline(s) with { VolumeSerial64 = EvidenceItem<ulong>.Of(0xFFFF, "x") });
        Assert.Equal(ReverificationOutcome.Verified, result.Outcome, "an item E0 could not obtain is not required, whatever a later reading shows (ID-13 item 3)");

        // ...but once E0 has it, every later reading must
        var withIt = Evaluate(Baseline(EvidenceStage.E0Preflight), s => Baseline(s) with { VolumeSerial64 = EvidenceItem<ulong>.Failed("x", 87) });
        Assert.Equal(ReverificationOutcome.IdentityChangedDuringScan, withIt.Outcome);
    }

    [Test]
    public static void A_network_source_needs_only_the_path_but_what_the_provider_returned_at_E0_is_required_again()
    {
        VolumeEvidence Share(EvidenceStage s) => Ev.Share(@"\\nas\media", stage: s);

        // a provider that returns nothing but the path: saveable, and only the path is compared
        VolumeEvidence Bare(EvidenceStage s) => Ev.Reading(@"\\nas\media", @"\\nas\media", null, null, null, null, null, null, s);
        Assert.Equal(ReverificationOutcome.Verified, Evaluate(Bare(EvidenceStage.E0Preflight), Bare).Outcome);
        Assert.Equal(ReverificationOutcome.IdentityChangedDuringScan,
            Evaluate(Bare(EvidenceStage.E0Preflight), s => Bare(s) with { CanonicalPath = EvidenceItem<string>.Of(@"\\nas\other", "x") }).Outcome);

        // a provider that did return the filesystem and serials at E0: required at E1, E2 and E3
        var e0 = Share(EvidenceStage.E0Preflight);
        Assert.Equal(ReverificationOutcome.Verified, Evaluate(e0, Share).Outcome);
        foreach (var stage in Later)
        {
            var result = Evaluate(e0, s => s == stage ? Share(s) with { VolumeSerial32 = EvidenceItem<uint>.Failed("x", 59) } : Share(s));
            Assert.Equal(IdentityItem.VolumeSerial32, result.Failures.Single().Item, $"the serial the provider returned at E0 is required again at {stage}");
        }
    }

    [Test]
    public static void A_mapped_network_drive_pointed_at_another_share_fails_through_the_canonical_path()
    {
        VolumeEvidence At(EvidenceStage s, string unc) => Ev.Reading(@"X:\", unc, "NTFS", 1, 2, "", 1000, Ev.NtfsRoot, s);
        var result = Evaluate(At(EvidenceStage.E0Preflight, @"\\nas\a"), s => At(s, s == EvidenceStage.E3WindowEndFresh ? @"\\nas\b" : @"\\nas\a"));
        Assert.Equal(ReverificationOutcome.IdentityChangedDuringScan, result.Outcome);
        Assert.Equal(IdentityItem.CanonicalPath, result.Failures.Single().Item);
        Assert.Equal(EvidenceStage.E3WindowEndFresh, result.Failures.Single().Stage);
    }

    // ---- contract ----

    [Test]
    public static void The_readings_must_be_the_four_stages_taken_on_one_enumerated_path()
    {
        var e0 = Baseline(EvidenceStage.E0Preflight);
        var e1 = Baseline(EvidenceStage.E1WindowStart);
        var e2 = Baseline(EvidenceStage.E2WindowEndHeld);
        var e3 = Baseline(EvidenceStage.E3WindowEndFresh);
        Assert.Throws<ArgumentException>(() => Reverification.Evaluate(e0, e2, e1, e3));
        Assert.Throws<ArgumentException>(() => Reverification.Evaluate(e1, e1, e2, e3));
        Assert.Throws<ArgumentNullException>(() => Reverification.Evaluate(e0, e1, null!, e3));
        // E3 taken on the canonical path instead of the enumerated one: refused, because that is exactly the mistake ID-10 forbids
        var canonicalPathOpened = e3 with { EnumeratedPath = @"C:\SomewhereElse" };
        var error = Assert.Throws<ArgumentException>(() => Reverification.Evaluate(e0, e1, e2, canonicalPathOpened));
        Assert.Contains("enumerated path", error.Message);
    }
}

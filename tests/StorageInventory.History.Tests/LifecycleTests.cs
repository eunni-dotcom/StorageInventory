using StorageInventory.Core.Identity;
using StorageInventory.History.Identity;
using StorageInventory.Testing;
using static StorageInventory.History.Tests.FakeWorld;

namespace StorageInventory.History.Tests;

/// <summary>
/// TEST-I5 (unit, with the fake provider): the sequence of ID-10. E1 and E2 are read through ONE handle opened on the
/// enumerated path and held; E3 is a fresh open of the enumerated path; no HANDLE is ever opened on the canonical path (the
/// fake models handles. The two by-path queries for the mount point and the capacity are display information and are not
/// identity items, so what they are given cannot change a verdict); every handle is closed, on every ending.
/// </summary>
public static class LifecycleTests
{
    private static ReverificationResult Run(FakeWorld world, string enumerated, Action? betweenStartAndEnd = null)
    {
        var e0 = PreflightEvidence.Collect(world, enumerated);
        using var hold = RootIdentityHold.Open(world, enumerated);
        betweenStartAndEnd?.Invoke();
        var end = hold.Finish();
        return Reverification.Evaluate(e0, hold.E1, end.E2, end.E3);
    }

    private static Dir Folder(string canonical, ulong serial64 = 0x1AFE5644FE561879, string fs = "NTFS", FileId128? root = null) =>
        new() { Canonical = canonical, Serial64 = serial64, Serial32 = unchecked((uint)serial64), FileSystem = fs, RootId = root ?? Ev.NtfsRoot };

    private static void NoHandleLeaked(FakeWorld world) => Assert.Equal(0, world.OpenHandles, "every handle opened was closed");

    [Test]
    public static void E0_opens_the_enumerated_path_once_reads_and_closes()
    {
        var world = new FakeWorld();
        world.Map(@"S:\", Folder(@"C:\Media"));
        var e0 = PreflightEvidence.Collect(world, @"S:\");

        Assert.Equal(EvidenceStage.E0Preflight, e0.Stage);
        Assert.Equal(@"S:\", e0.EnumeratedPath);
        Assert.Equal(@"C:\Media", e0.CanonicalPath.Value);
        Assert.SequenceEqual(
            new[] { (Event.Open, 1), (Event.Read, 1), (Event.Close, 1) },
            world.Log.Select(e => (e.Kind, e.HandleId)));
        NoHandleLeaked(world);
    }

    [Test]
    public static void E1_and_E2_come_through_one_held_handle_and_E3_through_a_fresh_open_of_the_enumerated_path()
    {
        var world = new FakeWorld();
        world.Map(@"S:\", Folder(@"C:\Media"));

        using var hold = RootIdentityHold.Open(world, @"S:\");
        Assert.True(hold.IsHeld);
        Assert.Equal(1, world.OpenHandles, "the window holds exactly one handle");
        Assert.Equal(EvidenceStage.E1WindowStart, hold.E1.Stage);

        var end = hold.Finish();
        Assert.False(hold.IsHeld, "closed when the window's readings are done");
        Assert.Equal(EvidenceStage.E2WindowEndHeld, end.E2.Stage);
        Assert.Equal(EvidenceStage.E3WindowEndFresh, end.E3.Stage);

        // handle 1: opened, read for E1 and for E2, closed last. handle 2: opened for E3, read, closed at once.
        Assert.SequenceEqual(
            new[]
            {
                (Event.Open, 1, (EvidenceStage?)null),
                (Event.Read, 1, EvidenceStage.E1WindowStart),
                (Event.Read, 1, EvidenceStage.E2WindowEndHeld),
                (Event.Open, 2, null),
                (Event.Read, 2, EvidenceStage.E3WindowEndFresh),
                (Event.Close, 2, null),
                (Event.Close, 1, null),
            },
            world.Log.Select(e => (e.Kind, e.HandleId, e.Stage)));
        NoHandleLeaked(world);
    }

    [Test]
    public static void Every_open_uses_the_enumerated_path_and_the_canonical_path_is_never_opened()
    {
        var world = new FakeWorld();
        const string canonical = @"C:\Users\Jack\Media";
        world.Map(@"S:\", Folder(canonical));   // S: is a SUBST letter for the canonical folder

        Run(world, @"S:\");

        Assert.True(world.OpenedPaths.Any(), "something was opened");
        Assert.True(world.OpenedPaths.All(p => p == @"S:\"), "every open used the path the scanner enumerates");
        Assert.False(world.OpenedPaths.Contains(canonical), "no handle is opened on the canonical path for E0, E1, E2 or E3");
        Assert.Equal(3, world.OpenedPaths.Count(), "E0, the held handle (E1 and E2) and the fresh open (E3)");
        NoHandleLeaked(world);
    }

    [Test]
    public static void A_re_pointed_SUBST_letter_is_caught_by_the_fresh_open()
    {
        var world = new FakeWorld();
        var a = Folder(@"C:\Users\Jack\A", root: new FileId128(0x0006000000000010, 0));
        var b = Folder(@"C:\Users\Jack\B", root: new FileId128(0x0006000000000020, 0));
        world.Map(@"S:\", a);

        var result = Run(world, @"S:\", () => world.Map(@"S:\", b));

        Assert.Equal(ReverificationOutcome.IdentityChangedDuringScan, result.Outcome);
        // the held handle still sees the object the window began with (E2 unchanged); the fresh open sees the new target (E3)
        Assert.True(result.Failures.All(f => f.Stage == EvidenceStage.E3WindowEndFresh), "only E3 changed: " + string.Join(", ", result.Failures.Select(f => $"{f.Stage}/{f.Item}")));
        Assert.Contains(IdentityItem.CanonicalPath.ToString(), string.Join(",", result.Failures.Select(f => f.Item)));
        NoHandleLeaked(world);
    }

    [Test]
    public static void A_letter_re_pointed_away_and_back_is_not_detected_limitation_L_ID2()
    {
        // L-ID2 (documented): start and end evidence cannot see a letter re-pointed away and back while the original volume
        // stays mounted. This test asserts the limitation as the specification states it; no polling is added to hide it.
        var world = new FakeWorld();
        var a = Folder(@"C:\Users\Jack\A");
        var b = Folder(@"C:\Users\Jack\B");
        world.Map(@"S:\", a);

        var result = Run(world, @"S:\", () =>
        {
            world.Map(@"S:\", b);
            world.Map(@"S:\", a);
        });

        Assert.Equal(ReverificationOutcome.Verified, result.Outcome, "L-ID2: the capture looks eligible, and the documentation says so");
    }

    [Test]
    public static void A_source_folder_renamed_away_replaced_and_restored_is_not_detected_limitation_L_ID2()
    {
        // L-ID2 as extended by G0F-O05: the source folder itself renamed away, another folder given its name, and both put back
        // before the window closes. The held handle follows the ORIGINAL object (E2 reports its original name again) and the
        // fresh open of the path reaches the original again (E3): four equal readings, although the scan may have listed
        // the other folder in between. Documented, asserted, and not hidden by polling.
        var world = new FakeWorld();
        var original = Folder(@"C:\Media", root: new FileId128(0x0001000000000010, 0));
        var other = Folder(@"C:\Elsewhere", root: new FileId128(0x0001000000000020, 0));
        world.Map(@"C:\Media", original);

        var result = Run(world, @"C:\Media", () =>
        {
            original.Canonical = @"C:\Media.old";   // renamed away
            world.Unmap(@"C:\Media");
            other.Canonical = @"C:\Media";            // another folder takes its name
            world.Map(@"C:\Media", other);
            // ...the scan lists the wrong folder here...
            other.Canonical = @"C:\Elsewhere";        // and everything is put back
            original.Canonical = @"C:\Media";
            world.Map(@"C:\Media", original);
        });

        Assert.Equal(ReverificationOutcome.Verified, result.Outcome, "L-ID2: undone before the window closes, so nothing is left to see");
        NoHandleLeaked(world);
    }

    [Test]
    public static void A_source_folder_replaced_by_another_folder_that_stays_there_is_caught_on_every_filesystem()
    {
        // The same swap NOT undone is detected: the held handle reports the original's new name (E2) and a fresh open of the
        // path reaches the other folder (E3). It needs no root file ID, so it holds on FAT, exFAT and UDF too, where
        // FileIdInfo is not provided.
        foreach (var withFileId in new[] { true, false })
        {
            var world = new FakeWorld();
            var original = Folder(@"E:\Media", fs: withFileId ? "NTFS" : "FAT32", root: new FileId128(0x0001000000000010, 0));
            var other = Folder(@"E:\Elsewhere", fs: withFileId ? "NTFS" : "FAT32", root: new FileId128(0x0001000000000020, 0));
            if (!withFileId)
            {
                // FileIdInfo is not provided (Win32 87): no 64-bit serial and no root file ID, as measured on FAT32, exFAT and UDF
                foreach (var folder in new[] { original, other }) { folder.Serial64 = null; folder.RootId = null; }
            }
            world.Map(@"E:\Media", original);

            var result = Run(world, @"E:\Media", () =>
            {
                original.Canonical = @"E:\Media.old";
                world.Unmap(@"E:\Media");
                other.Canonical = @"E:\Media";
                world.Map(@"E:\Media", other);
            });

            Assert.Equal(ReverificationOutcome.IdentityChangedDuringScan, result.Outcome, withFileId ? "NTFS" : "FAT32");
            Assert.True(result.Failures.Any(f => f.Stage == EvidenceStage.E2WindowEndHeld && f.Item == IdentityItem.CanonicalPath), "E2: the object the window began with was renamed");
            NoHandleLeaked(world);
        }
    }

    [Test]
    public static void A_renamed_source_folder_is_caught_by_the_held_handle_and_the_fresh_open()
    {
        var world = new FakeWorld();
        var media = Folder(@"C:\Media");
        world.Map(@"C:\Media", media);

        var result = Run(world, @"C:\Media", () =>
        {
            media.Canonical = @"C:\Media2";   // the held handle follows the object
            world.Unmap(@"C:\Media");         // the old path no longer resolves
        });

        Assert.Equal(ReverificationOutcome.IdentityChangedDuringScan, result.Outcome);
        Assert.True(result.Failures.Any(f => f.Stage == EvidenceStage.E2WindowEndHeld && f.Item == IdentityItem.CanonicalPath && f.Kind == ReverificationFailureKind.Different), "E2: the object is now called C:\\Media2");
        Assert.True(result.Failures.Any(f => f.Stage == EvidenceStage.E3WindowEndFresh && f.Item == IdentityItem.CanonicalPath && f.Kind == ReverificationFailureKind.Missing), "E3: the original path cannot be opened");
        NoHandleLeaked(world);
    }

    [Test]
    public static void A_deleted_source_folder_is_caught()
    {
        var world = new FakeWorld();
        var media = Folder(@"C:\Media");
        world.Map(@"C:\Media", media);
        var result = Run(world, @"C:\Media", () =>
        {
            media.Canonical = @"C:\$Extend\$Deleted\000E00000003D12161BF036F";   // what NTFS reports for a deleted, still-open directory
            world.Unmap(@"C:\Media");
        });
        Assert.Equal(ReverificationOutcome.IdentityChangedDuringScan, result.Outcome);
        NoHandleLeaked(world);
    }

    [Test]
    public static void A_medium_swapped_at_the_same_letter_fails_through_the_invalidated_handle_and_the_new_serial()
    {
        var world = new FakeWorld();
        var original = Folder(@"E:\", serial64: 0x40AAE8B5AAE8A89C);
        var replacement = Folder(@"E:\", serial64: 0x20BED926BED8F4EE);
        world.Map(@"E:\", original);

        var result = Run(world, @"E:\", () =>
        {
            original.Invalidated = true;          // surprise removal: the held handle is dead
            world.Map(@"E:\", replacement);       // another medium appears at the same letter
        });

        Assert.Equal(ReverificationOutcome.IdentityChangedDuringScan, result.Outcome);
        Assert.True(result.Failures.Any(f => f.Stage == EvidenceStage.E2WindowEndHeld && f.Kind == ReverificationFailureKind.Missing), "E2: the held handle can no longer be read");
        Assert.True(result.Failures.Any(f => f.Stage == EvidenceStage.E3WindowEndFresh && f.Item == IdentityItem.VolumeSerial64 && f.Kind == ReverificationFailureKind.Different), "E3: another volume");
        NoHandleLeaked(world);
    }

    [Test]
    public static void A_volume_swapped_away_and_back_through_the_invalidated_handle_is_still_caught()
    {
        // A->B->A through the invalidated handle (ID-10): the original volume is back at the letter, so E3 reads it fine, but
        // the held handle died with the removal and E2 cannot read it.
        var world = new FakeWorld();
        var original = Folder(@"E:\", serial64: 0x40AAE8B5AAE8A89C);
        world.Map(@"E:\", original);
        var result = Run(world, @"E:\", () =>
        {
            original.Invalidated = true;
            world.Map(@"E:\", Folder(@"E:\", serial64: 0x40AAE8B5AAE8A89C));   // the same volume, mounted again
        });
        Assert.Equal(ReverificationOutcome.IdentityChangedDuringScan, result.Outcome);
        Assert.True(result.Failures.All(f => f.Stage == EvidenceStage.E2WindowEndHeld && f.Kind == ReverificationFailureKind.Missing),
            "only the dead held handle fails: " + string.Join(", ", result.Failures.Select(f => $"{f.Stage}/{f.Item}/{f.Kind}")));
        NoHandleLeaked(world);
    }

    [Test]
    public static void A_mapped_drive_re_pointed_to_another_share_is_caught_by_E3()
    {
        var world = new FakeWorld();
        var a = Folder(@"\\nas\a");
        var b = Folder(@"\\nas\b");
        world.Map(@"X:\", a);
        var result = Run(world, @"X:\", () => world.Map(@"X:\", b));
        Assert.Equal(ReverificationOutcome.IdentityChangedDuringScan, result.Outcome);
        Assert.Equal(EvidenceStage.E3WindowEndFresh, result.Failures.First().Stage);
        NoHandleLeaked(world);
    }

    [Test]
    public static void A_source_that_cannot_be_opened_at_preflight_cannot_be_saved()
    {
        var world = new FakeWorld();   // nothing is mapped
        var e0 = PreflightEvidence.Collect(world, @"Q:\");
        var result = Reverification.Evaluate(e0, e0.AtStage(EvidenceStage.E1WindowStart), e0.AtStage(EvidenceStage.E2WindowEndHeld), e0.AtStage(EvidenceStage.E3WindowEndFresh));
        Assert.Equal(ReverificationOutcome.SaveUnavailable, result.Outcome, "never a fallback to trusting the path");
        Assert.False(IdentityClassifier.Assess(e0).CanSave);
        Assert.Equal(EvidenceStatus.CallFailed, e0.HandleOpened.Status);
        Assert.Equal(2, e0.HandleOpened.Win32Error);
        NoHandleLeaked(world);
    }

    [Test]
    public static void A_path_that_cannot_be_opened_when_the_window_opens_still_yields_a_hold_that_fails_closed()
    {
        var world = new FakeWorld();
        var e0Folder = Folder(@"C:\Media");
        world.Map(@"C:\Media", e0Folder);
        var e0 = PreflightEvidence.Collect(world, @"C:\Media");

        world.Unmap(@"C:\Media");   // gone between preflight and the window opening
        using var hold = RootIdentityHold.Open(world, @"C:\Media");
        Assert.False(hold.E1.HandleOpened.IsAvailable);
        var end = hold.Finish();
        var result = Reverification.Evaluate(e0, hold.E1, end.E2, end.E3);
        Assert.Equal(ReverificationOutcome.IdentityChangedDuringScan, result.Outcome);
        NoHandleLeaked(world);
    }

    [Test]
    public static void A_cancelled_or_failed_scan_only_disposes_the_hold_and_releases_the_handle()
    {
        var world = new FakeWorld();
        world.Map(@"C:\Media", Folder(@"C:\Media"));
        var hold = RootIdentityHold.Open(world, @"C:\Media");
        Assert.Equal(1, world.OpenHandles);
        hold.Dispose();
        Assert.False(hold.IsHeld);
        Assert.Equal(0, world.OpenHandles);
        hold.Dispose();   // idempotent
        Assert.Equal(1, world.Log.Count(e => e.Kind == Event.Close));
        Assert.Throws<InvalidOperationException>(() => hold.Finish());
    }

    [Test]
    public static void The_window_readings_can_be_taken_only_once()
    {
        var world = new FakeWorld();
        world.Map(@"C:\Media", Folder(@"C:\Media"));
        using var hold = RootIdentityHold.Open(world, @"C:\Media");
        hold.Finish();
        var error = Assert.Throws<InvalidOperationException>(() => hold.Finish());
        Assert.Contains("already", error.Message);
        NoHandleLeaked(world);
    }

    [Test]
    public static void A_throwing_reading_never_leaks_the_handle()
    {
        // E1: the read throws inside Open
        var source = new ThrowingSource(throwOnRead: 1);
        Assert.Throws<IOException>(() => RootIdentityHold.Open(source, @"C:\Media"));
        Assert.Equal(0, source.OpenHandles, "the handle opened for E1 was closed before the exception left");

        // E2: the read through the held handle throws inside Finish
        var later = new ThrowingSource(throwOnRead: 2);
        using var hold = RootIdentityHold.Open(later, @"C:\Media");
        Assert.Equal(1, later.OpenHandles);
        Assert.Throws<IOException>(() => hold.Finish());
        Assert.False(hold.IsHeld, "the held handle is closed even though E2 threw");
        Assert.Equal(0, later.OpenHandles);

        // E3: the fresh handle's read throws
        var fresh = new ThrowingSource(throwOnRead: 3);
        using var third = RootIdentityHold.Open(fresh, @"C:\Media");
        Assert.Throws<IOException>(() => third.Finish());
        Assert.Equal(0, fresh.OpenHandles, "neither the held nor the fresh handle leaks");
    }

    [Test]
    public static void Every_reading_names_its_stage_and_the_path_it_was_opened_on()
    {
        var world = new FakeWorld();
        world.Map(@"S:\", Folder(@"C:\Media"));
        var e0 = PreflightEvidence.Collect(world, @"S:\");
        using var hold = RootIdentityHold.Open(world, @"S:\");
        var end = hold.Finish();
        Assert.SequenceEqual(
            new[] { EvidenceStage.E0Preflight, EvidenceStage.E1WindowStart, EvidenceStage.E2WindowEndHeld, EvidenceStage.E3WindowEndFresh },
            new[] { e0.Stage, hold.E1.Stage, end.E2.Stage, end.E3.Stage });
        Assert.True(new[] { e0, hold.E1, end.E2, end.E3 }.All(e => e.EnumeratedPath == @"S:\"));
    }

    private sealed class ThrowingSource : IVolumeEvidenceSource
    {
        private readonly int _throwOnRead;
        private int _reads;

        public ThrowingSource(int throwOnRead) => _throwOnRead = throwOnRead;

        public int OpenHandles { get; private set; }

        public IEvidenceHandle Open(string enumeratedPath)
        {
            OpenHandles++;
            return new ThrowingHandle(this, enumeratedPath);
        }

        private sealed class ThrowingHandle(ThrowingSource owner, string path) : IEvidenceHandle
        {
            private bool _disposed;
            public string OpenedPath => path;

            public VolumeEvidence Read(EvidenceStage stage)
            {
                if (++owner._reads == owner._throwOnRead) throw new IOException("the read failed");
                return Ev.Ntfs(@"C:\Media", stage: stage, entered: path);
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                owner.OpenHandles--;
            }
        }
    }
}

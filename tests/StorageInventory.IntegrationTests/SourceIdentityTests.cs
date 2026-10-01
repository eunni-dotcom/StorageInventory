using System.Security.Cryptography;
using StorageInventory.Core;
using StorageInventory.Core.Identity;
using StorageInventory.History.Identity;
using StorageInventory.Testing;

namespace StorageInventory.IntegrationTests;

/// <summary>
/// v1.1 C3 on real Windows volumes. TEST-I1: the evidence is read through zero-access handles on the system drive and on every
/// other fixed NTFS or ReFS volume the machine has (a hosted runner has C: and D:), cross-checked against the framework's own
/// drive queries. TEST-I5 (integration): a SUBST letter re-pointed during the window and a renamed or deleted source folder are
/// not eligible; away-and-back is the documented limitation L-ID2. Nothing here touches the network (INV-05); the mapped-drive and
/// removable-media experiments are the manual TEST-I4 and TEST-I6 evidence (<c>docs/v1.1-c3-implementation-evidence.md</c>).
/// </summary>
public static class SourceIdentityTests
{
    private static readonly IVolumeEvidenceSource Windows = WindowsEvidenceSource.Instance;

    /// <summary>The root directory of an NTFS volume: MFT record 5, sequence 5.</summary>
    private static readonly FileId128 NtfsRoot = new(0x0005000000000005, 0);

    private static VolumeEvidence Read(string path, EvidenceStage stage = EvidenceStage.E0Preflight)
    {
        using var handle = Windows.Open(path);
        return handle.Read(stage);
    }

    private static List<DriveInfo> FixedVolumes(params string[] formats)
    {
        var volumes = new List<DriveInfo>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType == DriveType.Fixed && drive.IsReady && formats.Contains(drive.DriveFormat, StringComparer.OrdinalIgnoreCase)) volumes.Add(drive);
            }
            catch (IOException)
            {
                // a drive that goes away while being listed is not one of ours
            }
        }
        return volumes;
    }

    /// <summary>Everything TEST-I1 asserts about a whole-volume reading of a fixed NTFS or ReFS volume.</summary>
    private static void AssertWholeVolumeEvidence(DriveInfo drive, VolumeEvidence e)
    {
        var root = drive.RootDirectory.FullName;
        var name = drive.Name;
        Assert.True(e.HandleOpened.IsAvailable, name + ": the zero-access directory handle opens");
        Assert.Equal("CreateFileW", e.HandleOpened.Call, name);
        Assert.Equal(SourceKind.LocalVolume, e.Kind, name);
        Assert.Equal(root, e.CanonicalPath.Value, name + ": the canonical path of the opened root");
        Assert.Equal(drive.DriveFormat, e.FileSystemName.Value, name + ": the filesystem name agrees with the framework's own query");
        Assert.Equal(drive.VolumeLabel, e.VolumeLabel.Value, name + ": label");
        Assert.Equal(drive.TotalSize, e.CapacityBytes.Value, name + ": capacity");
        Assert.True(e.FreeBytes.IsAvailable && e.FreeBytes.Value > 0 && e.FreeBytes.Value <= e.CapacityBytes.Value, name + ": free space is plausible");
        Assert.Equal(root, e.MountPoint.Value, name + ": mount point");
        Assert.True(e.VolumeSerial32.IsAvailable && e.VolumeSerial32.Value != 0, name + ": a non-zero 32-bit serial");
        Assert.True(e.VolumeSerial64.IsAvailable && e.VolumeSerial64.Value != 0, name + ": a non-zero 64-bit serial (FileIdInfo)");
        Assert.True(e.RootDirectoryFileId.IsAvailable, name + ": the root directory's file ID");

        var assessment = IdentityClassifier.Assess(e);
        Assert.Equal(IdentityConfidence.Strong, assessment.Confidence, $"{name}: {drive.DriveFormat} with a 64-bit serial is Strong");
        Assert.True(assessment.CanSave, name + ": the minimum re-verification evidence is present");
        Assert.Equal(@"\", assessment.RootInVolume, name + ": a whole volume is the root of its volume");
        Assert.Null(assessment.NetworkRoot);
    }

    [Test]
    public static void TEST_I1_the_system_drive_is_read_through_a_zero_access_handle()
    {
        var drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
        AssertWholeVolumeEvidence(drive, Read(drive.RootDirectory.FullName));
    }

    [Test]
    public static void TEST_I1_every_fixed_NTFS_or_ReFS_volume_gives_zero_access_evidence()
    {
        var volumes = FixedVolumes("NTFS", "ReFS");
        Assert.True(volumes.Count >= 1, "at least the system drive");
        foreach (var drive in volumes) AssertWholeVolumeEvidence(drive, Read(drive.RootDirectory.FullName));
    }

    [Test]
    public static void TEST_I1_two_volumes_have_different_serials_although_their_root_directory_IDs_are_equal()
    {
        var readings = FixedVolumes("NTFS").Select(v => (Drive: v, Evidence: Read(v.RootDirectory.FullName))).ToList();
        if (readings.Count < 2) Assert.Skip("only one fixed NTFS volume here; TEST-I1's second-volume half needs another (a hosted runner has C: and D:)");

        // different volumes are told apart by their serials...
        Assert.Equal(readings.Count, readings.Select(r => r.Evidence.VolumeSerial64.Value).Distinct().Count(), "64-bit serials: " + string.Join(", ", readings.Select(r => $"{r.Drive.Name} {r.Evidence.VolumeSerial64.Value:X16}")));
        Assert.Equal(readings.Count, readings.Select(r => r.Evidence.VolumeSerial32.Value).Distinct().Count(), "32-bit serials");

        // ...never by the root directory's file ID: on NTFS every volume's root is MFT record 5 (Q-13, measured here)
        Assert.Equal(1, readings.Select(r => r.Evidence.RootDirectoryFileId.Value).Distinct().Count(), "the root IDs: " + string.Join(", ", readings.Select(r => $"{r.Drive.Name} {r.Evidence.RootDirectoryFileId.Value}")));
        Assert.Equal(NtfsRoot, readings[0].Evidence.RootDirectoryFileId.Value, "NTFS's root directory is MFT record 5");
    }

    [Test]
    public static void TEST_I1_a_subfolder_source_reports_its_exact_root_and_its_own_directory_ID()
    {
        var work = TestEnvironment.NewWorkFolder("identity_sub");
        try
        {
            var folder = Path.Combine(work, "Media", "Music");
            Directory.CreateDirectory(folder);

            var e = Read(folder);
            Assert.True(e.CanonicalPath.IsAvailable, "canonical path");
            Assert.True(SourceLocation.TryDerive(e.CanonicalPath.Value!, out var location));
            Assert.Equal(SourceKind.LocalVolume, location.Kind);
            Assert.True(location.RootInVolume.StartsWith('\\') && location.RootInVolume.EndsWith(@"\Media\Music", StringComparison.Ordinal),
                "the exact root inside the volume, as the filesystem spells it: " + location.RootInVolume);
            Assert.True(e.RootDirectoryFileId.Value != NtfsRoot, "a subfolder has a file ID of its own, not the volume root's");

            // information for "the source folder was recreated since the older snapshot" (§7.6): stable while it exists,
            // different once it is deleted and created again; the volume's identity items do not move
            Assert.Equal(e.RootDirectoryFileId.Value, Read(folder).RootDirectoryFileId.Value, "stable while the folder exists");
            Directory.Delete(folder);
            Directory.CreateDirectory(folder);
            var recreated = Read(folder);
            Assert.True(recreated.RootDirectoryFileId.Value != e.RootDirectoryFileId.Value, "a recreated folder is a different directory object");
            Assert.Equal(e.CanonicalPath.Value, recreated.CanonicalPath.Value, "the exact path is the same");
            Assert.Equal(e.VolumeSerial64.Value, recreated.VolumeSerial64.Value, "same volume");
            Assert.Equal(e.VolumeSerial32.Value, recreated.VolumeSerial32.Value);
        }
        finally
        {
            TestEnvironment.RemoveTree(work);
        }
    }

    [Test]
    public static void TEST_I1_a_path_that_cannot_be_opened_yields_no_invented_values()
    {
        var missing = Path.Combine(TestEnvironment.WorkRoot, "does-not-exist-" + Guid.NewGuid().ToString("N"), "nor-this");
        var e = Read(missing);

        Assert.False(e.HandleOpened.IsAvailable);
        Assert.Equal(EvidenceStatus.CallFailed, e.HandleOpened.Status);
        Assert.True(e.HandleOpened.Win32Error is 2 or 3, "ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND, was " + e.HandleOpened.Win32Error);
        Assert.False(e.CanonicalPath.IsAvailable || e.FileSystemName.IsAvailable || e.VolumeSerial32.IsAvailable || e.VolumeSerial64.IsAvailable
                     || e.RootDirectoryFileId.IsAvailable || e.VolumeLabel.IsAvailable || e.MountPoint.IsAvailable || e.CapacityBytes.IsAvailable || e.FreeBytes.IsAvailable,
            "with no handle nothing is available, and nothing is made up");
        Assert.Equal(EvidenceStatus.Unavailable, e.CanonicalPath.Status, "the items say why: nothing was attempted");
        Assert.False(IdentityClassifier.Assess(e).CanSave, "never a fallback to trusting the path (ID-13)");
    }

    [Test]
    public static void TEST_I1_capturing_evidence_changes_nothing_in_the_tree()
    {
        var work = TestEnvironment.NewWorkFolder("identity_readonly");
        try
        {
            var tree = Path.Combine(work, "tree");
            Directory.CreateDirectory(Path.Combine(tree, "a", "b"));
            File.WriteAllText(Path.Combine(tree, "a", "one.txt"), "one");
            File.WriteAllText(Path.Combine(tree, "a", "b", "two.txt"), "two!");
            Snapshot.Settle(work);
            var before = Snapshot.Take(work, trueValues: true);

            var e0 = PreflightEvidence.Collect(Windows, tree);
            using (var hold = RootIdentityHold.Open(Windows, tree)) hold.Finish();
            Read(Path.Combine(tree, "a"));

            Assert.Equal(before, Snapshot.Take(work, trueValues: true), "no file or folder was created, changed, renamed or deleted, and no timestamp moved");
            Assert.True(e0.CanonicalPath.IsAvailable);
        }
        finally
        {
            TestEnvironment.RemoveTree(work);
        }
    }

    // ---- TEST-I5, integration: the window over a real source ----

    private static (ReverificationResult Result, VolumeEvidence E0, VolumeEvidence E1, WindowEndReadings End) Window(string enumeratedPath, Action? duringTheWindow = null)
    {
        var e0 = PreflightEvidence.Collect(Windows, enumeratedPath);
        using var hold = RootIdentityHold.Open(Windows, enumeratedPath);
        duringTheWindow?.Invoke();
        var end = hold.Finish();
        return (Reverification.Evaluate(e0, hold.E1, end.E2, end.E3), e0, hold.E1, end);
    }

    /// <summary>A SUBST drive letter for the test's own use. Needs no administrator rights. Skips the test where SUBST is unavailable.</summary>
    private sealed class SubstDrive : IDisposable
    {
        public SubstDrive(string target)
        {
            Letter = TestEnvironment.FreeDriveLetter() ?? throw new SkipException("no free drive letter for SUBST");
            TestEnvironment.Run("subst.exe", $"{Letter}:", target);
            if (!Directory.Exists(Root)) throw new SkipException("SUBST is unavailable here");
        }

        public char Letter { get; }
        public string Root => $@"{Letter}:\";

        public void Repoint(string target)
        {
            TestEnvironment.Run("subst.exe", $"{Letter}:", "/D");
            TestEnvironment.Run("subst.exe", $"{Letter}:", target);
        }

        public void Dispose() => TestEnvironment.Run("subst.exe", $"{Letter}:", "/D");
    }

    [Test]
    public static void TEST_I5_a_stable_source_is_verified_from_start_to_end()
    {
        var work = TestEnvironment.NewWorkFolder("identity_stable");
        try
        {
            var folder = Path.Combine(work, "Media");
            Directory.CreateDirectory(folder);
            var (result, e0, e1, end) = Window(folder);
            Assert.Equal(ReverificationOutcome.Verified, result.Outcome, string.Join("; ", result.Failures.Select(f => $"{f.Stage}/{f.Item}/{f.Kind}")));
            Assert.Equal(folder, e0.EnumeratedPath);
            Assert.True(new[] { e1, end.E2, end.E3 }.All(r => r.EnumeratedPath == folder), "every reading was taken on the enumerated path");
        }
        finally
        {
            TestEnvironment.RemoveTree(work);
        }
    }

    [Test]
    public static void TEST_I5_a_SUBST_letter_re_pointed_during_the_window_is_not_eligible()
    {
        var work = TestEnvironment.NewWorkFolder("identity_subst");
        try
        {
            var a = Path.Combine(work, "A");
            var b = Path.Combine(work, "B");
            Directory.CreateDirectory(Path.Combine(a, "sub"));
            Directory.CreateDirectory(Path.Combine(b, "sub"));
            using var subst = new SubstDrive(a);

            var (result, e0, e1, end) = Window(subst.Root, () => subst.Repoint(b));

            Assert.Equal(ReverificationOutcome.IdentityChangedDuringScan, result.Outcome, "re-pointing the letter must be detected");
            // what actually happens (recorded here, not assumed): the held handle keeps reporting the object the window began with,
            // and the fresh open of the letter leads to the new target, so E3 is the reading that detects it
            Assert.Equal(e1.CanonicalPath.Value, end.E2.CanonicalPath.Value, "E2: the held handle still reaches the original folder");
            Assert.True(end.E3.CanonicalPath.Value != e1.CanonicalPath.Value && end.E3.CanonicalPath.Value!.EndsWith(@"\B", StringComparison.Ordinal), "E3: the letter now leads to B: " + end.E3.CanonicalPath.Value);
            Assert.True(result.Failures.Any(f => f.Stage == EvidenceStage.E3WindowEndFresh && f.Item == IdentityItem.CanonicalPath), "E3 reports the canonical path change");
            Assert.True(new[] { e0, e1, end.E2, end.E3 }.All(r => r.EnumeratedPath == subst.Root), "every open used the letter the scanner enumerates, never the canonical path");
        }
        finally
        {
            TestEnvironment.RemoveTree(work);
        }
    }

    [Test]
    public static void TEST_I5_a_SUBST_letter_re_pointed_away_and_back_is_not_detected_limitation_L_ID2()
    {
        // L-ID2: no start-and-end evidence can see this. The test pins the limitation as documented; no polling is added.
        var work = TestEnvironment.NewWorkFolder("identity_subst_back");
        try
        {
            var a = Path.Combine(work, "A");
            var b = Path.Combine(work, "B");
            Directory.CreateDirectory(a);
            Directory.CreateDirectory(b);
            using var subst = new SubstDrive(a);

            var (result, _, _, _) = Window(subst.Root, () =>
            {
                subst.Repoint(b);
                subst.Repoint(a);
            });

            Assert.Equal(ReverificationOutcome.Verified, result.Outcome, "L-ID2: away and back looks like no change, and the help text says so");
        }
        finally
        {
            TestEnvironment.RemoveTree(work);
        }
    }

    [Test]
    public static void TEST_I5_a_source_folder_renamed_during_the_window_is_not_eligible()
    {
        var work = TestEnvironment.NewWorkFolder("identity_rename");
        try
        {
            var source = Path.Combine(work, "Source");
            var renamed = Path.Combine(work, "Source2");
            Directory.CreateDirectory(Path.Combine(source, "inner"));

            var (result, _, e1, end) = Window(source, () => Directory.Move(source, renamed));   // the held handle does not stop the rename

            Assert.Equal(ReverificationOutcome.IdentityChangedDuringScan, result.Outcome);
            // recorded behaviour: the held handle follows the object, so E2 reports the new name; the original path no longer opens, so E3 fails
            Assert.True(end.E2.CanonicalPath.IsAvailable && end.E2.CanonicalPath.Value != e1.CanonicalPath.Value && end.E2.CanonicalPath.Value!.EndsWith(@"\Source2", StringComparison.Ordinal),
                "E2 (held handle): " + end.E2.CanonicalPath);
            Assert.False(end.E3.HandleOpened.IsAvailable, "E3 (fresh open of the original path) cannot open it");
            Assert.True(end.E3.HandleOpened.Win32Error is 2 or 3, "E3 open error " + end.E3.HandleOpened.Win32Error);
            Assert.True(result.Failures.Any(f => f.Stage == EvidenceStage.E2WindowEndHeld) && result.Failures.Any(f => f.Stage == EvidenceStage.E3WindowEndFresh), "both readings fail");
        }
        finally
        {
            TestEnvironment.RemoveTree(work);
        }
    }

    [Test]
    public static void TEST_I5_a_source_folder_renamed_only_in_letter_case_is_not_eligible()
    {
        var work = TestEnvironment.NewWorkFolder("identity_case");
        try
        {
            var source = Path.Combine(work, "CaseSource");
            Directory.CreateDirectory(source);
            var (result, _, e1, end) = Window(source, () =>
            {
                Directory.Move(source, source + "_tmp");
                Directory.Move(source + "_tmp", Path.Combine(work, "casesource"));
            });
            Assert.Equal(ReverificationOutcome.IdentityChangedDuringScan, result.Outcome, "the canonical path is compared exactly (ID-02): a case-only rename is a rename");
            Assert.True(end.E2.CanonicalPath.Value!.EndsWith(@"\casesource", StringComparison.Ordinal), "E2 reports the new spelling: " + end.E2.CanonicalPath.Value);
            Assert.True(e1.CanonicalPath.Value!.EndsWith(@"\CaseSource", StringComparison.Ordinal));
        }
        finally
        {
            TestEnvironment.RemoveTree(work);
        }
    }

    [Test]
    public static void TEST_I5_a_source_folder_deleted_during_the_window_is_not_eligible()
    {
        var work = TestEnvironment.NewWorkFolder("identity_delete");
        try
        {
            var source = Path.Combine(work, "Source");
            Directory.CreateDirectory(source);
            var (result, _, _, end) = Window(source, () => Directory.Delete(source));   // the held handle does not stop the delete either
            Assert.Equal(ReverificationOutcome.IdentityChangedDuringScan, result.Outcome);
            Assert.False(end.E3.HandleOpened.IsAvailable, "the path no longer opens");
        }
        finally
        {
            TestEnvironment.RemoveTree(work);
        }
    }

    // ---- TEST-I6 support: what holding the handle does and does not do ----

    [Test]
    public static void The_scan_and_its_reports_are_unchanged_while_the_root_handle_is_held()
    {
        // Existing behaviour is preserved: C3 adds no identity work to a v1 scan, and holding the zero-access root handle
        // through one changes neither the traversal nor a byte of the reports.
        var work = TestEnvironment.NewWorkFolder("identity_scan");
        try
        {
            var tree = Path.Combine(work, "tree");
            Directory.CreateDirectory(Path.Combine(tree, "a", "b"));
            Directory.CreateDirectory(Path.Combine(tree, "empty"));
            for (var i = 0; i < 12; i++) File.WriteAllBytes(Path.Combine(tree, "a", $"file{i:00}.bin"), new byte[i * 37]);
            File.WriteAllText(Path.Combine(tree, "a", "b", "note.txt"), "hello");

            StorageScanResult Scan(string output) => new InventoryScanner().Scan(new StorageScanOptions { RootPath = tree, OutputPath = output, SortFiles = true });
            var plain = Scan(Path.Combine(work, "out_plain"));

            var e0 = PreflightEvidence.Collect(Windows, tree);
            StorageScanResult held;
            ReverificationResult verdict;
            using (var hold = RootIdentityHold.Open(Windows, tree))
            {
                held = Scan(Path.Combine(work, "out_held"));
                var end = hold.Finish();
                verdict = Reverification.Evaluate(e0, hold.E1, end.E2, end.E3);
            }

            Assert.True(plain.Finished && held.Finished, "both scans finished");
            Assert.Equal(plain.Totals, held.Totals, "totals");
            foreach (var (name, a, b) in new[] { ("Files", plain.Reports!.FilesCsv, held.Reports!.FilesCsv), ("Folders", plain.Reports.FoldersCsv, held.Reports.FoldersCsv), ("ScanErrors", plain.Reports.ErrorsCsv, held.Reports.ErrorsCsv) })
            {
                Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(a))), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(b))), name + " report bytes");
            }
            Assert.Equal(ReverificationOutcome.Verified, verdict.Outcome, "an undisturbed scan is eligible as far as identity goes");
        }
        finally
        {
            TestEnvironment.RemoveTree(work);
        }
    }

    [Test]
    public static void The_held_handle_does_not_block_the_folder_it_holds_from_being_renamed_or_deleted_and_is_released_on_dispose()
    {
        var work = TestEnvironment.NewWorkFolder("identity_effects");
        try
        {
            var folder = Path.Combine(work, "Held");
            Directory.CreateDirectory(folder);
            using (var hold = RootIdentityHold.Open(Windows, folder))
            {
                Assert.True(hold.IsHeld);
                Directory.Move(folder, folder + "Moved");   // sharing allows delete: a held handle pins the volume, not the folder
            }
            // after Dispose the handle is closed: the (empty) folder can be deleted and nothing keeps it alive
            Directory.Delete(folder + "Moved");
            Assert.False(Directory.Exists(folder + "Moved"));
        }
        finally
        {
            TestEnvironment.RemoveTree(work);
        }
    }
}

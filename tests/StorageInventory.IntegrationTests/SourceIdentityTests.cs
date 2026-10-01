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
    public static void TEST_I1_a_source_deeper_than_MAX_PATH_is_read_through_the_extended_path()
    {
        var work = TestEnvironment.NewWorkFolder("identity_long");
        try
        {
            var deep = Path.Combine(work, new string('a', 90), new string('b', 90), new string('c', 90), "Source");
            Directory.CreateDirectory(deep);
            Assert.True(deep.Length > 260, "the path is longer than MAX_PATH: " + deep.Length);

            var e = Read(deep);
            Assert.True(e.HandleOpened.IsAvailable, "the zero-access open works beyond MAX_PATH");
            Assert.True(e.CanonicalPath.Value!.Length > 260);
            Assert.True(SourceLocation.TryDerive(e.CanonicalPath.Value!, out var location));
            Assert.True(location.RootInVolume.EndsWith(@"\Source", StringComparison.Ordinal) && location.RootInVolume.Length > 260, "the exact root is kept in full");
            Assert.True(e.MountPoint.IsAvailable && e.CapacityBytes.IsAvailable, "the path queries work on a long path too");
            Assert.True(IdentityClassifier.Assess(e).CanSave);
        }
        finally
        {
            TestEnvironment.RemoveTree(work);
        }
    }

    [Test]
    public static void TEST_I1_the_root_is_kept_as_the_exact_UTF16_the_filesystem_spells_it()
    {
        var work = TestEnvironment.NewWorkFolder("identity_unicode");
        try
        {
            // Korean, a supplementary-plane emoji (a surrogate pair), a precomposed and a decomposed accent, and mixed case
            var names = new[] { "음악", "Music 🎵", "Café", "Café", "MiXeD" };
            foreach (var name in names)
            {
                var folder = Path.Combine(work, name);
                Directory.CreateDirectory(folder);
                var e = Read(folder);
                Assert.True(SourceLocation.TryDerive(e.CanonicalPath.Value!, out var location), name);
                Assert.True(location.RootInVolume.EndsWith("\\" + name, StringComparison.Ordinal), $"'{name}' is preserved code unit for code unit: {location.RootInVolume}");
            }
            // 'Café' and 'Café' (precomposed and decomposed) are two folders with different exact roots, so two different sources
            var a = SourceLocation.TryDerive(Read(Path.Combine(work, "Café")).CanonicalPath.Value!, out var la);
            var b = SourceLocation.TryDerive(Read(Path.Combine(work, "Café")).CanonicalPath.Value!, out var lb);
            Assert.True(a && b && la.RootInVolume != lb.RootInVolume, "no normalisation: the two spellings stay different");
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
    public static void The_held_handle_does_not_block_the_folder_it_holds_from_being_renamed_or_deleted()
    {
        // The handle shares read, write and delete, so it pins the volume (the orderly dismount, measured by the experiments),
        // not the folder. This says nothing about whether the handle is released: see the next test.
        var work = TestEnvironment.NewWorkFolder("identity_effects");
        try
        {
            var folder = Path.Combine(work, "Held");
            Directory.CreateDirectory(folder);
            using var hold = RootIdentityHold.Open(Windows, folder);
            Assert.True(hold.IsHeld);
            Directory.Move(folder, folder + "Moved");
            Directory.Delete(folder + "Moved");
            Assert.False(Directory.Exists(folder + "Moved"), "a held zero-access handle blocks neither a rename nor a delete");
        }
        finally
        {
            TestEnvironment.RemoveTree(work);
        }
    }

    private static int ProcessHandleCount()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return process.HandleCount;
    }

    [Test]
    public static void The_real_root_handle_is_closed_however_the_window_ends()
    {
        // ID-10 and Q-19 depend on the held handle being released when the window ends, because the only accepted side effect is
        // that it blocks "Safely remove" during a scan. The handle has no access rights and shares everything, so nothing a test
        // can try on the FOLDER shows whether it is still open (a rename, a delete and a listing all succeed either way). What
        // does show it is the process's own handle count, in three parts:
        //   1. a control, so the count is known to be able to fail: windows that are held DO raise it;
        //   2. disposing them gives the handles back;
        //   3. many windows, each ending a different way, leave the count where it was.
        // The collector is held off while counting, so a handle that was leaked cannot be quietly finalised in the middle of the
        // measurement and make a broken release look fine.
        const int control = 40;
        const int windows = 160;
        const int slack = 12;   // other threads open and close handles of their own

        var work = TestEnvironment.NewWorkFolder("identity_handles");
        var noGcRegion = false;
        try
        {
            var folder = Path.Combine(work, "Held");
            var moved = folder + "Moved";
            Directory.CreateDirectory(folder);

            for (var i = 0; i < 3; i++)
            {
                using var warmUp = RootIdentityHold.Open(Windows, folder);   // JIT, lazily created runtime handles
                warmUp.Finish();
            }
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            try { noGcRegion = GC.TryStartNoGCRegion(32 * 1024 * 1024); }
            catch (ArgumentOutOfRangeException) { /* a region this size is not allowed here: measure without it */ }

            var baseline = ProcessHandleCount();

            // 1. the control
            var held = new List<RootIdentityHold>();
            for (var i = 0; i < control; i++) held.Add(RootIdentityHold.Open(Windows, folder));
            var raised = ProcessHandleCount() - baseline;
            Assert.True(raised >= control - slack, $"the control: {control} held windows raised the handle count by {raised}; the measurement could not detect a leak");

            // 2. disposing gives them back
            foreach (var hold in held) hold.Dispose();
            var afterDispose = ProcessHandleCount() - baseline;
            Assert.True(afterDispose <= slack, $"{control} disposed windows left {afterDispose} handles open (baseline {baseline})");

            // 3. every way a window can end
            for (var i = 0; i < windows; i++)
            {
                switch (i % 4)
                {
                    case 0:   // the normal ending: Finish reads E2 and E3 and closes the held handle itself
                    {
                        using var hold = RootIdentityHold.Open(Windows, folder);
                        hold.Finish();
                        break;
                    }
                    case 1:   // a cancelled or failed scan never reaches Finish: Dispose closes the held handle
                    {
                        using var hold = RootIdentityHold.Open(Windows, folder);
                        break;
                    }
                    case 2:   // disposed twice (idempotent)
                    {
                        var hold = RootIdentityHold.Open(Windows, folder);
                        hold.Dispose();
                        hold.Dispose();
                        break;
                    }
                    default:  // the source moved during the window: E3's fresh open fails, and the held handle is still closed
                    {
                        using var hold = RootIdentityHold.Open(Windows, folder);
                        Directory.Move(folder, moved);
                        try { hold.Finish(); }
                        finally { Directory.Move(moved, folder); }
                        break;
                    }
                }
            }
            var afterMany = ProcessHandleCount() - baseline;
            Assert.True(afterMany <= slack, $"{windows} windows (Finish, abandoned, disposed twice, source moved) left {afterMany} handles open (baseline {baseline}); each leak would add one");
        }
        finally
        {
            if (noGcRegion)
            {
                try { GC.EndNoGCRegion(); }
                catch (InvalidOperationException) { /* the region had already ended */ }
            }
            TestEnvironment.RemoveTree(work);
        }
    }

    [Test]
    public static void TEST_I1_the_handle_the_product_opens_is_granted_no_list_read_write_or_delete_right()
    {
        // TEST-I1 asks for evidence "through zero-access handles" and that no write access is requested. The product passes
        // the literal 0 (audited); this measures what Windows then actually granted, through the handle the product's own open
        // function returns, and shows with a contrast handle that the probe does see rights when they are there.
        var work = TestEnvironment.NewWorkFolder("identity_access");
        try
        {
            var folder = Path.Combine(work, "Source");
            Directory.CreateDirectory(folder);

            using var product = StorageInventory.Core.Paths.NativeMethods.TryOpenDirectoryZeroAccess(folder, out var error)
                ?? throw new AssertionException("the product's zero-access handle did not open: Win32 " + error);
            var granted = GrantedAccessProbe.GrantedTo(product);
            Assert.Equal(0u, granted & ~(GrantedAccessProbe.Synchronize | GrantedAccessProbe.FileReadAttributes),
                $"only SYNCHRONIZE and FILE_READ_ATTRIBUTES (which CreateFileW itself adds) were granted, not 0x{granted:X8}");
            Assert.True((granted & GrantedAccessProbe.FileReadAttributes) != 0, "the right to read attributes is what the queries need");

            var forbidden = GrantedAccessProbe.FileListDirectory | GrantedAccessProbe.FileAddFile | GrantedAccessProbe.FileAddSubdirectory | GrantedAccessProbe.FileReadEa
                | GrantedAccessProbe.FileWriteEa | GrantedAccessProbe.FileTraverse | GrantedAccessProbe.FileDeleteChild | GrantedAccessProbe.FileWriteAttributes
                | GrantedAccessProbe.Delete | GrantedAccessProbe.ReadControl | GrantedAccessProbe.WriteDac | GrantedAccessProbe.WriteOwner;
            Assert.Equal(0u, granted & forbidden, "no list, read, write, delete or security right");

            using var contrast = GrantedAccessProbe.OpenForReading(StorageInventory.Core.Paths.NativeMethods.ToExtendedPath(folder));
            var wider = GrantedAccessProbe.GrantedTo(contrast);
            Assert.True((wider & GrantedAccessProbe.FileListDirectory) != 0 && (wider & GrantedAccessProbe.ReadControl) != 0,
                $"the contrast: a handle opened for reading DOES carry list and read-control rights (0x{wider:X8}), so the probe can see rights");
        }
        finally
        {
            TestEnvironment.RemoveTree(work);
        }
    }

    // ---- L-ID2 for the source folder itself (G0F-O05) ----

    [Test]
    public static void TEST_I5_a_source_folder_renamed_away_replaced_and_restored_is_not_detected_limitation_L_ID2()
    {
        // L-ID2 as extended by G0F-O05: the source folder renamed away, another folder given its name, and everything put back
        // before the window closes. The held handle follows the original object and the fresh open of the path reaches it again,
        // so every reading equals E0 although the scan could have listed the other folder in between. Pinned as documented.
        var work = TestEnvironment.NewWorkFolder("identity_swap_back");
        try
        {
            var source = Path.Combine(work, "Source");
            var other = Path.Combine(work, "Other");
            Directory.CreateDirectory(Path.Combine(source, "inner"));
            Directory.CreateDirectory(Path.Combine(other, "inner"));

            var (result, _, _, _) = Window(source, () =>
            {
                Directory.Move(source, source + ".old");   // renamed away: the held handle goes with it
                Directory.Move(other, source);             // another folder takes the name
                Directory.Move(source, other);             // ...and everything is put back
                Directory.Move(source + ".old", source);
            });

            Assert.Equal(ReverificationOutcome.Verified, result.Outcome, "L-ID2: a replacement that is undone before the window closes leaves nothing to see");
        }
        finally
        {
            TestEnvironment.RemoveTree(work);
        }
    }

    [Test]
    public static void TEST_I5_a_source_folder_replaced_by_another_folder_that_stays_is_not_eligible()
    {
        var work = TestEnvironment.NewWorkFolder("identity_swap");
        try
        {
            var source = Path.Combine(work, "Source");
            var other = Path.Combine(work, "Other");
            Directory.CreateDirectory(Path.Combine(source, "inner"));
            Directory.CreateDirectory(Path.Combine(other, "inner"));

            var (result, _, e1, end) = Window(source, () =>
            {
                Directory.Move(source, source + ".old");
                Directory.Move(other, source);
            });

            Assert.Equal(ReverificationOutcome.IdentityChangedDuringScan, result.Outcome);
            // recorded behaviour: the held handle follows the ORIGINAL folder, so E2 reports its new name on every filesystem...
            Assert.True(end.E2.CanonicalPath.Value!.EndsWith(@"\Source.old", StringComparison.Ordinal), "E2: " + end.E2.CanonicalPath.Value);
            Assert.True(result.Failures.Any(f => f.Stage == EvidenceStage.E2WindowEndHeld && f.Item == IdentityItem.CanonicalPath), "E2 reports the original folder's new name");
            // ...while the fresh open reaches the other folder under the same name: the same canonical path, and where the filesystem
            // provides a root file ID (NTFS, ReFS) a different directory
            Assert.Equal(e1.CanonicalPath.Value, end.E3.CanonicalPath.Value, "E3: the path leads to a folder with the same name");
            if (e1.RootDirectoryFileId.IsAvailable)
            {
                Assert.True(result.Failures.Any(f => f.Stage == EvidenceStage.E3WindowEndFresh && f.Item == IdentityItem.RootDirectoryFileId), "E3: a different directory object (its root file ID)");
            }
        }
        finally
        {
            TestEnvironment.RemoveTree(work);
        }
    }
}

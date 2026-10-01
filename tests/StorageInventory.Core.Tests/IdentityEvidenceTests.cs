using StorageInventory.Core.Identity;
using StorageInventory.Core.Paths;
using StorageInventory.Testing;

namespace StorageInventory.Core.Tests;

/// <summary>
/// v1.1 C3, Core side: the pure parts of the identity evidence (§7.1, §7.4). Splitting a canonical path into a volume or
/// share and the root's EXACT path inside it, deciding local or network from where Windows says the object is, and the
/// small value types. Anything that needs a real handle is in the integration tests.
/// </summary>
public static class IdentityEvidenceTests
{
    [Test]
    public static void A_canonical_path_splits_into_a_volume_or_share_and_the_exact_root_inside_it()
    {
        // canonical path, kind, network root, root in volume
        var cases = new (string Canonical, SourceKind Kind, string? NetworkRoot, string Root)[]
        {
            (@"C:\", SourceKind.LocalVolume, null, @"\"),
            (@"C:\Media", SourceKind.LocalVolume, null, @"\Media"),
            (@"C:\Media\Music", SourceKind.LocalVolume, null, @"\Media\Music"),
            (@"C:\Media\", SourceKind.LocalVolume, null, @"\Media"),                       // a trailing separator is not part of the root
            (@"c:\media", SourceKind.LocalVolume, null, @"\media"),                        // case is preserved exactly, never folded
            (@"E:\Ünï\Ṁusic ♪", SourceKind.LocalVolume, null, @"\Ünï\Ṁusic ♪"),           // nor is anything normalised
            (@"C:\a\\b", SourceKind.LocalVolume, null, @"\a\\b"),                          // interior separators are kept as they are
            (@"\\nas\share", SourceKind.Network, @"\\nas\share", @"\"),
            (@"\\nas\share\", SourceKind.Network, @"\\nas\share", @"\"),
            (@"\\nas\share\Media\Music", SourceKind.Network, @"\\nas\share", @"\Media\Music"),
            (@"\\NAS\Share\media", SourceKind.Network, @"\\NAS\Share", @"\media"),         // server and share spellings are kept exactly
            (@"\\nas.local\share", SourceKind.Network, @"\\nas.local\share", @"\"),
            (@"\\192.168.1.5\media\x", SourceKind.Network, @"\\192.168.1.5\media", @"\x"),
            (@"\\localhost\C$", SourceKind.Network, @"\\localhost\C$", @"\"),              // as measured for a mapped drive letter
        };
        foreach (var (canonical, kind, networkRoot, root) in cases)
        {
            Assert.True(SourceLocation.TryDerive(canonical, out var location), $"'{canonical}' should split");
            Assert.Equal(kind, location.Kind, canonical);
            Assert.Equal(networkRoot, location.NetworkRoot, canonical);
            Assert.Equal(root, location.RootInVolume, canonical);
        }

        var deep = @"D:\" + string.Join('\\', Enumerable.Repeat(new string('x', 40), 12));   // far beyond MAX_PATH
        Assert.True(SourceLocation.TryDerive(deep, out var long_));
        Assert.Equal(deep[2..], long_.RootInVolume);
    }

    [Test]
    public static void A_path_that_is_neither_a_drive_letter_path_nor_a_UNC_path_does_not_split()
    {
        foreach (var path in new[] { "", @"C:", @"C:Media", @"\", @"\Media", @"\\", @"\\server", @"\\server\", @"\\\share", @"Media", @"\\?\C:\x", @"Volume{4b9a7c6e}\Media", @"1:\x", @"CC:\x" })
        {
            Assert.False(SourceLocation.TryDerive(path, out _), $"'{path}' must not be taken for a source location");
        }
    }

    [Test]
    public static void The_device_namespace_prefixes_are_not_a_server_name()
    {
        // "\\?\" and "\\.\" look like a UNC server segment to a naive split ("?" and "." as the server, the next segment as the
        // share). Each must be refused on its own, for every shape that follows it.
        foreach (var prefix in new[] { @"\\?\", @"\\.\" })
        {
            foreach (var rest in new[] { @"x", @"x\y", @"C:\Media", @"UNC\nas\share", @"Volume{4b9a7c6e}\Media", @"GLOBALROOT\Device\x" })
            {
                Assert.False(SourceLocation.TryDerive(prefix + rest, out var location), $"'{prefix + rest}' must not be taken for a source location");
                Assert.Equal(default, location, "a refused path leaves nothing behind");
            }
        }

        // the neighbours that ARE servers: a server whose name merely contains or resembles the marker characters
        Assert.True(SourceLocation.TryDerive(@"\\.x\share", out var dotted), "a name that starts with a dot is a server");
        Assert.Equal(@"\\.x\share", dotted.NetworkRoot);
        Assert.True(SourceLocation.TryDerive(@"\\a.b\share", out _));
    }

    [Test]
    public static void The_kind_follows_the_resolved_canonical_path_never_the_input_syntax()
    {
        // a mapped network letter resolves to a UNC canonical path, so it is network whatever letter was typed
        Assert.Equal(SourceKind.Network, SourceLocation.ClassifyKind(@"\\nas\share\x", @"X:\x"));
        // a SUBST letter resolves to a local canonical path
        Assert.Equal(SourceKind.LocalVolume, SourceLocation.ClassifyKind(@"C:\Users\Jack\Media", @"S:\"));
        // a UNC input whose canonical path says otherwise is local (this cannot happen for real, but the rule is the object's location)
        Assert.Equal(SourceKind.LocalVolume, SourceLocation.ClassifyKind(@"D:\x", @"\\nas\share"));

        // only without a canonical path (the source then cannot be saved anyway) does the kind fall back to v1's own test
        Assert.Equal(SourceKind.Network, SourceLocation.ClassifyKind(null, @"X:\x", isNetworkPath: p => p.StartsWith("X:")));
        Assert.Equal(SourceKind.LocalVolume, SourceLocation.ClassifyKind(null, @"C:\x", isNetworkPath: _ => false));
        Assert.Equal(SourceKind.Network, SourceLocation.ClassifyKind(null, @"\\nas\share"));   // v1's default test: a UNC path is network
    }

    [Test]
    public static void The_stable_source_kind_codes_are_the_schemas()
    {
        // §9.4: 1 LocalVolume, 2 Network (also snapshot.capture_kind). Never reorder or renumber them.
        Assert.Equal(1, (int)SourceKind.LocalVolume);
        Assert.Equal(2, (int)SourceKind.Network);
    }

    [Test]
    public static void A_file_id_is_rendered_the_way_fsutil_prints_it_and_compared_exactly()
    {
        // an NTFS root directory: MFT record 5, sequence 5
        var root = new FileId128(0x0005000000000005, 0);
        Assert.Equal("0x00000000000000000005000000000005", root.ToString());
        Assert.Equal("0x00000000000000010000000000000002", new FileId128(2, 1).ToString());   // all 128 bits are kept (ReFS)
        Assert.True(root == new FileId128(0x0005000000000005, 0));
        Assert.False(root == new FileId128(0x0005000000000005, 1), "the high half takes part in equality");
    }

    [Test]
    public static void The_extended_path_helpers_round_trip_without_further_normalisation()
    {
        Assert.Equal(@"\\?\C:\Media\", NativeMethods.ToExtendedPath(@"C:\Media"));
        Assert.Equal(@"\\?\C:\", NativeMethods.ToExtendedPath(@"C:\"));
        Assert.Equal(@"\\?\UNC\nas\share\Media\", NativeMethods.ToExtendedPath(@"\\nas\share\Media"));
        Assert.Equal(@"C:\Media", NativeMethods.StripExtendedPrefix(@"\\?\C:\Media"));
        Assert.Equal(@"\\nas\share\Media", NativeMethods.StripExtendedPrefix(@"\\?\UNC\nas\share\Media"));
        Assert.Equal(@"\\nas\share", NativeMethods.StripExtendedPrefix(@"\\?\unc\nas\share"), "the UNC marker is matched ignoring case");
        Assert.Equal(@"C:\Media", NativeMethods.StripExtendedPrefix(@"C:\Media"));
    }

    [Test]
    public static void An_item_is_never_a_value_unless_it_says_so()
    {
        var missing = default(EvidenceItem<string>);
        Assert.Equal(EvidenceStatus.Unavailable, missing.Status);
        Assert.False(missing.IsAvailable);
        Assert.Null(missing.Value);

        var failed = EvidenceItem<ulong>.Failed("GetFileInformationByHandleEx/FileIdInfo", 87);
        Assert.Equal(EvidenceStatus.NotProvided, failed.Status);
        Assert.Contains("not provided", failed.ToString());
        Assert.Equal(EvidenceStatus.CallFailed, EvidenceItem<ulong>.Failed("call", 5).Status);
        Assert.Contains("Win32 error 5", EvidenceItem<ulong>.Failed("call", 5).ToString());
        Assert.Equal("7", EvidenceItem<uint>.Of(7, "call").ToString());
    }

    [Test]
    public static void A_reading_with_nothing_filled_in_reports_nothing_as_available()
    {
        var empty = new VolumeEvidence(EvidenceStage.E0Preflight, @"C:\x");
        Assert.False(empty.HandleOpened.IsAvailable);
        Assert.False(empty.CanonicalPath.IsAvailable);
        Assert.False(empty.FileSystemName.IsAvailable);
        Assert.False(empty.VolumeSerial32.IsAvailable);
        Assert.False(empty.VolumeSerial64.IsAvailable);
        Assert.False(empty.RootDirectoryFileId.IsAvailable);
        Assert.False(empty.VolumeLabel.IsAvailable);
        Assert.False(empty.MountPoint.IsAvailable);
        Assert.False(empty.CapacityBytes.IsAvailable);
        Assert.False(empty.FreeBytes.IsAvailable);
        Assert.False(empty.FileSystemFlags.IsAvailable);
    }
}

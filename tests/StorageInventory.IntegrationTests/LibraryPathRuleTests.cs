using StorageInventory.Core.Paths;
using StorageInventory.History.Library;
using StorageInventory.Testing;

namespace StorageInventory.IntegrationTests;

/// <summary>
/// TEST-L2: the Library path rules LIB-05a to LIB-06e against real directories, junctions, SUBST letters and a mapped network
/// drive: a network location, a reparse point, a OneDrive root, the app-data root, overlap in every direction and aliases, each
/// by literal AND canonical location (SEC-26). Q-05 stays as specified: a junction blocks, with an explanation, and History is
/// simply unavailable while scans still work. The rules read metadata only and create nothing.
/// </summary>
public static class LibraryPathRuleTests
{
    private sealed class Tree : IDisposable
    {
        public Tree(string label)
        {
            Work = TestEnvironment.NewWorkFolder("libpath_" + label);
            AppData = Path.Combine(Work, "AppData");
            Library = Path.Combine(AppData, "Library");
            Directory.CreateDirectory(AppData);
        }

        public string Work { get; }
        public string AppData { get; }
        public string Library { get; }

        public void Dispose() => TestEnvironment.RemoveTree(Work);
    }

    private sealed class Subst : IDisposable
    {
        public Subst(string target)
        {
            Letter = TestEnvironment.FreeDriveLetter() ?? throw new SkipException("no free drive letter for SUBST");
            TestEnvironment.Run("subst.exe", $"{Letter}:", target);
            if (!Directory.Exists(Root)) throw new SkipException("SUBST is unavailable here");
        }

        public char Letter { get; }
        public string Root => $@"{Letter}:\";
        public void Dispose() => TestEnvironment.Run("subst.exe", $"{Letter}:", "/D");
    }

    private static string Codes(LibraryPathAssessment a) => string.Join(", ", a.Issues.Select(i => $"{i.Severity}:{i.Code}"));

    private static void ExpectBlocked(LibraryPathAssessment a, string code) =>
        Assert.True(a.Issues.Any(i => i.Severity == IssueSeverity.Blocked && i.Code == code), $"expected {code}; got {Codes(a)}");

    // ---- LIB-05: the Library directory itself ----

    [Test]
    public static void An_ordinary_Library_directory_is_valid_and_validation_creates_nothing()
    {
        using var t = new Tree("ok");
        var a = LibraryPathRules.ValidateLibraryDirectory(t.Library, t.AppData);
        Assert.False(a.Blocked, Codes(a));
        Assert.False(Directory.Exists(t.Library), "validation creates nothing (an open creates no directory)");
    }

    [Test]
    public static void LIB_05a_a_network_location_is_blocked_by_UNC_path_and_by_mapped_drive()
    {
        var unc = LibraryPathRules.ValidateLibraryDirectory(@"\\localhost\C$\StorageInventory-test\AppData\Library", @"\\localhost\C$\StorageInventory-test\AppData");
        ExpectBlocked(unc, "LibraryOnNetwork");

        using var t = new Tree("net");
        var letter = TestEnvironment.FreeDriveLetter();
        if (letter is null) Assert.Skip("no free drive letter");
        var share = $@"\\localhost\{Path.GetPathRoot(t.Work)![0]}$";
        TestEnvironment.Run("net.exe", "use", $"{letter}:", share, "/persistent:no");
        try
        {
            if (!Directory.Exists($@"{letter}:\")) Assert.Skip("a mapped drive to the local admin share is unavailable here");
            var rel = Path.GetRelativePath(Path.GetPathRoot(t.Work)!, t.Library);
            var relRoot = Path.GetRelativePath(Path.GetPathRoot(t.Work)!, t.AppData);
            var mapped = LibraryPathRules.ValidateLibraryDirectory($@"{letter}:\{rel}", $@"{letter}:\{relRoot}");
            ExpectBlocked(mapped, "LibraryOnNetwork");
        }
        finally
        {
            TestEnvironment.Run("net.exe", "use", $"{letter}:", "/delete", "/y");
        }
    }

    [Test]
    public static void LIB_05b_a_reparse_point_anywhere_in_the_path_is_blocked_with_an_explanation_and_Q_05_is_scoped()
    {
        using var t = new Tree("junction");
        // the Library directory is itself a junction
        var target = Path.Combine(t.Work, "elsewhere");
        Directory.CreateDirectory(target);
        Assert.True(TestEnvironment.CreateJunction(t.Library, target), "mklink /J");
        var direct = LibraryPathRules.ValidateLibraryDirectory(t.Library, t.AppData);
        ExpectBlocked(direct, "LibraryThroughLink");
        var issue = direct.FirstBlocking!;
        Assert.Contains("link", issue.Message);
        Assert.Contains("scans still work", issue.Message);   // Q-05: blocked with an explanation; History is unavailable, scanning is not
        Assert.Equal(0, Directory.GetFileSystemEntries(target).Length, "nothing was created through the junction");

        // an ANCESTOR is a junction (a profile relocated with a junction): the app-data root itself
        using var u = new Tree("junction-ancestor");
        var realAppData = Path.Combine(u.Work, "real");
        Directory.CreateDirectory(realAppData);
        Directory.Delete(u.AppData);
        Assert.True(TestEnvironment.CreateJunction(u.AppData, realAppData));
        var ancestor = LibraryPathRules.ValidateLibraryDirectory(u.Library, u.AppData);
        ExpectBlocked(ancestor, "LibraryThroughLink");
    }

    [Test]
    public static void LIB_05c_a_OneDrive_or_sync_root_is_blocked_literally_and_through_an_alias()
    {
        using var t = new Tree("sync");
        foreach (var variable in LibraryPathRules.SyncRootVariables)
        {
            var literal = LibraryPathRules.ValidateLibraryDirectory(t.Library, t.AppData, name => name == variable ? t.Work : null);
            ExpectBlocked(literal, "LibraryInSyncFolder");
        }
        var unrelated = LibraryPathRules.ValidateLibraryDirectory(t.Library, t.AppData, name => name == "OneDrive" ? Path.Combine(t.Work, "other") : null);
        Assert.False(unrelated.Blocked, "a sync root elsewhere does not block: " + Codes(unrelated));
        var none = LibraryPathRules.ValidateLibraryDirectory(t.Library, t.AppData, _ => null);
        Assert.False(none.Blocked);

        // by canonical location: the sync root is named through a SUBST letter, the Library through its real path
        using var alias = new Subst(t.Work);
        var viaAlias = LibraryPathRules.ValidateLibraryDirectory(t.Library, t.AppData, name => name == "OneDrive" ? alias.Root.TrimEnd('\\') : null);
        ExpectBlocked(viaAlias, "LibraryInSyncFolder");
    }

    [Test]
    public static void A_Library_outside_its_app_data_root_and_unusable_paths_are_blocked()
    {
        using var t = new Tree("outside");
        ExpectBlocked(LibraryPathRules.ValidateLibraryDirectory(Path.Combine(t.Work, "Elsewhere"), t.AppData), "LibraryOutsideAppData");
        ExpectBlocked(LibraryPathRules.ValidateLibraryDirectory(t.AppData, t.AppData), "LibraryOutsideAppData");   // the Library is not the root itself
        ExpectBlocked(LibraryPathRules.ValidateLibraryDirectory(@"relative\Library", t.AppData), "LibraryPathInvalid");
        ExpectBlocked(LibraryPathRules.ValidateLibraryDirectory(@"C:\Users\x\Lib*ry", @"C:\Users\x"), "LibraryPathInvalid");
        ExpectBlocked(LibraryPathRules.ValidateLibraryDirectory(@"\\?\C:\x\Library", @"\\?\C:\x"), "LibraryPathInvalid");
        ExpectBlocked(LibraryPathRules.ValidateLibraryDirectory(null, t.AppData), "LibraryPathInvalid");
    }

    // ---- LIB-06a: the source ----

    [Test]
    public static void LIB_06a_a_source_equal_to_or_inside_the_app_data_root_is_blocked_literally_and_through_aliases()
    {
        using var t = new Tree("source");
        Directory.CreateDirectory(t.Library);
        var inner = Path.Combine(t.AppData, "Cache");
        Directory.CreateDirectory(inner);

        foreach (var source in new[] { t.AppData, t.Library, inner, t.AppData.ToUpperInvariant() + @"\", Path.Combine(t.Library, "deeper") })
        {
            ExpectBlocked(LibraryPathRules.CheckSource(source, t.Library, t.AppData), "SourceInsideAppData");
        }

        // through an alias: a SUBST letter for the app-data root, or for the Library directory
        using var aliasRoot = new Subst(t.AppData);
        var viaRoot = LibraryPathRules.CheckSource(aliasRoot.Root, t.Library, t.AppData);
        ExpectBlocked(viaRoot, "SourceInsideAppDataAlias");
        var viaInner = LibraryPathRules.CheckSource(Path.Combine(aliasRoot.Root, "Cache"), t.Library, t.AppData);
        ExpectBlocked(viaInner, "SourceInsideAppDataAlias");

        // a sibling of the app-data root is fine
        var sibling = Path.Combine(t.Work, "Pictures");
        Directory.CreateDirectory(sibling);
        Assert.False(LibraryPathRules.CheckSource(sibling, t.Library, t.AppData).Blocked);
    }

    [Test]
    public static void LIB_06c_a_Library_inside_the_source_is_allowed_with_an_informational_issue_in_both_directions_of_alias()
    {
        using var t = new Tree("inside");
        Directory.CreateDirectory(t.Library);
        // the source is an ancestor of the Library (the C:\ and C:\Users cases)
        var literal = LibraryPathRules.CheckSource(t.Work, t.Library, t.AppData);
        Assert.False(literal.Blocked, Codes(literal));
        Assert.Equal(LibraryInsideSource.Yes, literal.InsideSource);
        Assert.True(literal.Issues.Any(i => i.Severity == IssueSeverity.Info && i.Code == "LibraryInsideSource"), Codes(literal));

        // the source named through a SUBST letter, the Library through its real path: canonical location finds the overlap
        using var alias = new Subst(t.Work);
        var viaAlias = LibraryPathRules.CheckSource(alias.Root, t.Library, t.AppData);
        Assert.False(viaAlias.Blocked, Codes(viaAlias));
        Assert.Equal(LibraryInsideSource.Yes, viaAlias.InsideSource);

        // the other direction: the Library named through the alias, the source through its real path
        var aliasedLibrary = Path.Combine(alias.Root, "AppData", "Library");
        var other = LibraryPathRules.CheckSource(t.Work, aliasedLibrary, Path.Combine(alias.Root, "AppData"));
        Assert.Equal(LibraryInsideSource.Yes, other.InsideSource, Codes(other));

        // a source that does not contain the Library
        var picture = Path.Combine(t.Work, "Pictures");
        Directory.CreateDirectory(picture);
        var none = LibraryPathRules.CheckSource(picture, t.Library, t.AppData);
        Assert.Equal(LibraryInsideSource.No, none.InsideSource);
        Assert.Equal(0, none.Issues.Count, Codes(none));
    }

    [Test]
    public static void LIB_06e_when_a_canonical_location_cannot_be_determined_the_Library_is_treated_as_possibly_inside()
    {
        using var t = new Tree("unknown");
        Directory.CreateDirectory(t.Library);
        var missingSource = Path.Combine(t.Work, "NoSuchFolder");
        var unknown = LibraryPathRules.CheckSource(missingSource, t.Library, t.AppData);
        Assert.False(unknown.Blocked, Codes(unknown));
        Assert.Equal(LibraryInsideSource.Unknown, unknown.InsideSource, "recorded as library_inside_source = 2 (unknown)");
        Assert.True(unknown.Issues.Any(i => i.Code == "LibraryInsideSourceUnknown"), Codes(unknown));
    }

    // ---- LIB-06b / LIB-06d: the report folder ----

    [Test]
    public static void LIB_06b_a_report_folder_equal_to_or_inside_the_app_data_root_is_blocked_literally_and_through_aliases()
    {
        using var t = new Tree("reports");
        Directory.CreateDirectory(t.Library);
        foreach (var folder in new[] { t.AppData, t.Library, Path.Combine(t.AppData, "reports-new"), Path.Combine(t.Library, "x", "y") })
        {
            ExpectBlocked(LibraryPathRules.CheckReportFolder(folder, t.Library, t.AppData), "OutputInsideAppData");
        }
        using var alias = new Subst(t.AppData);
        ExpectBlocked(LibraryPathRules.CheckReportFolder(alias.Root, t.Library, t.AppData), "OutputInsideAppDataAlias");
        ExpectBlocked(LibraryPathRules.CheckReportFolder(Path.Combine(alias.Root, "later", "reports"), t.Library, t.AppData), "OutputInsideAppDataAlias");

        var fine = Path.Combine(t.Work, "Reports");
        Assert.False(LibraryPathRules.CheckReportFolder(fine, t.Library, t.AppData).Blocked);
    }

    [Test]
    public static void LIB_06d_a_Library_inside_the_report_folder_is_allowed()
    {
        using var t = new Tree("reports-contains");
        Directory.CreateDirectory(t.Library);
        var a = LibraryPathRules.CheckReportFolder(t.Work, t.Library, t.AppData);   // the report folder is an ancestor of the Library
        Assert.False(a.Blocked, "harmless: reports and spools are create-new, have other names and are written only directly in the report folder: " + Codes(a));
        Assert.Equal(0, a.Issues.Count, Codes(a));
    }

    [Test]
    public static void The_rules_never_change_the_filesystem()
    {
        using var t = new Tree("readonly");
        var before = Directory.EnumerateFileSystemEntries(t.Work, "*", SearchOption.AllDirectories).Order().ToList();
        LibraryPathRules.ValidateLibraryDirectory(t.Library, t.AppData);
        LibraryPathRules.CheckSource(t.Work, t.Library, t.AppData);
        LibraryPathRules.CheckReportFolder(Path.Combine(t.Work, "r"), t.Library, t.AppData);
        Assert.SequenceEqual(before, Directory.EnumerateFileSystemEntries(t.Work, "*", SearchOption.AllDirectories).Order(), "no directory or file was created");
    }
}

using StorageInventory.Core.Paths;
using StorageInventory.Testing;

namespace StorageInventory.IntegrationTests;

/// <summary>Path policy against real directories, including the Phase A adversarial fixture.</summary>
public static class PathPolicyFixtureTests
{
    private static PhaseAFixture Fx => PhaseAFixture.Shared;

    private static void ExpectBlocked(PathValidationResult r, string code)
    {
        Assert.False(r.CanScan, $"expected blocked ({code})");
        Assert.True(r.Issues.Any(i => i.Severity == IssueSeverity.Blocked && i.Code == code),
            $"expected {code}, got: {string.Join("; ", r.Issues.Select(i => $"{i.Severity}:{i.Code}"))}");
    }

    private static string FreshOutput() => Path.Combine(Fx.Base, "out_" + Guid.NewGuid().ToString("N")[..6]);

    [Test]
    public static void A_valid_pair_can_scan_and_validation_creates_nothing()
    {
        var output = FreshOutput();
        var r = PathPolicy.Validate(Path.Combine(Fx.Root, @"Kpop\TWICE"), output);
        Assert.True(r.CanScan, string.Join("; ", r.Issues.Select(i => i.Code)));
        Assert.True(r.Issues.Any(i => i.Code == "OutputWillBeCreated" && i.Severity == IssueSeverity.Info));
        Assert.False(Directory.Exists(output), "validation must never create the output folder");
    }

    [Test]
    public static void Missing_source_is_blocked()
        => ExpectBlocked(PathPolicy.Validate(Path.Combine(Fx.Base, "does-not-exist"), FreshOutput()), "SourceMissing");

    [Test]
    public static void Source_that_is_a_file_is_blocked()
        => ExpectBlocked(PathPolicy.Validate(Path.Combine(Fx.Root, "a.txt"), FreshOutput()), "SourceIsFile");

    [Test]
    public static void Output_that_is_a_file_is_blocked()
        => ExpectBlocked(PathPolicy.Validate(Path.Combine(Fx.Root, "Kpop"), Path.Combine(Fx.Root, "a.txt")), "OutputIsFile");

    [Test]
    public static void Output_equal_to_source_is_blocked_including_case_and_trailing_slash_variants()
    {
        ExpectBlocked(PathPolicy.Validate(Fx.Root, Fx.Root), "OutputInsideSource");
        ExpectBlocked(PathPolicy.Validate(Fx.Root, Fx.Root.ToUpperInvariant() + @"\"), "OutputInsideSource");
        ExpectBlocked(PathPolicy.Validate(Fx.Root + @"\", Fx.Root.ToLowerInvariant()), "OutputInsideSource");
    }

    [Test]
    public static void Output_inside_source_is_blocked_even_if_it_does_not_exist_yet()
    {
        var inside = Path.Combine(Fx.Root, "Kpop", "reports-" + Guid.NewGuid().ToString("N")[..6]);
        ExpectBlocked(PathPolicy.Validate(Fx.Root, inside), "OutputInsideSource");
        Assert.False(Directory.Exists(inside));
    }

    [Test]
    public static void Output_in_a_parent_of_the_source_is_allowed()
    {
        // Reports saved in a parent folder of the source are not inside the scanned tree.
        var r = PathPolicy.Validate(Path.Combine(Fx.Root, @"Kpop\TWICE"), Path.Combine(Fx.Root, "Kpop"));
        Assert.True(r.CanScan, string.Join("; ", r.Issues.Select(i => i.Code)));
    }

    [Test]
    public static void Source_that_is_a_junction_is_blocked()
        => ExpectBlocked(PathPolicy.Validate(Path.Combine(Fx.Root, "Loop"), FreshOutput()), "SourceThroughLink");

    [Test]
    public static void Source_beneath_a_junction_is_blocked()
        => ExpectBlocked(PathPolicy.Validate(Path.Combine(Fx.Root, @"Kpop\LinkOut"), FreshOutput()), "SourceThroughLink");

    [Test]
    public static void Output_through_a_junction_into_the_source_is_blocked()
    {
        var link = Path.Combine(Fx.Base, "jn-" + Guid.NewGuid().ToString("N")[..6]);
        Assert.True(TestEnvironment.CreateJunction(link, Path.Combine(Fx.Root, "Empty")), "could not create test junction");
        var r = PathPolicy.Validate(Fx.Root, Path.Combine(link, "reports"));
        ExpectBlocked(r, "OutputThroughLink");
        Assert.Equal(0, Directory.GetFileSystemEntries(Path.Combine(Fx.Root, "Empty")).Length, "nothing created through the junction");
    }

    [Test]
    public static void SUBST_alias_of_the_source_with_output_inside_the_real_source_is_blocked_before_scanning()
    {
        // PowerShell reference: caught only at run time by the tripwire. Native: blocked up front (canonical paths).
        var letter = TestEnvironment.FreeDriveLetter() ?? throw new SkipException("no free drive letter for SUBST");
        var target = Path.Combine(Fx.Root, "Kpop");
        TestEnvironment.Run("subst.exe", $"{letter}:", target);
        try
        {
            if (!Directory.Exists($@"{letter}:\")) Assert.Skip("SUBST is unavailable here");
            var insideReal = Path.Combine(target, "aliasout");
            ExpectBlocked(PathPolicy.Validate($@"{letter}:\", insideReal), "OutputInsideSourceAlias");
            ExpectBlocked(PathPolicy.Validate(target, $@"{letter}:\aliasout"), "OutputInsideSourceAlias");
            Assert.False(Directory.Exists(insideReal), "nothing created");
        }
        finally
        {
            TestEnvironment.Run("subst.exe", $"{letter}:", "/D");
        }
    }

    [Test]
    public static void Short_8dot3_alias_of_the_source_is_seen_through()
    {
        var longName = Path.Combine(Fx.Base, "A Rather Long Folder Name For Aliasing");
        Directory.CreateDirectory(longName);
        var shortName = TestEnvironment.GetShortPath(longName);
        if (shortName is null) Assert.Skip("8.3 short names are disabled on this volume");
        var r = PathPolicy.Validate(shortName!, Path.Combine(longName, "reports"));
        ExpectBlocked(r, "OutputInsideSourceAlias");
    }

    [Test]
    public static void Long_path_source_beyond_260_characters_is_accepted()
    {
        var longDir = Directory.GetDirectories(Directory.GetDirectories(Path.Combine(Fx.Root, "Long"))[0])[0];
        Assert.True(longDir.Length > 260, $"fixture path is only {longDir.Length} characters");
        var r = PathPolicy.Validate(longDir, FreshOutput());
        Assert.True(r.CanScan, string.Join("; ", r.Issues.Select(i => i.Code)));
    }

    [Test]
    public static void Source_with_brackets_dollar_backtick_Korean_and_emoji_is_treated_literally()
    {
        var weird = Directory.GetDirectories(Fx.Root, "Weird*")[0];
        var r = PathPolicy.Validate(weird, FreshOutput());
        Assert.True(r.CanScan, string.Join("; ", r.Issues.Select(i => i.Code)));
        Assert.Equal(weird, r.RootFullPath);
    }

    [Test]
    public static void Directory_symbolic_link_source_is_blocked()
    {
        if (!Fx.SymlinksCreated) Assert.Skip("cannot create symbolic links without Developer Mode or admin");
        ExpectBlocked(PathPolicy.Validate(Path.Combine(Fx.Root, @"Links\dir-link"), FreshOutput()), "SourceThroughLink");
    }

    [Test]
    public static void File_symbolic_link_as_source_is_blocked()
    {
        if (!Fx.SymlinksCreated) Assert.Skip("cannot create symbolic links without Developer Mode or admin");
        var r = PathPolicy.Validate(Path.Combine(Fx.Root, @"Links\file-link.bin"), FreshOutput());
        Assert.False(r.CanScan);
    }

    [Test]
    public static void Validation_does_not_change_the_fixture()
    {
        Snapshot.Settle(Fx.Root);
        var before = Snapshot.Take(Fx.Root);
        foreach (var candidate in new[] { Fx.Root, Path.Combine(Fx.Root, "Loop"), Path.Combine(Fx.Root, "Denied"), Path.Combine(Fx.Root, "Kpop") })
        {
            PathPolicy.Validate(candidate, Path.Combine(candidate, "out"));
            PathPolicy.Validate(candidate, FreshOutput());
        }
        Assert.Equal(before, Snapshot.Take(Fx.Root), "validation must be read-only");
    }
}

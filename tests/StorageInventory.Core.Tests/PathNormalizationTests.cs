using StorageInventory.Core.Paths;
using StorageInventory.Testing;

namespace StorageInventory.Core.Tests;

/// <summary>Pure string rules of the path policy (no file system needed).</summary>
public static class PathNormalizationTests
{
    private static (string? Full, List<PathIssue> Issues) Norm(string? input, string? baseDirectory = null)
    {
        var issues = new List<PathIssue>();
        var full = PathPolicy.Normalize(input, PathSubject.Reports, baseDirectory, issues);
        return (full, issues);
    }

    private static void Blocked(string input, string code)
    {
        var (full, issues) = Norm(input);
        Assert.Null(full, $"'{input}' should be rejected");
        Assert.True(issues.Any(i => i.Severity == IssueSeverity.Blocked && i.Code == code),
            $"'{input}': expected {code}, got {string.Join(", ", issues.Select(i => i.Code))}");
    }

    private static string Allowed(string input)
    {
        var (full, issues) = Norm(input);
        Assert.True(full is not null, $"'{input}' should be allowed but got {string.Join(", ", issues.Select(i => i.Code + ":" + i.Details))}");
        Assert.False(issues.Any(i => i.Severity == IssueSeverity.Blocked), $"'{input}' should not be blocked");
        return full!;
    }

    [Test]
    public static void Empty_and_whitespace_are_blocked()
    {
        Blocked("", "OutputEmpty");
        Blocked("   ", "OutputEmpty");
        Blocked(null!, "OutputEmpty");
    }

    [Test]
    public static void Bare_drive_letter_means_drive_root_and_is_reported_not_silently_fixed()
    {
        var (full, issues) = Norm("D:");
        Assert.Equal(@"D:\", full);
        Assert.True(issues.Any(i => i.Code == "OutputDriveRootAssumed" && i.Severity == IssueSeverity.Info));
    }

    [Test]
    public static void Drive_roots_keep_their_trailing_separator()
    {
        Assert.Equal(@"D:\", Allowed(@"D:\"));
        Assert.Equal(@"E:\", Allowed("E:/"));
    }

    [Test]
    public static void Ordinary_folders_are_normalised()
    {
        Assert.Equal(@"D:\Media", Allowed(@"D:\Media\"));
        Assert.Equal(@"D:\Media\Photos", Allowed(@"D:/Media/Photos"));
        Assert.Equal(@"D:\Photos", Allowed(@"D:\Media\..\Photos"));
        Assert.Equal(@"\\server\share\folder", Allowed(@"\\server\share\folder\"));
    }

    [Test]
    public static void Surrounding_whitespace_is_ignored_but_reported()
    {
        var (full, issues) = Norm("  D:\\Media  ");
        Assert.Equal(@"D:\Media", full);
        Assert.True(issues.Any(i => i.Code == "OutputWhitespaceIgnored"));
    }

    [Test]
    public static void Relative_paths_are_blocked_without_a_base_and_resolved_with_one()
    {
        Blocked(@"Media\Photos", "OutputNotFullPath");
        Blocked(@"D:Media", "OutputNotFullPath");          // drive-relative: ambiguous
        Blocked(@"\Media", "OutputNotFullPath");           // root-relative: depends on the current drive
        var (full, _) = Norm(@"reports\today", @"C:\Work");
        Assert.Equal(@"C:\Work\reports\today", full);
        var (rooted, _) = Norm(@"\Media", @"C:\Work");
        Assert.Null(rooted, "a root-relative path stays ambiguous even with a base directory");
    }

    [Test]
    public static void Device_paths_are_blocked()
    {
        Blocked(@"\\?\C:\Media", "OutputDevicePath");
        Blocked(@"\\.\C:\Media", "OutputDevicePath");
        Blocked(@"//?/C:/Media", "OutputDevicePath");
        Blocked(@"\\?\UNC\server\share", "OutputDevicePath");
    }

    [Test]
    public static void Reserved_device_names_are_blocked_anywhere_in_the_path()
    {
        foreach (var name in new[] { "CON", "PRN", "AUX", "NUL", "nul", "COM1", "COM9", "LPT1", "LPT0", "CON.txt", "NUL.tar.gz" })
        {
            Blocked(@"C:\Temp\" + name, "OutputReservedName");
            Blocked(@"C:\Temp\" + name + @"\inner", "OutputReservedName");
        }
    }

    [Test]
    public static void Superscript_COM_and_LPT_device_names_are_blocked()
    {
        foreach (var name in new[] { "COM\u00B9", "COM\u00B2", "COM\u00B3", "LPT\u00B9", "LPT\u00B2", "LPT\u00B3", "lpt\u00B9.log" })
        {
            Blocked(@"C:\Temp\" + name, "OutputReservedName");
        }
    }

    [Test]
    public static void Legitimate_names_resembling_devices_are_allowed()
    {
        foreach (var name in new[] { "CONFIG", "Console", "COM10", "LPT", "NULL", "AUXILIARY", "COM\u0663", "LPT\u2074", "COMX" })
        {
            Allowed(@"C:\Temp\" + name);
        }
    }

    [Test]
    public static void Alternate_data_stream_syntax_is_blocked()
    {
        Blocked(@"C:\Temp\Out:stream", "OutputStreamSyntax");
        Blocked(@"C:\Temp\Out::$DATA", "OutputStreamSyntax");
    }

    [Test]
    public static void Trailing_dot_and_trailing_space_aliases_are_blocked()
    {
        Blocked(@"C:\Media.\Out", "OutputTrailingDotOrSpace");
        Blocked(@"C:\Media \Out", "OutputTrailingDotOrSpace");
        Blocked(@"C:\Media\Out.", "OutputTrailingDotOrSpace");
        Blocked(@"C:\Media\Out...", "OutputTrailingDotOrSpace");
        Allowed(@"C:\Media\My.Folder");
        Allowed(@"C:\Media\.hidden");
    }

    [Test]
    public static void Characters_Windows_forbids_are_blocked()
    {
        foreach (var bad in new[] { @"C:\a*b", @"C:\a?b", @"C:\a<b", @"C:\a>b", @"C:\a|b", "C:\\a\"b", "C:\\a\tb", "C:\\a\nb" })
        {
            Blocked(bad, "OutputInvalidCharacters");
        }
    }

    [Test]
    public static void Unusual_but_legal_characters_are_treated_literally()
    {
        Assert.Equal(@"C:\Weird [x] $(whoami) `tick 'apos' & ; (p) " + "\uD55C\uAD6D\uC5B4 \U0001F600",
            Allowed(@"C:\Weird [x] $(whoami) `tick 'apos' & ; (p) " + "\uD55C\uAD6D\uC5B4 \U0001F600"));
    }

    [Test]
    public static void Long_paths_beyond_260_characters_are_accepted()
    {
        var longPath = @"C:\" + string.Join(@"\", Enumerable.Repeat(new string('L', 100), 5));
        Assert.True(longPath.Length > 500);
        Assert.Equal(longPath, Allowed(longPath));
    }

    [Test]
    public static void Containment_is_whole_segment_and_case_insensitive()
    {
        Assert.True(PathPolicy.IsSameOrInside(@"D:\Media", @"D:\Media"));
        Assert.True(PathPolicy.IsSameOrInside(@"d:\MEDIA\out", @"D:\Media"));
        Assert.True(PathPolicy.IsSameOrInside(@"D:\Inventory", @"D:\"));
        Assert.False(PathPolicy.IsSameOrInside(@"D:\Media2", @"D:\Media"), "a sibling with a common prefix is not inside");
        Assert.False(PathPolicy.IsSameOrInside(@"D:\", @"D:\Media"));
    }

    [Test]
    public static void UNC_paths_are_network_paths()
    {
        Assert.True(PathPolicy.IsNetworkPath(@"\\server\share\folder"));
    }

    [Test]
    public static void Extended_path_conversion_handles_drives_and_UNC()
    {
        Assert.Equal(@"\\?\C:\", NativeMethods.ToExtendedPath(@"C:\"));
        Assert.Equal(@"\\?\C:\a\", NativeMethods.ToExtendedPath(@"C:\a"));
        Assert.Equal(@"\\?\UNC\server\share\a\", NativeMethods.ToExtendedPath(@"\\server\share\a"));
        Assert.Equal(@"\\server\share\a", NativeMethods.StripExtendedPrefix(@"\\?\UNC\server\share\a"));
        Assert.Equal(@"C:\a", NativeMethods.StripExtendedPrefix(@"\\?\C:\a"));
    }
}

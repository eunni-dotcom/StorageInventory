using System.Text.RegularExpressions;

namespace StorageInventory.Core.Paths;

public enum IssueSeverity
{
    /// <summary>Something worth knowing; the scan can proceed.</summary>
    Info,

    /// <summary>The scan can proceed, but the user should be aware (network, cloud sync...).</summary>
    Warning,

    /// <summary>The scan must not start.</summary>
    Blocked,
}

/// <summary>Which of the two paths an issue is about.</summary>
public enum PathSubject
{
    Source,
    Reports,
}

/// <summary>A validation finding. <see cref="Code"/> is stable (for tests and "Why is this blocked?" help);
/// <see cref="Message"/> is plain language; <see cref="Details"/> is the technical explanation.</summary>
public sealed record PathIssue(IssueSeverity Severity, PathSubject Subject, string Code, string Message, string? Details = null);

/// <summary>The outcome of validating a source/report-folder pair.</summary>
public sealed class PathValidationResult
{
    internal PathValidationResult(string? rootFullPath, string? outputFullPath, IReadOnlyList<PathIssue> issues,
        bool outputFolderExists, bool rootIsNetwork, bool outputIsNetwork)
    {
        RootFullPath = rootFullPath;
        OutputFullPath = outputFullPath;
        Issues = issues;
        OutputFolderExists = outputFolderExists;
        RootIsNetwork = rootIsNetwork;
        OutputIsNetwork = outputIsNetwork;
    }

    /// <summary>The normalised source path, or null if it could not be normalised.</summary>
    public string? RootFullPath { get; }

    /// <summary>The normalised report-folder path, or null if it could not be normalised.</summary>
    public string? OutputFullPath { get; }

    public IReadOnlyList<PathIssue> Issues { get; }
    public bool OutputFolderExists { get; }
    public bool RootIsNetwork { get; }
    public bool OutputIsNetwork { get; }

    /// <summary>True only when both paths are usable and nothing is blocked.</summary>
    public bool CanScan => RootFullPath is not null && OutputFullPath is not null && Issues.All(i => i.Severity != IssueSeverity.Blocked);

    public IssueSeverity? HighestSeverity => Issues.Count == 0 ? null : Issues.Max(i => i.Severity);
}

/// <summary>
/// The path safety policy. Equivalent to the PowerShell reference (Resolve-FileSystemPath, the inside-root check and
/// Find-ReparsePointInPath), plus one strengthening: the canonical location of both paths is resolved with Windows'
/// GetFinalPathNameByHandle, so aliases such as SUBST drives, 8.3 short names and letter-case variants are blocked
/// BEFORE any file is created. The PowerShell reference caught those only at run time, with a tripwire.
/// Suspicious input is never silently "fixed": every normalisation is reported as an issue.
/// </summary>
public static partial class PathPolicy
{
    private const string InvalidPathCharacters = "\"*?<>|";

    [GeneratedRegex(@"^[\\/]{2}[?.][\\/]")]
    private static partial Regex DevicePrefix();

    [GeneratedRegex(@"^[A-Za-z]:$")]
    private static partial Regex BareDrive();

    // Reserved device names as any path component, with or without an extension, including the superscript forms
    // COM1-3 / LPT1-3 (U+00B9, U+00B2, U+00B3). [0-9] rather than \d: .NET's \d also matches non-ASCII digits,
    // which Windows does not reserve. Matching is case-insensitive, as Windows' is.
    [GeneratedRegex(@"(^|\\)(CON|PRN|AUX|NUL|(COM|LPT)[0-9\u00B9\u00B2\u00B3])(\.[^\\]*)?(\\|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReservedDeviceName();

    // A path component ending in '.' or ' ' - Windows strips these, so 'D:\Media.\Out' is really 'D:\Media\Out'.
    [GeneratedRegex(@"[. ](\\|$)")]
    private static partial Regex TrailingDotOrSpace();

    /// <summary>Validates a scan source and report folder. Only ever reads metadata; creates nothing.</summary>
    /// <param name="baseDirectory">If given, relative paths are resolved against it; otherwise relative paths are
    /// blocked (a GUI has no meaningful "current directory").</param>
    public static PathValidationResult Validate(string? rootInput, string? outputInput, string? baseDirectory = null)
    {
        var issues = new List<PathIssue>();
        var root = Normalize(rootInput, PathSubject.Source, baseDirectory, issues);
        var output = Normalize(outputInput, PathSubject.Reports, baseDirectory, issues);
        var rootUsable = false;
        var outputExists = false;
        var rootIsNetwork = false;
        var outputIsNetwork = false;

        if (root is not null)
        {
            if (Directory.Exists(root))
            {
                rootUsable = true;
            }
            else if (File.Exists(root))
            {
                issues.Add(new(IssueSeverity.Blocked, PathSubject.Source, "SourceIsFile", "The source is a file, not a folder or drive.", root));
            }
            else
            {
                issues.Add(new(IssueSeverity.Blocked, PathSubject.Source, "SourceMissing", "The source folder does not exist or cannot be reached.", root));
            }

            var link = FindReparsePointInPath(root);
            if (link is not null)
            {
                rootUsable = false;
                issues.Add(new(IssueSeverity.Blocked, PathSubject.Source, "SourceThroughLink",
                    "The source passes through a link (junction, symbolic link or similar). Choose the real folder instead.", link));
            }

            rootIsNetwork = IsNetworkPath(root);
            if (rootIsNetwork)
            {
                issues.Add(new(IssueSeverity.Warning, PathSubject.Source, "SourceNetwork",
                    "The source is on a network location. It will only be scanned because you chose it; this can be slow.", root));
            }
        }

        if (output is not null)
        {
            if (File.Exists(output))
            {
                issues.Add(new(IssueSeverity.Blocked, PathSubject.Reports, "OutputIsFile", "The report location is an existing file, not a folder.", output));
            }
            else
            {
                outputExists = Directory.Exists(output);
                if (!outputExists)
                {
                    issues.Add(new(IssueSeverity.Info, PathSubject.Reports, "OutputWillBeCreated", "The report folder does not exist yet and will be created.", output));
                }
            }

            var link = FindReparsePointInPath(output);
            if (link is not null)
            {
                issues.Add(new(IssueSeverity.Blocked, PathSubject.Reports, "OutputThroughLink",
                    "The report folder passes through a link (junction, symbolic link or similar). Choose an ordinary folder.", link));
            }

            outputIsNetwork = IsNetworkPath(output);
            if (outputIsNetwork)
            {
                issues.Add(new(IssueSeverity.Warning, PathSubject.Reports, "OutputNetwork",
                    "The report folder is on a network location. The reports, which list your file names, will be written across the network.", output));
            }

            var syncFolder = FindCloudSyncFolder(output);
            if (syncFolder is not null)
            {
                issues.Add(new(IssueSeverity.Warning, PathSubject.Reports, "OutputCloudSynced",
                    "The report folder is inside OneDrive. OneDrive will upload the reports, which list your file and folder names.", syncFolder));
            }
        }

        if (root is not null && output is not null)
        {
            if (IsSameOrInside(output, root))
            {
                issues.Add(new(IssueSeverity.Blocked, PathSubject.Reports, "OutputInsideSource",
                    "The report folder is the source folder or inside it. Reports must be saved outside the folder being scanned.",
                    $"{output} is within {root}"));
            }
            else if (rootUsable)
            {
                CheckCanonicalLocations(root, output, issues);
            }
        }

        return new PathValidationResult(root, output, issues, outputExists, rootIsNetwork, outputIsNetwork);
    }

    /// <summary>
    /// Resolves the real locations of both paths and blocks if the report folder is actually inside the source through
    /// an alias. If the real location cannot be determined, a warning is added and the scan's runtime tripwire remains
    /// the backstop.
    /// </summary>
    private static void CheckCanonicalLocations(string root, string output, List<PathIssue> issues)
    {
        var realRoot = TryGetCanonicalPath(root);
        var realOutput = TryGetCanonicalPathOfPossiblyMissingFolder(output);
        if (realRoot is null || realOutput is null)
        {
            issues.Add(new(IssueSeverity.Warning, PathSubject.Reports, "CanonicalCheckUnavailable",
                "Windows could not confirm the real location of one of the folders. The scan will still stop if it ever finds its own reports inside the source.",
                $"source: {realRoot ?? "unknown"}; reports: {realOutput ?? "unknown"}"));
            return;
        }

        if (IsSameOrInside(realOutput, realRoot))
        {
            issues.Add(new(IssueSeverity.Blocked, PathSubject.Reports, "OutputInsideSourceAlias",
                "The report folder is actually inside the source folder (the two paths are different names for overlapping locations, e.g. a SUBST drive or a short 8.3 name).",
                $"source is really {realRoot}; reports would really go to {realOutput}"));
        }

        if (!string.Equals(realRoot, root, StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new(IssueSeverity.Info, PathSubject.Source, "SourceIsAlias",
                $"The source refers to {realRoot}.", $"{root} is another name for {realRoot}"));
        }
    }

    /// <summary>Normalises one user-supplied path. Returns null (with a Blocked issue) if it is unusable.</summary>
    internal static string? Normalize(string? input, PathSubject subject, string? baseDirectory, List<PathIssue> issues)
    {
        var prefix = subject == PathSubject.Source ? "Source" : "Output";
        if (string.IsNullOrWhiteSpace(input))
        {
            issues.Add(new(IssueSeverity.Blocked, subject, prefix + "Empty",
                subject == PathSubject.Source ? "Choose a folder or drive to scan." : "Choose where to save the reports."));
            return null;
        }

        var candidate = input.Trim();
        if (candidate.Length != input.Length)
        {
            issues.Add(new(IssueSeverity.Info, subject, prefix + "WhitespaceIgnored", "Spaces before or after the path were ignored.", $"'{input}'"));
        }

        if (DevicePrefix().IsMatch(candidate))
        {
            issues.Add(new(IssueSeverity.Blocked, subject, prefix + "DevicePath",
                "Device paths (\\\\?\\ or \\\\.\\) are not accepted. Use a normal path such as D:\\Media or \\\\server\\share\\folder.", candidate));
            return null;
        }

        if (BareDrive().IsMatch(candidate))
        {
            // To Windows, "D:" alone means "the current directory on drive D"; the user almost certainly means the root.
            issues.Add(new(IssueSeverity.Info, subject, prefix + "DriveRootAssumed", $"'{candidate}' is taken to mean the whole drive, {candidate}\\.", null));
            candidate += "\\";
        }

        if (candidate.IndexOfAny(InvalidPathCharacters.ToCharArray()) >= 0 || candidate.Any(char.IsControl))
        {
            issues.Add(new(IssueSeverity.Blocked, subject, prefix + "InvalidCharacters",
                "The path contains characters Windows does not allow in folder names ( \" * ? < > | or control characters).", candidate));
            return null;
        }

        if (!Path.IsPathFullyQualified(candidate))
        {
            if (baseDirectory is not null && !Path.IsPathRooted(candidate))
            {
                candidate = Path.Combine(baseDirectory, candidate);
            }

            if (!Path.IsPathFullyQualified(candidate))
            {
                issues.Add(new(IssueSeverity.Blocked, subject, prefix + "NotFullPath",
                    "Use a full path that starts with a drive letter or \\\\server\\share, for example D:\\Media.", candidate));
                return null;
            }
        }

        // Checked on the input BEFORE normalisation as well as after it: .NET's GetFullPath silently turns
        // 'C:\Media.\Out' into 'C:\Media\Out', exactly the kind of rewrite this policy must not hide.
        if (HasSegmentEndingInDotOrSpace(candidate))
        {
            issues.Add(new(IssueSeverity.Blocked, subject, prefix + "TrailingDotOrSpace",
                "A folder name in the path ends with a dot or a space. Windows silently removes these, so the path would not mean what it says.", candidate));
            return null;
        }

        string full;
        try
        {
            full = Path.GetFullPath(candidate);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            issues.Add(new(IssueSeverity.Blocked, subject, prefix + "InvalidPath", "This is not a valid path.", ex.Message));
            return null;
        }

        full = full.TrimEnd('\\', '/');
        if (BareDrive().IsMatch(full)) full += "\\";

        if (DevicePrefix().IsMatch(full) || ReservedDeviceName().IsMatch(full))
        {
            issues.Add(new(IssueSeverity.Blocked, subject, prefix + "ReservedName",
                "The path uses a name Windows reserves for devices (CON, NUL, COM1, LPT1 and similar).", full));
            return null;
        }

        if (full.IndexOf(':', 2) >= 0)
        {
            issues.Add(new(IssueSeverity.Blocked, subject, prefix + "StreamSyntax",
                "The path contains ':' after the drive letter (alternate data stream syntax), which is not allowed.", full));
            return null;
        }

        if (TrailingDotOrSpace().IsMatch(full))
        {
            issues.Add(new(IssueSeverity.Blocked, subject, prefix + "TrailingDotOrSpace",
                "A folder name in the path ends with a dot or a space. Windows silently removes these, so the path would not mean what it says.", full));
            return null;
        }

        return full;
    }

    private static bool HasSegmentEndingInDotOrSpace(string path)
    {
        foreach (var segment in path.Split('\\', '/'))
        {
            if (segment.Length == 0 || segment == "." || segment == "..") continue;
            if (segment[^1] is '.' or ' ') return true;
        }
        return false;
    }

    /// <summary>True if <paramref name="candidate"/> equals <paramref name="container"/> or lies beneath it
    /// (case-insensitive, whole path segments).</summary>
    public static bool IsSameOrInside(string candidate, string container)
    {
        var c = candidate.TrimEnd('\\') + "\\";
        var p = container.TrimEnd('\\') + "\\";
        return c.StartsWith(p, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Walks from <paramref name="path"/> up to its root and describes the first EXISTING component that is a
    /// reparse point, or returns null. Only attributes are read; links are never followed.</summary>
    internal static string? FindReparsePointInPath(string path)
    {
        DirectoryInfo? current = new(path);
        while (current is not null)
        {
            current.Refresh();
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return $"{current.FullName} [{DescribeReparsePoint(current)}]";
            }
            current = current.Parent;
        }
        return null;
    }

    /// <summary>Describes a reparse point from its own reparse data (LinkTarget reads the link without following it).</summary>
    internal static string DescribeReparsePoint(FileSystemInfo item)
    {
        string? target;
        try
        {
            target = item.LinkTarget;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            target = null;
        }

        if (string.IsNullOrEmpty(target))
        {
            return "reparse point of undetermined type (e.g. a cloud-storage placeholder, or a link whose target could not be read)";
        }

        var kind = target.Contains("Volume{", StringComparison.OrdinalIgnoreCase) ? "Mount point" : "Link";
        return $"{kind} -> {target}";
    }

    /// <summary>UNC paths and mapped network drives. GetDriveType is a read-only query.</summary>
    internal static bool IsNetworkPath(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return true;
        try
        {
            return new DriveInfo(path[..1]).DriveType == DriveType.Network;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string? FindCloudSyncFolder(string output)
    {
        foreach (var variable in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
        {
            var folder = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrEmpty(folder) && IsSameOrInside(output, folder)) return folder;
        }
        return null;
    }

    /// <summary>Canonical location of an existing folder, or null if Windows cannot report it.</summary>
    internal static string? TryGetCanonicalPath(string existingDirectory)
    {
        try
        {
            var canonical = NativeMethods.GetFinalDirectoryPath(existingDirectory).TrimEnd('\\');
            return BareDrive().IsMatch(canonical) ? canonical + "\\" : canonical;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Canonical location of a folder that may not exist yet: resolves the deepest existing ancestor and
    /// appends the rest.</summary>
    internal static string? TryGetCanonicalPathOfPossiblyMissingFolder(string path)
    {
        var missing = new Stack<string>();
        DirectoryInfo? current = new(path);
        while (current is not null && !current.Exists)
        {
            missing.Push(current.Name);
            current = current.Parent;
        }
        if (current is null) return null;

        var canonical = TryGetCanonicalPath(current.FullName);
        if (canonical is null) return null;
        while (missing.Count > 0) canonical = Path.Combine(canonical, missing.Pop());
        return canonical;
    }
}

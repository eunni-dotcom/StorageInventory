using StorageInventory.Core.Paths;

namespace StorageInventory.History.Library;

/// <summary>A finding of the Library path rules. <see cref="Code"/> is stable (tests and the "Why is this blocked?" help).</summary>
internal sealed record LibraryPathIssue(IssueSeverity Severity, string Code, string Message, string? Details = null);

/// <summary>Whether the Library lies inside a scanned source (LIB-06c, LIB-06e); the codes are the schema's (§9.4).</summary>
internal enum LibraryInsideSource
{
    No = 0,
    Yes = 1,
    Unknown = 2,
}

/// <summary>The outcome of a Library path check: the issues, and whether any of them blocks.</summary>
internal sealed class LibraryPathAssessment(IReadOnlyList<LibraryPathIssue> issues, LibraryInsideSource insideSource, string? libraryFullPath)
{
    public IReadOnlyList<LibraryPathIssue> Issues { get; } = issues;

    /// <summary>The normalised Library directory, or null when it could not be normalised.</summary>
    public string? LibraryFullPath { get; } = libraryFullPath;

    /// <summary>Only meaningful for <see cref="LibraryPathRules.CheckSource"/>; <see cref="LibraryInsideSource.No"/> otherwise.</summary>
    public LibraryInsideSource InsideSource { get; } = insideSource;

    public bool Blocked => Issues.Any(i => i.Severity == IssueSeverity.Blocked);

    public LibraryPathIssue? FirstBlocking => Issues.FirstOrDefault(i => i.Severity == IssueSeverity.Blocked);
}

/// <summary>
/// The package-free Library path rules of §6.4 (LIB-05a to LIB-06e), as a Library-aware layer beside v1's
/// <see cref="PathPolicy"/>, which is unchanged so that C1 stays behaviour-preserving. Everything here reads metadata
/// only: it creates, writes and deletes nothing. The Library code is location-agnostic (LIB-04): every method takes the
/// directories explicitly, and only the App decides that the default is <c>%LOCALAPPDATA%\StorageInventory\Library</c>.
/// <para>The app-data root is the directory that contains the Library directory (<c>%LOCALAPPDATA%\StorageInventory\</c> in
/// the product, a temporary folder in tests); the source and the report folder are checked against that whole root, not
/// only against the Library directory (LIB-06a/b). Every comparison is made literally AND by canonical location, in both
/// directions, so SUBST letters, 8.3 names and case variants cannot hide an overlap (SEC-26).</para>
/// </summary>
internal static class LibraryPathRules
{
    /// <summary>The environment variables v1's <see cref="PathPolicy"/> uses to find a OneDrive root (LIB-05c).</summary>
    internal static readonly string[] SyncRootVariables = ["OneDrive", "OneDriveConsumer", "OneDriveCommercial"];

    /// <summary>Validates the Library directory itself: LIB-05a (network), LIB-05b (a reparse point anywhere in the path),
    /// LIB-05c (a sync root), and that it lies inside the app-data root it was given (a configuration defect otherwise).
    /// Before every open and before every creation step.</summary>
    /// <param name="libraryDirectory">The Library directory as given.</param>
    /// <param name="appDataRoot">The directory that contains the Library directory.</param>
    /// <param name="getEnvironmentVariable">Reads an environment variable; the real one when null. A seam for tests.</param>
    public static LibraryPathAssessment ValidateLibraryDirectory(string? libraryDirectory, string? appDataRoot, Func<string, string?>? getEnvironmentVariable = null)
    {
        var issues = new List<LibraryPathIssue>();
        var library = NormalizeOrBlock(libraryDirectory, "Library", issues);
        var root = NormalizeOrBlock(appDataRoot, "AppData", issues);
        if (library is null || root is null) return new LibraryPathAssessment(issues, LibraryInsideSource.No, library);

        if (!PathPolicy.IsSameOrInside(library, root) || string.Equals(library.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new(IssueSeverity.Blocked, "LibraryOutsideAppData",
                "The Library folder is not inside StorageInventory's own app-data folder.", $"{library} is not inside {root}"));
        }

        if (PathPolicy.IsNetworkPath(library) || PathPolicy.IsNetworkPath(root))
        {
            issues.Add(new(IssueSeverity.Blocked, "LibraryOnNetwork",
                "The Library cannot be on a network location: SQLite's locking is not reliable over network file systems.", library));
        }

        var link = PathPolicy.FindReparsePointInPath(library) ?? PathPolicy.FindReparsePointInPath(root);
        if (link is not null)
        {
            issues.Add(new(IssueSeverity.Blocked, "LibraryThroughLink",
                "The Library folder passes through a link (junction, symbolic link or similar), for example because your profile folder was relocated. History is not available in that case; scans still work without saving.", link));
        }

        var env = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        foreach (var variable in SyncRootVariables)
        {
            var sync = env(variable);
            if (!string.IsNullOrEmpty(sync) && PathPolicy.IsSameOrInside(library, sync))
            {
                issues.Add(new(IssueSeverity.Blocked, "LibraryInSyncFolder",
                    "The Library cannot be inside a OneDrive folder or another cloud-sync folder: the sync engine would copy its files independently.", sync));
                break;
            }
        }

        // The same overlap, by canonical location: a Library reached through an alias of a sync root is still in it.
        var canonicalLibrary = PathPolicy.TryGetCanonicalPathOfPossiblyMissingFolder(library);
        if (canonicalLibrary is not null)
        {
            if (PathPolicy.IsNetworkPath(canonicalLibrary))
            {
                issues.Add(new(IssueSeverity.Blocked, "LibraryOnNetwork",
                    "The Library cannot be on a network location: SQLite's locking is not reliable over network file systems.", canonicalLibrary));
            }

            foreach (var variable in SyncRootVariables)
            {
                var sync = env(variable);
                if (string.IsNullOrEmpty(sync)) continue;
                var canonicalSync = Directory.Exists(sync) ? PathPolicy.TryGetCanonicalPath(sync) : sync;
                if (canonicalSync is not null && PathPolicy.IsSameOrInside(canonicalLibrary, canonicalSync) && issues.All(i => i.Code != "LibraryInSyncFolder"))
                {
                    issues.Add(new(IssueSeverity.Blocked, "LibraryInSyncFolder",
                        "The Library cannot be inside a OneDrive folder or another cloud-sync folder: the sync engine would copy its files independently.", canonicalSync));
                }
            }
        }

        return new LibraryPathAssessment(issues, LibraryInsideSource.No, library);
    }

    /// <summary>LIB-06a and LIB-06c/e for a scan <b>source</b>: blocked when it equals or lies inside the app-data root (which
    /// contains the Library), literally or by canonical location; the Library lying inside the source is allowed under D-35
    /// and reported as information, with <c>library_inside_source</c> = Yes, or Unknown when a canonical location cannot be
    /// determined (LIB-06e).</summary>
    public static LibraryPathAssessment CheckSource(string? sourcePath, string libraryDirectory, string appDataRoot)
    {
        var issues = new List<LibraryPathIssue>();
        var source = NormalizeOrBlock(sourcePath, "Source", issues);
        var library = NormalizeOrBlock(libraryDirectory, "Library", issues);
        var root = NormalizeOrBlock(appDataRoot, "AppData", issues);
        if (source is null || library is null || root is null) return new LibraryPathAssessment(issues, LibraryInsideSource.Unknown, library);

        var literallyInsideAppData = PathPolicy.IsSameOrInside(source, root);
        var literallyContainsLibrary = PathPolicy.IsSameOrInside(library, source);
        var insideAppData = literallyInsideAppData;
        var containsLibrary = literallyContainsLibrary;
        var canonicalUnknown = false;

        var canonicalSource = Directory.Exists(source) ? PathPolicy.TryGetCanonicalPath(source) : null;
        var canonicalRoot = PathPolicy.TryGetCanonicalPathOfPossiblyMissingFolder(root);
        var canonicalLibrary = PathPolicy.TryGetCanonicalPathOfPossiblyMissingFolder(library);
        if (canonicalSource is null || canonicalRoot is null || canonicalLibrary is null)
        {
            canonicalUnknown = true;
        }
        else
        {
            if (PathPolicy.IsSameOrInside(canonicalSource, canonicalRoot))
            {
                if (!literallyInsideAppData)
                {
                    issues.Add(new(IssueSeverity.Blocked, "SourceInsideAppDataAlias",
                        "The source is actually inside StorageInventory's own app-data folder (the two paths are different names for overlapping locations).",
                        $"source is really {canonicalSource}; app data is really {canonicalRoot}"));
                }
                insideAppData = true;
            }
            if (PathPolicy.IsSameOrInside(canonicalLibrary, canonicalSource)) containsLibrary = true;
        }

        if (literallyInsideAppData)
        {
            issues.Add(new(IssueSeverity.Blocked, "SourceInsideAppData",
                "The source is StorageInventory's own app-data folder or inside it. Scanning it would observe the History Library itself.", $"{source} is within {root}"));
        }

        var inside = containsLibrary ? LibraryInsideSource.Yes : canonicalUnknown ? LibraryInsideSource.Unknown : LibraryInsideSource.No;
        if (!insideAppData)
        {
            switch (inside)
            {
                case LibraryInsideSource.Yes:
                    issues.Add(new(IssueSeverity.Info, "LibraryInsideSource",
                        "The History Library is inside this source. It is not written while the source is scanned, and this scan's own data appears only in later snapshots.", library));
                    break;
                case LibraryInsideSource.Unknown:
                    issues.Add(new(IssueSeverity.Info, "LibraryInsideSourceUnknown",
                        "Windows could not confirm whether the History Library is inside this source. It is treated as possibly inside.", library));
                    break;
            }
        }

        return new LibraryPathAssessment(issues, inside, library);
    }

    /// <summary>LIB-06b and LIB-06d for the <b>report folder</b>: blocked when it equals or lies inside the app-data root,
    /// literally or by canonical location; a report folder that contains the Library is allowed (harmless).</summary>
    public static LibraryPathAssessment CheckReportFolder(string? reportFolder, string libraryDirectory, string appDataRoot)
    {
        var issues = new List<LibraryPathIssue>();
        var folder = NormalizeOrBlock(reportFolder, "Output", issues);
        var library = NormalizeOrBlock(libraryDirectory, "Library", issues);
        var root = NormalizeOrBlock(appDataRoot, "AppData", issues);
        if (folder is null || library is null || root is null) return new LibraryPathAssessment(issues, LibraryInsideSource.No, library);

        var literal = PathPolicy.IsSameOrInside(folder, root);
        if (literal)
        {
            issues.Add(new(IssueSeverity.Blocked, "OutputInsideAppData",
                "The report folder is StorageInventory's own app-data folder or inside it. That folder is exclusively StorageInventory's.", $"{folder} is within {root}"));
        }
        else
        {
            var canonicalFolder = PathPolicy.TryGetCanonicalPathOfPossiblyMissingFolder(folder);
            var canonicalRoot = PathPolicy.TryGetCanonicalPathOfPossiblyMissingFolder(root);
            if (canonicalFolder is not null && canonicalRoot is not null && PathPolicy.IsSameOrInside(canonicalFolder, canonicalRoot))
            {
                issues.Add(new(IssueSeverity.Blocked, "OutputInsideAppDataAlias",
                    "The report folder is actually inside StorageInventory's own app-data folder (the two paths are different names for overlapping locations).",
                    $"reports would really go to {canonicalFolder}; app data is really {canonicalRoot}"));
            }
        }

        return new LibraryPathAssessment(issues, LibraryInsideSource.No, library);
    }

    private static string? NormalizeOrBlock(string? input, string what, List<LibraryPathIssue> issues)
    {
        var inner = new List<PathIssue>();
        var full = PathPolicy.Normalize(input, PathSubject.Reports, baseDirectory: null, inner);
        if (full is null)
        {
            var first = inner.FirstOrDefault(i => i.Severity == IssueSeverity.Blocked);
            issues.Add(new(IssueSeverity.Blocked, what + "PathInvalid", first?.Message ?? "This is not a usable path.", first?.Details ?? input));
        }
        return full;
    }
}

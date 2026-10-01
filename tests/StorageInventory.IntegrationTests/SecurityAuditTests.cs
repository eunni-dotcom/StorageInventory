using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using StorageInventory.Core;
using StorageInventory.Testing;

namespace StorageInventory.IntegrationTests;

/// <summary>
/// Machine-checked security invariants of the SHIPPED code (src/). Each capability that can change anything outside
/// the process is allowed in exactly one audited place; everything else is forbidden. See docs/native-security-review.md.
/// </summary>
public static class SecurityAuditTests
{
    private static readonly Lazy<Dictionary<string, string>> Sources = new(() =>
        Directory.EnumerateFiles(Path.Combine(TestEnvironment.RepoRoot, "src"), "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".xaml", StringComparison.Ordinal) || f.EndsWith(".csproj", StringComparison.Ordinal) || f.EndsWith(".manifest", StringComparison.Ordinal))
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToDictionary(f => Path.GetRelativePath(TestEnvironment.RepoRoot, f), File.ReadAllText));

    /// <summary>Files (relative to the repo) whose code matches the pattern, ignoring // comments.</summary>
    private static List<string> FilesMatching(string pattern) => FilesMatching(pattern, Sources.Value);

    /// <summary>The same matcher over any set of sources, so a rule can be shown to reject a violating snippet.</summary>
    private static List<string> FilesMatching(string pattern, IReadOnlyDictionary<string, string> sources)
    {
        var regex = new Regex(pattern, RegexOptions.Multiline);
        return sources
            .Where(kv => regex.IsMatch(string.Join("\n", kv.Value.Split('\n').Select(StripComment))))
            .Select(kv => kv.Key.Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static string StripComment(string line)
    {
        var i = line.IndexOf("//", StringComparison.Ordinal);
        return i >= 0 && !line[..i].Contains('"') ? line[..i] : line;
    }

    private static void OnlyIn(string what, string pattern, params string[] allowed)
    {
        var found = FilesMatching(pattern);
        Assert.SequenceEqual(allowed.Order(StringComparer.Ordinal), found, $"{what} may only appear in the audited location(s)");
    }

    [Test]
    public static void File_creation_happens_only_in_ReportRun()
    {
        OnlyIn("FileMode.CreateNew", @"FileMode\.CreateNew", "src/StorageInventory.Core/Reports/ReportRun.cs");
        OnlyIn("new FileStream", @"new FileStream\(",
            "src/StorageInventory.Core/Reports/ReportCsvReader.cs", "src/StorageInventory.Core/Reports/ReportRun.cs", "src/StorageInventory.Core/Reports/ReportWriters.cs");
        OnlyIn("Directory.CreateDirectory", @"Directory\.CreateDirectory\(", "src/StorageInventory.Core/Reports/ReportRun.cs");
        Assert.Equal(0, FilesMatching(@"FileMode\.(Create|OpenOrCreate|Truncate|Append)\b").Count, "no overwriting or appending file modes");
        Assert.Equal(0, FilesMatching(@"File\.(Create|WriteAll\w*|AppendAll\w*|AppendText|CreateText|Copy|Replace|Encrypt|Decrypt|SetAttributes|Set\w*Time\w*|SetAccessControl|SetUnixFileMode)\(").Count,
            "no other File writing/metadata APIs");
    }

    [Test]
    public static void Readers_only_ever_open_files_for_reading()
    {
        foreach (var file in new[] { "src/StorageInventory.Core/Reports/ReportCsvReader.cs", "src/StorageInventory.Core/Reports/ReportWriters.cs" })
        {
            var code = Sources.Value[file.Replace('/', Path.DirectorySeparatorChar)];
            foreach (Match m in Regex.Matches(code, @"new FileStream\(([^;]*)\)"))
            {
                Assert.Contains("FileMode.Open", m.Groups[1].Value);
                Assert.Contains("FileAccess.Read", m.Groups[1].Value);
            }
        }
    }

    [Test]
    public static void Deletes_and_renames_happen_only_in_ReportRun_and_nothing_deletes_folders()
    {
        OnlyIn("File.Delete", @"File\.Delete\(", "src/StorageInventory.Core/Reports/ReportRun.cs");
        OnlyIn("File.Move", @"File\.Move\(", "src/StorageInventory.Core/Reports/ReportRun.cs");
        Assert.Equal(0, FilesMatching(@"(Directory\.(Delete|Move)|\.Delete\(\s*(true|recursive))").Count, "no folder deletion or moving anywhere");
        Assert.Contains("overwrite: false", Sources.Value[Path.Combine("src", "StorageInventory.Core", "Reports", "ReportRun.cs")]);
    }

    [Test]
    public static void Process_launching_happens_only_in_ReportOpener()
    {
        OnlyIn("Process.Start", @"Process\.Start\(", "src/StorageInventory.App/Services/ReportOpener.cs");
        Assert.Equal(0, FilesMatching(@"Verb\s*=|""runas""").Count, "never requests elevation");
    }

    [Test]
    public static void No_network_registry_dynamic_code_or_persistence_APIs()
    {
        var forbidden = new Dictionary<string, string>
        {
            ["network"] = @"System\.Net\b|HttpClient|WebClient|WebRequest|\bSocket\b|TcpClient|UdpClient|Dns\.",
            ["registry"] = @"Microsoft\.Win32\.Registry|\bRegistry\.|RegistryKey",
            ["dynamic code"] = @"Assembly\.Load|Activator\.CreateInstance|Type\.GetType\(|DynamicMethod|Reflection\.Emit|CSharpScript|Process\.GetProcess",
            ["environment changes"] = @"Environment\.SetEnvironmentVariable",
            // Windows Task Scheduler (COM ITaskService / the TaskScheduler wrapper namespace), not .NET's thread-pool
            // TaskScheduler.Default, which InventoryScanner legitimately uses to run the scan on a background thread.
            ["persistence/services"] = @"ServiceController|ITaskService|Win32\.TaskScheduler|schtasks|Startup\b.*Registry|IsolatedStorage|Properties\.Settings",
            ["telemetry"] = @"(?i)telemetry|analytics|ApplicationInsights|crash ?report",
        };
        foreach (var (what, pattern) in forbidden)
        {
            var hits = FilesMatching(pattern);
            Assert.Equal(0, hits.Count, $"{what}: {string.Join(", ", hits)}");
        }
    }

    [Test]
    public static void The_only_native_calls_are_two_read_only_kernel32_functions()
    {
        var pinvokes = new[] { typeof(StorageScanResult).Assembly, typeof(StorageInventory.App.App).Assembly }
            .SelectMany(a => a.GetTypes())
            .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Select(m => (Method: m, Import: m.GetCustomAttribute<DllImportAttribute>()))
            .Where(x => x.Import is not null)
            .Select(x => $"{x.Method.DeclaringType!.Name}.{x.Method.Name} -> {x.Import!.Value}!{x.Import.EntryPoint}")
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.SequenceEqual(
            ["NativeMethods.CreateFile -> kernel32.dll!CreateFileW", "NativeMethods.GetFinalPathNameByHandle -> kernel32.dll!GetFinalPathNameByHandleW"],
            pinvokes);
        var native = Sources.Value[Path.Combine("src", "StorageInventory.Core", "Paths", "NativeMethods.cs")];
        Assert.Contains("CreateFile(ToExtendedPath(directory), 0,", native);   // desiredAccess = 0: no read/write/delete rights
    }

    [Test]
    public static void Core_has_no_UI_process_or_network_dependencies()
    {
        var refs = typeof(StorageScanResult).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();
        foreach (var forbidden in new[] { "PresentationFramework", "PresentationCore", "WindowsBase", "System.Windows.Forms", "System.Diagnostics.Process",
                                          "System.Net.Http", "System.Net.Sockets", "System.Net.Primitives", "Microsoft.Win32.Registry" })
        {
            Assert.False(refs.Contains(forbidden), $"Core references {forbidden}");
        }
    }

    [Test]
    public static void The_app_never_asks_for_elevation()
    {
        var manifest = Sources.Value[Path.Combine("src", "StorageInventory.App", "app.manifest")];
        Assert.Contains("level=\"asInvoker\"", manifest);
        Assert.False(manifest.Contains("requireAdministrator") || manifest.Contains("highestAvailable"), "no elevation request");
    }

    [Test]
    public static void No_package_references_anywhere()
    {
        Assert.Equal(0, FilesMatching(@"<PackageReference").Count, "the product builds from the SDK alone");
    }

    // ---- C1 (v1.1): the observer fan-out and the spool codec. The v1 rules above are unchanged. ----

    /// <summary>Each C1 rule: what may appear, the pattern, and the only files allowed to contain it.</summary>
    private static readonly (string What, string Pattern, string[] Allowed)[] C1Rules =
    [
        // SINK-01: one traversal feeds every consumer. Only the engine lists directories.
        ("filesystem enumeration", @"FileSystemEnumera(ble|tor)|\.(Enumerate|Get)(Files|Directories|FileSystemEntries|FileSystemInfos)\(",
            ["src/StorageInventory.Core/Scanning/ScanEngine.cs"]),
        // ...and only the pipeline creates an engine (once per scan; see the count below).
        ("ScanEngine construction", @"new ScanEngine\(", ["src/StorageInventory.Core/InventoryScanner.cs"]),
        // SINK-05: OutputGuard wraps only the critical observer, in the pipeline.
        ("OutputGuard construction", @"new OutputGuard\(", ["src/StorageInventory.Core/InventoryScanner.cs"]),
        // SPOOL-23: no spool record reaches a caller before the verification pass V; only V creates a VerifiedSpool.
        ("VerifiedSpool construction", @"new VerifiedSpool\(", ["src/StorageInventory.Core/Spool/SpoolReader.cs"]),
    ];

    /// <summary>C1 creates no spool file: the codec works on a Stream it is given (the file is C5's, in ReportRun).</summary>
    private const string FileSystemAccess = @"\bFile\.|\bDirectory\.|\bFileStream\b|\bFileMode\.|\bFileInfo\b|\bDirectoryInfo\b|\bFileOptions\.|DeleteOnClose|Path\.GetTempFileName";

    private static List<string> SpoolFilesTouchingTheFileSystem(IReadOnlyDictionary<string, string> sources) =>
        FilesMatching(FileSystemAccess, sources.Where(kv => kv.Key.Replace('\\', '/').StartsWith("src/StorageInventory.Core/Spool/", StringComparison.Ordinal))
            .ToDictionary(kv => kv.Key, kv => kv.Value));

    [Test]
    public static void C1_observer_and_spool_capabilities_appear_only_in_their_audited_places()
    {
        foreach (var (what, pattern, allowed) in C1Rules) OnlyIn(what, pattern, allowed);

        var pipeline = Sources.Value[Path.Combine("src", "StorageInventory.Core", "InventoryScanner.cs")];
        Assert.Equal(1, Regex.Matches(pipeline, @"new ScanEngine\(").Count, "one engine per scan");
        Assert.Equal(1, Regex.Matches(pipeline, @"engine\.Run\(").Count, "one traversal per scan");
        Assert.Contains("new OutputGuard(sink)", pipeline);   // the guard wraps the critical CSV sink, nothing else

        Assert.Equal(0, SpoolFilesTouchingTheFileSystem(Sources.Value).Count, "the spool codec creates, opens or deletes no file in C1");
        Assert.True(Sources.Value.Keys.Count(k => k.Replace('\\', '/').StartsWith("src/StorageInventory.Core/Spool/", StringComparison.Ordinal)) >= 4, "the codec sources were found");
    }

    [Test]
    public static void C1_rules_reject_violating_snippets()
    {
        // Negative self-tests (A-21): every C1 rule finds a planted violation in a file it does not allow.
        var rogue = new Dictionary<string, string>
        {
            ["src/StorageInventory.Core/Reports/Rogue.cs"] = """
                var listing = new FileSystemEnumerable<int>(path, Transform, options);
                var names = new DirectoryInfo(path).GetFiles();
                var second = new ScanEngine(root, observer, names, null);
                var guarded = new OutputGuard(spoolWriter);
                var trusted = new VerifiedSpool(stream, 0, header, 0, trailer, runs);
                """,
            ["src/StorageInventory.Core/Spool/RogueSpool.cs"] = "using var spool = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);",
        };
        foreach (var (what, pattern, _) in C1Rules)
        {
            Assert.True(FilesMatching(pattern, rogue).Contains("src/StorageInventory.Core/Reports/Rogue.cs"), $"the {what} rule misses a violation");
        }
        Assert.SequenceEqual(["src/StorageInventory.Core/Spool/RogueSpool.cs"], SpoolFilesTouchingTheFileSystem(rogue), "the spool file-access rule misses a violation");
        Assert.Equal(0, FilesMatching(C1Rules[0].Pattern, new Dictionary<string, string> { ["src/x.cs"] = "// new FileSystemEnumerable<int>(p) in a comment" }).Count,
            "comments are not code");
    }

    [Test]
    public static void C1_internals_are_visible_only_to_approved_assemblies_and_the_observer_layer_is_not_public()
    {
        // A-13, the part that applies in C1: Core's InternalsVisibleTo list may only name assemblies of the approved set
        // (it becomes exactly that set when History and Library exist), and nothing of the observer or spool layer is
        // public. In C1 the list is still v1's.
        var core = typeof(StorageScanResult).Assembly;
        var visibleTo = core.GetCustomAttributes<System.Runtime.CompilerServices.InternalsVisibleToAttribute>().Select(a => a.AssemblyName).Order(StringComparer.Ordinal).ToList();
        string[] approved = ["StorageInventory.Core.Tests", "StorageInventory.History", "StorageInventory.History.Tests", "StorageInventory.IntegrationTests",
            "StorageInventory.Library", "StorageInventory.Library.Tests"];
        Assert.True(visibleTo.All(approved.Contains), "unapproved InternalsVisibleTo: " + string.Join(", ", visibleTo));
        Assert.SequenceEqual(["StorageInventory.Core.Tests", "StorageInventory.IntegrationTests"], visibleTo, "C1 keeps v1's list");
        foreach (var t in core.GetTypes().Where(t => t.Namespace is "StorageInventory.Core.Scanning" or "StorageInventory.Core.Spool"))
        {
            Assert.False(t.IsVisible, t.FullName + " is public");
        }
        var scanner = typeof(InventoryScanner);
        Assert.False(scanner.GetMethod("ScanObserved", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.IsPublic, "the observed entry point is internal");
    }
}

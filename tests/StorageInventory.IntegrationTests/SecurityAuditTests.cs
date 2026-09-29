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
    private static List<string> FilesMatching(string pattern)
    {
        var regex = new Regex(pattern, RegexOptions.Multiline);
        return Sources.Value
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
}

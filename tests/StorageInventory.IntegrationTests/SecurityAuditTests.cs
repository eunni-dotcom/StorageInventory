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

    // ---- v1.1 C3 (A-08): the first-party native surface is exactly six read-only kernel32 functions ----

    /// <summary>Every first-party assembly that can hold a P/Invoke: Core, History and the App (Library joins in C4).</summary>
    private static readonly Assembly[] FirstPartyAssemblies =
    [
        typeof(StorageScanResult).Assembly,
        typeof(StorageInventory.History.Identity.IdentityMatching).Assembly,
        typeof(StorageInventory.App.App).Assembly,
    ];

    /// <summary>The six, as "Type.Method -> dll!entry point": the two v1 functions and the four C3 identity queries (§7.8).</summary>
    private static readonly string[] SixNativeCalls =
    [
        "NativeMethods.CreateFile -> kernel32.dll!CreateFileW",
        "NativeMethods.GetDiskFreeSpaceEx -> kernel32.dll!GetDiskFreeSpaceExW",
        "NativeMethods.GetFileInformationByHandleEx -> kernel32.dll!GetFileInformationByHandleEx",
        "NativeMethods.GetFinalPathNameByHandle -> kernel32.dll!GetFinalPathNameByHandleW",
        "NativeMethods.GetVolumeInformationByHandle -> kernel32.dll!GetVolumeInformationByHandleW",
        "NativeMethods.GetVolumePathName -> kernel32.dll!GetVolumePathNameW",
    ];

    /// <summary>Every P/Invoke declared by the given types, found by reflection, as "Type.Method -> dll!entry point".</summary>
    private static List<string> PInvokesOf(IEnumerable<Type> types) => types
        .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        .Select(m => (Method: m, Import: m.GetCustomAttribute<DllImportAttribute>()))
        .Where(x => x.Import is not null)
        .Select(x => $"{x.Method.DeclaringType!.Name}.{x.Method.Name} -> {x.Import!.Value}!{x.Import.EntryPoint}")
        .Order(StringComparer.Ordinal)
        .ToList();

    /// <summary>The A-08 reflection rule as a function, so a negative self-test can run it on a type that breaks it: what
    /// differs from the allowed six, and any function that is not in kernel32.</summary>
    private static List<string> NativeSurfaceViolations(IEnumerable<string> found)
    {
        var all = found.ToList();
        var violations = all.Where(f => !SixNativeCalls.Contains(f)).Select(f => "not allowed: " + f).ToList();
        violations.AddRange(SixNativeCalls.Where(a => !all.Contains(a)).Select(a => "missing: " + a));
        violations.AddRange(all.Where(f => !f.Contains("-> kernel32.dll!", StringComparison.Ordinal)).Select(f => "not kernel32: " + f));
        return violations;
    }

    private const string NativeMethodsFile = "src/StorageInventory.Core/Paths/NativeMethods.cs";

    /// <summary>The text rules of A-08 over a set of sources: <c>CreateFile</c> is called only with desired access 0 and
    /// <c>GetFileInformationByHandleEx</c> only with <c>FileIdInfo</c>, whose enum has no other member and is never cast to.</summary>
    private static List<string> NativeCallShapeViolations(IReadOnlyDictionary<string, string> sources)
    {
        var violations = new List<string>();
        foreach (var (file, raw) in sources)
        {
            var code = string.Join("\n", raw.Split('\n').Select(StripComment));
            // a call is "Name(" that is not the extern declaration
            var createCalls = Regex.Matches(code, @"(?<!extern SafeFileHandle )\bCreateFile\(([^;]*)\)", RegexOptions.Multiline);
            foreach (Match call in createCalls)
            {
                var arguments = SplitArguments(call.Groups[1].Value);
                if (arguments.Count < 2 || arguments[1].Trim() != "0") violations.Add($"{file}: CreateFile with desired access '{(arguments.Count > 1 ? arguments[1].Trim() : "?")}' (must be the literal 0)");
            }
            foreach (Match call in Regex.Matches(code, @"(?<!extern bool )\bGetFileInformationByHandleEx\(([^;]*)\)"))
            {
                var arguments = SplitArguments(call.Groups[1].Value);
                if (arguments.Count < 2 || arguments[1].Trim() != "FileInfoByHandleClass.FileIdInfo") violations.Add($"{file}: GetFileInformationByHandleEx with class '{(arguments.Count > 1 ? arguments[1].Trim() : "?")}' (must be FileIdInfo)");
            }
            if (Regex.IsMatch(code, @"\(\s*FileInfoByHandleClass\s*\)")) violations.Add($"{file}: a cast to FileInfoByHandleClass");
            if (Regex.IsMatch(code, @"enum\s+FileInfoByHandleClass\b[^{]*\{([^}]*)\}", RegexOptions.Singleline))
            {
                var members = Regex.Match(code, @"enum\s+FileInfoByHandleClass\b[^{]*\{([^}]*)\}", RegexOptions.Singleline).Groups[1].Value
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (members.Length != 1 || members[0] != "FileIdInfo = 18") violations.Add($"{file}: FileInfoByHandleClass has members [{string.Join("; ", members)}] (only FileIdInfo = 18 is allowed)");
            }
        }
        return violations;
    }

    /// <summary>Splits a call's argument text at top-level commas.</summary>
    private static List<string> SplitArguments(string text)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is '(' or '[' or '{') depth++;
            else if (text[i] is ')' or ']' or '}') depth--;
            else if (text[i] == ',' && depth == 0) { parts.Add(text[start..i]); start = i + 1; }
        }
        parts.Add(text[start..]);
        return parts;
    }

    [Test]
    public static void The_only_native_calls_are_six_read_only_kernel32_functions()
    {
        // A-08, by reflection over every first-party assembly: exactly the two v1 functions and the four C3 identity queries.
        var found = PInvokesOf(FirstPartyAssemblies.SelectMany(a => a.GetTypes()));
        Assert.SequenceEqual(SixNativeCalls, found);
        Assert.Equal(6, found.Count, "no seventh native call");
        Assert.Equal(0, NativeSurfaceViolations(found).Count, "the surface rule finds nothing wrong with the shipped assemblies");
        Assert.True(found.All(f => f.StartsWith("NativeMethods.", StringComparison.Ordinal)), "every P/Invoke lives in NativeMethods");

        // ...and no first-party SOURCE declares or binds a native function anywhere else (this also covers projects with no assembly here yet)
        OnlyIn("native declarations", @"\[DllImport|\[LibraryImport|\bextern\s+\w", NativeMethodsFile);

        // GetFileInformationByHandleEx can only be asked for FileIdInfo: by type (a one-member enum) and by every call site
        var infoClass = typeof(StorageScanResult).Assembly.GetType("StorageInventory.Core.Paths.NativeMethods")!
            .GetMethod("GetFileInformationByHandleEx", BindingFlags.Static | BindingFlags.NonPublic)!.GetParameters()[1].ParameterType;
        Assert.True(infoClass.IsEnum, "the information class parameter is an enum");
        Assert.SequenceEqual(["FileIdInfo"], Enum.GetNames(infoClass), "the enum's only member");
        Assert.Equal(18, Convert.ToInt32(Enum.GetValues(infoClass).GetValue(0)), "FILE_INFO_BY_HANDLE_CLASS FileIdInfo");

        // text rules over the real source: CreateFile at desired access 0, GetFileInformationByHandleEx with FileIdInfo only
        var native = Sources.Value.Where(kv => kv.Key.Replace('\\', '/') == NativeMethodsFile).ToDictionary(kv => kv.Key, kv => kv.Value);
        Assert.Equal(1, native.Count, "NativeMethods.cs was found");
        Assert.Equal(0, NativeCallShapeViolations(Sources.Value).Count, string.Join("; ", NativeCallShapeViolations(Sources.Value)));
        Assert.Equal(1, Regex.Matches(native.Values.Single(), @"(?<!extern SafeFileHandle )\bCreateFile\(").Count, "one CreateFile call site: the single place a handle is created");
        Assert.Equal(1, Regex.Matches(native.Values.Single(), @"(?<!extern bool )\bGetFileInformationByHandleEx\(").Count, "one GetFileInformationByHandleEx call site");
    }

    [Test]
    public static void The_native_rules_reject_violating_fixtures()
    {
        // Negative self-tests (A-21): each A-08 rule finds a planted violation, so the audit cannot silently go blind.

        // a seventh function, a WRITE-capable one, and a function from another DLL, declared by a real P/Invoke fixture
        var fixtureTypes = new[] { typeof(RogueNativeFixtures) };
        var rogue = PInvokesOf(fixtureTypes);
        Assert.Equal(2, rogue.Count, "the fixture declares two P/Invokes: " + string.Join(", ", rogue));
        var withExtras = SixNativeCalls.Concat(rogue).ToList();
        var violations = NativeSurfaceViolations(withExtras);
        Assert.True(violations.Any(v => v.Contains("DeleteFileW", StringComparison.Ordinal)), "a seventh (write-capable) kernel32 function is detected: " + string.Join("; ", violations));
        Assert.True(violations.Any(v => v.Contains("not kernel32", StringComparison.Ordinal) && v.Contains("MessageBoxW", StringComparison.Ordinal)), "a function from another DLL is detected");
        Assert.True(NativeSurfaceViolations(SixNativeCalls.Take(5)).Any(v => v.StartsWith("missing:", StringComparison.Ordinal)), "a removed function is noticed too");
        Assert.Equal(0, NativeSurfaceViolations(SixNativeCalls).Count, "the exact six are accepted");

        // the text rules: wrong desired access, another information class, a cast, a second enum member
        var sources = new Dictionary<string, string>
        {
            ["src/Rogue/Access.cs"] = "var h = CreateFile(path, GENERIC_READ, share, IntPtr.Zero, OpenExisting, flags, IntPtr.Zero);",
            ["src/Rogue/Class.cs"] = "GetFileInformationByHandleEx(handle, FileInfoByHandleClass.FileStandardInfo, out info, size);",
            ["src/Rogue/Cast.cs"] = "var c = (FileInfoByHandleClass)5;",
            ["src/Rogue/Enum.cs"] = "private enum FileInfoByHandleClass { FileIdInfo = 18, FileBasicInfo = 0 }",
        };
        var shape = NativeCallShapeViolations(sources);
        foreach (var file in new[] { "Access.cs", "Class.cs", "Cast.cs", "Enum.cs" })
        {
            Assert.True(shape.Any(v => v.Contains(file, StringComparison.Ordinal)), $"the call-shape rule misses the {file} violation: {string.Join("; ", shape)}");
        }
        var clean = new Dictionary<string, string>
        {
            ["src/Fine.cs"] = "var h = CreateFile(path, 0, share, IntPtr.Zero, OpenExisting, flags, IntPtr.Zero); GetFileInformationByHandleEx(handle, FileInfoByHandleClass.FileIdInfo, out info, size);",
        };
        Assert.Equal(0, NativeCallShapeViolations(clean).Count, "the permitted shapes are accepted");

        // the binding-outside-NativeMethods rule
        var declarations = new Dictionary<string, string> { ["src/StorageInventory.Core/Rogue.cs"] = "[DllImport(\"kernel32.dll\")] static extern bool DeleteFileW(string p);" };
        Assert.SequenceEqual(["src/StorageInventory.Core/Rogue.cs"], FilesMatching(@"\[DllImport|\[LibraryImport|\bextern\s+\w", declarations));
    }

    [Test]
    public static void Native_functions_cannot_be_bound_any_other_way()
    {
        // No dynamic binding that would add a seventh call without a declaration: function pointers, manual loading, the
        // runtime's NativeLibrary helpers. (A-10 will name the Library-related ones as well in C4.)
        Assert.Equal(0, FilesMatching(@"\bNativeLibrary\b|SetDllImportResolver|GetProcAddress|\bLoadLibrary\w*\b|delegate\s*\*|GetDelegateForFunctionPointer|GetFunctionPointerForDelegate|DllImportSearchPath").Count,
            "no manual native binding anywhere in src");
    }

    /// <summary>A-12 as a function (so a negative self-test can run it on a violating list): what an assembly may not
    /// reference: UI, process, network or registry assemblies, and SQLite (which only the Library assembly will ever use).</summary>
    private static List<string> ReferenceViolations(string assembly, IEnumerable<string> references)
    {
        string[] forbidden = ["PresentationFramework", "PresentationCore", "WindowsBase", "System.Windows.Forms", "System.Diagnostics.Process",
                              "System.Net.Http", "System.Net.Sockets", "System.Net.Primitives", "Microsoft.Win32.Registry"];
        return references
            .Where(r => forbidden.Contains(r) || r.StartsWith("Microsoft.Data.Sqlite", StringComparison.Ordinal) || r.StartsWith("SQLitePCL", StringComparison.Ordinal))
            .Select(r => $"{assembly} references {r}")
            .ToList();
    }

    /// <summary>A-13 as a function: the friend assemblies must all be approved, and the list must be exactly the expected one.</summary>
    private static List<string> FriendViolations(string assembly, IEnumerable<string> visibleTo, IReadOnlyCollection<string> approved, IReadOnlyCollection<string> expectedExactly)
    {
        var actual = visibleTo.Order(StringComparer.Ordinal).ToList();
        var violations = actual.Where(f => !approved.Contains(f)).Select(f => $"{assembly}: unapproved friend {f}").ToList();
        if (!actual.SequenceEqual(expectedExactly.Order(StringComparer.Ordinal))) violations.Add($"{assembly}: friends are [{string.Join(", ", actual)}], expected exactly [{string.Join(", ", expectedExactly)}]");
        return violations;
    }

    [Test]
    public static void Core_and_History_have_no_UI_process_network_or_database_dependencies()
    {
        // A-12: unchanged for Core; History additionally may not reference any UI assembly, and neither may reference SQLite.
        foreach (var (name, assembly) in new[] { ("Core", typeof(StorageScanResult).Assembly), ("History", typeof(StorageInventory.History.Identity.IdentityMatching).Assembly) })
        {
            var violations = ReferenceViolations(name, assembly.GetReferencedAssemblies().Select(a => a.Name!));
            Assert.Equal(0, violations.Count, string.Join("; ", violations));
        }

        // History's whole dependency graph: Core and the framework, nothing else
        var history = typeof(StorageInventory.History.Identity.IdentityMatching).Assembly.GetReferencedAssemblies().Select(a => a.Name!).Where(n => n.StartsWith("StorageInventory", StringComparison.Ordinal)).ToList();
        Assert.SequenceEqual(["StorageInventory.Core"], history, "History references only Core among first-party assemblies");

        // and no project under src carries a package reference (the product still builds from the SDK alone in C3)
        Assert.Equal(0, FilesMatching(@"<PackageReference|SQLitePCL|Microsoft\.Data\.Sqlite").Count, "no SQLite or package reference in src");
    }

    [Test]
    public static void The_dependency_and_friend_rules_reject_violating_inputs()
    {
        // Negative self-tests (A-21) for the A-12 and A-13 rules that C3 extended to History.
        var bad = ReferenceViolations("History", ["System.Runtime", "StorageInventory.Core", "Microsoft.Data.Sqlite", "SQLitePCLRaw.core", "PresentationFramework", "System.Net.Http", "Microsoft.Win32.Registry", "System.Diagnostics.Process"]);
        foreach (var expected in new[] { "Microsoft.Data.Sqlite", "SQLitePCLRaw.core", "PresentationFramework", "System.Net.Http", "Microsoft.Win32.Registry", "System.Diagnostics.Process" })
        {
            Assert.True(bad.Any(v => v.EndsWith(expected, StringComparison.Ordinal)), $"the dependency rule misses a reference to {expected}: {string.Join("; ", bad)}");
        }
        Assert.Equal(0, ReferenceViolations("History", ["System.Runtime", "System.Linq", "System.Collections", "StorageInventory.Core"]).Count, "ordinary references are accepted");

        string[] approved = ["StorageInventory.Core.Tests", "StorageInventory.History", "StorageInventory.History.Tests", "StorageInventory.IntegrationTests", "StorageInventory.Library", "StorageInventory.Library.Tests"];
        string[] expectedList = ["StorageInventory.Core.Tests", "StorageInventory.History", "StorageInventory.History.Tests", "StorageInventory.IntegrationTests"];
        Assert.Equal(0, FriendViolations("Core", expectedList, approved, expectedList).Count, "the expected list passes");
        Assert.True(FriendViolations("Core", [.. expectedList, "StorageInventory.App"], approved, expectedList).Any(v => v.Contains("unapproved friend StorageInventory.App", StringComparison.Ordinal)), "an unapproved friend is detected");
        Assert.True(FriendViolations("Core", [.. expectedList, "StorageInventory.Library"], approved, expectedList).Count == 1, "an approved assembly that is not in this gate's list is detected as a list difference");
        Assert.True(FriendViolations("Core", expectedList.Take(3), approved, expectedList).Count == 1, "a missing friend is detected");
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
    public static void Core_internals_are_visible_only_to_approved_assemblies_and_the_observer_spool_and_identity_layers_are_not_public()
    {
        // A-13: Core's InternalsVisibleTo list may only name assemblies of the approved set, and becomes exactly that set
        // as History and Library appear. v1.1 C3 adds History and its tests (Library and its tests join in C4). Nothing of
        // the observer, spool or identity layers is public.
        var core = typeof(StorageScanResult).Assembly;
        var visibleTo = core.GetCustomAttributes<System.Runtime.CompilerServices.InternalsVisibleToAttribute>().Select(a => a.AssemblyName).Order(StringComparer.Ordinal).ToList();
        string[] approved = ["StorageInventory.Core.Tests", "StorageInventory.History", "StorageInventory.History.Tests", "StorageInventory.IntegrationTests",
            "StorageInventory.Library", "StorageInventory.Library.Tests"];
        var friendViolations = FriendViolations("Core", visibleTo, approved,
            ["StorageInventory.Core.Tests", "StorageInventory.History", "StorageInventory.History.Tests", "StorageInventory.IntegrationTests"]);   // C3's list: v1's two test assemblies plus History and History.Tests
        Assert.Equal(0, friendViolations.Count, string.Join("; ", friendViolations));
        foreach (var t in core.GetTypes().Where(t => t.Namespace is "StorageInventory.Core.Scanning" or "StorageInventory.Core.Spool" or "StorageInventory.Core.Identity"))
        {
            Assert.False(t.IsVisible, t.FullName + " is public");
        }
        var scanner = typeof(InventoryScanner);
        Assert.False(scanner.GetMethod("ScanObserved", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.IsPublic, "the observed entry point is internal");
        Assert.Equal(0, core.GetTypes().Count(t => t.Name == "NativeMethods" && t.IsVisible), "the native methods are never public");
    }

    [Test]
    public static void History_exposes_nothing_publicly_and_shares_its_internals_only_with_its_tests()
    {
        // C3 adds the History assembly. It has no public API (the identity matching types are internal, shared with the
        // approved friend assemblies only); a public expansion would be a stop condition for the gate.
        var history = typeof(StorageInventory.History.Identity.IdentityMatching).Assembly;
        Assert.Equal(0, history.GetExportedTypes().Length, "public types: " + string.Join(", ", history.GetExportedTypes().Select(t => t.FullName)));
        var visibleTo = history.GetCustomAttributes<System.Runtime.CompilerServices.InternalsVisibleToAttribute>().Select(a => a.AssemblyName).Order(StringComparer.Ordinal).ToList();
        var friendViolations = FriendViolations("History", visibleTo, ["StorageInventory.History.Tests", "StorageInventory.IntegrationTests", "StorageInventory.Library", "StorageInventory.Library.Tests"],
            ["StorageInventory.History.Tests", "StorageInventory.IntegrationTests"]);   // Library and its tests are added by C4
        Assert.Equal(0, friendViolations.Count, string.Join("; ", friendViolations));
    }
}

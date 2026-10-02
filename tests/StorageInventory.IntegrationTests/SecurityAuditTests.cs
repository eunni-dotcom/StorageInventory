using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
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
    internal static readonly Lazy<Dictionary<string, string>> Sources = new(() =>
        Directory.EnumerateFiles(Path.Combine(TestEnvironment.RepoRoot, "src"), "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".xaml", StringComparison.Ordinal) || f.EndsWith(".csproj", StringComparison.Ordinal) || f.EndsWith(".manifest", StringComparison.Ordinal))
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToDictionary(f => Path.GetRelativePath(TestEnvironment.RepoRoot, f), File.ReadAllText));

    /// <summary>Files (relative to the repo) whose code matches the pattern, ignoring // comments.</summary>
    internal static List<string> FilesMatching(string pattern) => FilesMatching(pattern, Sources.Value);

    /// <summary>The same matcher over any set of sources, so a rule can be shown to reject a violating snippet.</summary>
    internal static List<string> FilesMatching(string pattern, IReadOnlyDictionary<string, string> sources)
    {
        var regex = new Regex(pattern, RegexOptions.Multiline);
        return sources
            .Where(kv => regex.IsMatch(string.Join("\n", kv.Value.Split('\n').Select(StripComment))))
            .Select(kv => kv.Key.Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    internal static string StripComment(string line)
    {
        var i = line.IndexOf("//", StringComparison.Ordinal);
        return i >= 0 && !line[..i].Contains('"') ? line[..i] : line;
    }

    internal static void OnlyIn(string what, string pattern, params string[] allowed)
    {
        var found = FilesMatching(pattern);
        Assert.SequenceEqual(allowed.Order(StringComparer.Ordinal), found, $"{what} may only appear in the audited location(s)");
    }

    [Test]
    public static void File_creation_happens_only_in_ReportRun_and_LibraryStore()
    {
        // A-01 (v1.1): FileMode.CreateNew and Directory.CreateDirectory only in ReportRun.cs and LibraryStore.cs
        OnlyIn("FileMode.CreateNew", @"FileMode\.CreateNew", "src/StorageInventory.Core/Reports/ReportRun.cs", "src/StorageInventory.Library/LibraryStore.cs");
        // A-03 (v1.1): new FileStream( only in the report readers and writers, ReportRun.cs and LibraryStore.cs
        OnlyIn("new FileStream", @"new FileStream\(",
            "src/StorageInventory.Core/Reports/ReportCsvReader.cs", "src/StorageInventory.Core/Reports/ReportRun.cs", "src/StorageInventory.Core/Reports/ReportWriters.cs",
            "src/StorageInventory.Library/LibraryStore.cs");
        OnlyIn("Directory.CreateDirectory", @"Directory\.CreateDirectory\(", "src/StorageInventory.Core/Reports/ReportRun.cs", "src/StorageInventory.Library/LibraryStore.cs");
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

        // A-03 (v1.1): in LibraryStore.cs every construction is either a create-new (for writing), or FileMode.Open with FileAccess.Read
        var violations = LibraryStoreFileStreamViolations(Sources.Value);
        Assert.Equal(0, violations.Count, string.Join("; ", violations));
    }

    /// <summary>The A-03 rule for <c>LibraryStore.cs</c> as a function over sources, so a negative self-test can run it on a violating file.</summary>
    internal static List<string> LibraryStoreFileStreamViolations(IReadOnlyDictionary<string, string> sources)
    {
        var violations = new List<string>();
        foreach (var (file, raw) in sources.Where(kv => kv.Key.Replace('\\', '/').EndsWith("LibraryStore.cs", StringComparison.Ordinal)))
        {
            var code = string.Join("\n", raw.Split('\n').Select(StripComment));
            foreach (Match m in Regex.Matches(code, @"new FileStream\(([^;]*)\)"))
            {
                var args = m.Groups[1].Value;
                var createNew = args.Contains("FileMode.CreateNew", StringComparison.Ordinal);
                var readOnly = Regex.IsMatch(args, @"FileMode\.Open\b") && Regex.IsMatch(args, @"FileAccess\.Read\b");   // word boundaries: FileAccess.ReadWrite is not FileAccess.Read
                if (!createNew && !readOnly) violations.Add($"{file}: new FileStream({args.Trim()}) is neither a create-new nor FileMode.Open with FileAccess.Read");
                if (createNew && !Regex.IsMatch(args, @"FileAccess\.Write\b")) violations.Add($"{file}: a create-new without FileAccess.Write: {args.Trim()}");
            }
        }
        return violations;
    }

    [Test]
    public static void Deletes_and_renames_happen_only_in_ReportRun_and_the_set_aside_and_nothing_deletes_folders()
    {
        // A-04 (v1.1): File.Delete only in ReportRun.cs (the Library never deletes files); File.Move only in ReportRun.cs (the
        // workbook) and LibraryStore.QuarantineSet, both with overwrite: false
        OnlyIn("File.Delete", @"File\.Delete\(", "src/StorageInventory.Core/Reports/ReportRun.cs");
        OnlyIn("File.Move", @"File\.Move\(", "src/StorageInventory.Core/Reports/ReportRun.cs", "src/StorageInventory.Library/LibraryStore.cs");
        Assert.Equal(0, FilesMatching(@"(Directory\.(Delete|Move)|\.Delete\(\s*(true|recursive))").Count, "no folder deletion or moving anywhere");
        Assert.Contains("overwrite: false", Sources.Value[Path.Combine("src", "StorageInventory.Core", "Reports", "ReportRun.cs")]);
        var store = Sources.Value[Path.Combine("src", "StorageInventory.Library", "LibraryStore.cs")];
        var moves = Regex.Matches(string.Join("\n", store.Split('\n').Select(StripComment)), @"File\.Move\(([^;]*)\)");
        Assert.Equal(1, moves.Count, "one rename in LibraryStore: the set-aside");
        Assert.Contains("overwrite: false", moves[0].Groups[1].Value);
        Assert.Contains("stem + suffix", moves[0].Groups[1].Value);   // the destination is the quarantine stem (library.damaged-...) plus the member's suffix
        Assert.Contains("LibraryNames.QuarantinePrefix", store);
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

    /// <summary>Every first-party assembly that can hold a P/Invoke: Core, History, the Library and the App. The
    /// <c>AUDIT_WITHOUT_APP</c> symbol exists only for <c>tests/mutation/AuditHarness</c>, which re-runs these audit rules on a
    /// machine that cannot load the WPF App assembly (Linux); the real test project never defines it.</summary>
    private static readonly Assembly[] FirstPartyAssemblies =
    [
        typeof(StorageScanResult).Assembly,
        typeof(StorageInventory.History.Identity.IdentityMatching).Assembly,
        typeof(StorageInventory.Library.LibraryNames).Assembly,
#if !AUDIT_WITHOUT_APP
        typeof(StorageInventory.App.App).Assembly,
#endif
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

    /// <summary>The call-shape text rules of A-08 over a set of sources: <c>CreateFile</c> is called only with desired access 0 and
    /// <c>GetFileInformationByHandleEx</c> only with <c>FileIdInfo</c>, whose enum has no other member and whose name appears in
    /// exactly three kinds of place: its declaration, the P/Invoke's parameter and the <c>.FileIdInfo</c> argument. That closes
    /// the shapes that SPELL the type: casts, <c>default(...)</c>, <c>Enum.ToObject(typeof(...))</c> and <c>Unsafe.As</c>. It
    /// cannot see a call that never names the type, such as one made through reflection, which gets a value from the
    /// method's own parameter type. That is closed by the separate reflection rules (<see cref="NativeReflectionPattern"/> over
    /// the source, <see cref="ReflectiveMemberViolations"/> over the compiled assemblies), not by this one (C3-M03).</summary>
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
            foreach (Match use in Regex.Matches(code, @"\bFileInfoByHandleClass\b"))
            {
                var before = code[Math.Max(0, use.Index - 5)..use.Index];
                var after = code[(use.Index + use.Length)..];
                var allowed = before == "enum " || after.StartsWith(".FileIdInfo", StringComparison.Ordinal) || Regex.IsMatch(after, @"^\s+informationClass\b");
                if (!allowed) violations.Add($"{file}: FileInfoByHandleClass is used other than in its declaration, as the P/Invoke parameter or as '.FileIdInfo' (near '{code[use.Index..Math.Min(code.Length, use.Index + 40)].Replace('\n', ' ')}')");
            }
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

        // GetFileInformationByHandleEx is asked for FileIdInfo only: by type (a one-member enum whose name the text rule allows in
        // exactly three kinds of place) and by its single call site
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

        // the text rules: wrong desired access, another information class, a cast, a second enum member, and the other ways to
        // conjure an enum value without writing its member (default, Enum.ToObject, Unsafe.As)
        var sources = new Dictionary<string, string>
        {
            ["src/Rogue/Access.cs"] = "var h = CreateFile(path, GENERIC_READ, share, IntPtr.Zero, OpenExisting, flags, IntPtr.Zero);",
            ["src/Rogue/Class.cs"] = "GetFileInformationByHandleEx(handle, FileInfoByHandleClass.FileStandardInfo, out info, size);",
            ["src/Rogue/Cast.cs"] = "var c = (FileInfoByHandleClass)5;",
            ["src/Rogue/Enum.cs"] = "private enum FileInfoByHandleClass { FileIdInfo = 18, FileBasicInfo = 0 }",
            ["src/Rogue/Default.cs"] = "var c = default(FileInfoByHandleClass); GetFileInformationByHandleEx(handle, c, out info, size);",
            ["src/Rogue/ToObject.cs"] = "var c = Enum.ToObject(typeof(FileInfoByHandleClass), 5);",
            ["src/Rogue/As.cs"] = "var c = Unsafe.As<int, FileInfoByHandleClass>(ref raw);",
        };
        var shape = NativeCallShapeViolations(sources);
        foreach (var file in new[] { "Access.cs", "Class.cs", "Cast.cs", "Enum.cs", "Default.cs", "ToObject.cs", "As.cs" })
        {
            Assert.True(shape.Any(v => v.Contains(file, StringComparison.Ordinal)), $"the call-shape rule misses the {file} violation: {string.Join("; ", shape)}");
        }
        var clean = new Dictionary<string, string>
        {
            ["src/Fine.cs"] = "var h = CreateFile(path, 0, share, IntPtr.Zero, OpenExisting, flags, IntPtr.Zero); GetFileInformationByHandleEx(handle, FileInfoByHandleClass.FileIdInfo, out info, size);",
            ["src/Declaration.cs"] = "private enum FileInfoByHandleClass { FileIdInfo = 18 } private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, FileInfoByHandleClass informationClass, out FileIdInfoNative information, uint bufferSize);",
        };
        Assert.Equal(0, NativeCallShapeViolations(clean).Count, "the permitted shapes are accepted");

        // the binding-outside-NativeMethods rule
        var declarations = new Dictionary<string, string> { ["src/StorageInventory.Core/Rogue.cs"] = "[DllImport(\"kernel32.dll\")] static extern bool DeleteFileW(string p);" };
        Assert.SequenceEqual(["src/StorageInventory.Core/Rogue.cs"], FilesMatching(@"\[DllImport|\[LibraryImport|\bextern\s+\w", declarations));
    }

    /// <summary>
    /// A-08, C3-M03. Shipped code never uses reflection at all (nothing under <c>src</c> does today), so it is banned outright: a
    /// reflective call into <c>NativeMethods</c> would reach <c>GetFileInformationByHandleEx</c> with a class value taken from
    /// the method's own parameter type, and so never write the name <c>FileInfoByHandleClass</c> that the call-shape rule keys on.
    /// The pattern lists the ways to obtain or invoke a method by reflection or a delegate, over the text of <c>src</c>.
    /// </summary>
    private const string NativeReflectionPattern =
        @"System\s*\.\s*Reflection|\bMethodInfo\b|\bMethodBase\b|\bMemberInfo\b|\.\s*GetMethods?\s*\(|\.\s*GetMembers?\s*\(|\.\s*GetRuntimeMethods?\s*\(|\.\s*InvokeMember\s*\(|\bDynamicInvoke\s*\(|\bCreateDelegate\s*\(|\bExpression\s*\.\s*(Call|Lambda)\b|Linq\s*\.\s*Expressions|\bEnum\s*\.\s*ToObject\s*\(|typeof\s*\(\s*NativeMethods\s*\)";

    /// <summary>The members that call or look up a method by reflection or conjure an enum value from a number, as "Type.Member".
    /// Spelling does not matter at this level: whichever way the source is written the compiled call names one of these.</summary>
    private static bool IsReflectiveMember(string type, string member) => type switch
    {
        "System.Reflection.MethodBase" or "System.Reflection.MethodInfo" or "System.Reflection.MemberInfo" or "System.Reflection.TypeInfo"
            or "System.Reflection.RuntimeReflectionExtensions" => true,
        "System.Type" => member.StartsWith("GetMethod", StringComparison.Ordinal) || member.StartsWith("GetMember", StringComparison.Ordinal)
            || member is "InvokeMember" or "GetRuntimeMethod",
        "System.Delegate" => member is "DynamicInvoke" or "CreateDelegate",
        "System.Enum" => member == "ToObject",
        "System.Runtime.InteropServices.Marshal" => member is "GetDelegateForFunctionPointer" or "GetFunctionPointerForDelegate",
        _ => false,
    };

    /// <summary>
    /// The compiled counterpart of <see cref="NativeReflectionPattern"/>: every member reference in the assembly's metadata
    /// that <see cref="IsReflectiveMember"/> names, as "Type.Member". It reads the assembly's own metadata
    /// (<c>System.Reflection.Metadata</c>, as A-25 does for leases), so a call spelled in a way the text rule's expressions do not
    /// match is still found. It does not look inside method bodies, so it says where the assembly refers to a member, not from where.
    /// </summary>
    private static List<string> ReflectiveMemberViolations(Assembly assembly)
    {
        using var stream = File.OpenRead(assembly.Location);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var handle in reader.MemberReferences)
        {
            var reference = reader.GetMemberReference(handle);
            if (reference.Parent.Kind != HandleKind.TypeReference) continue;
            var parent = reader.GetTypeReference((TypeReferenceHandle)reference.Parent);
            var type = $"{reader.GetString(parent.Namespace)}.{reader.GetString(parent.Name)}";
            var member = reader.GetString(reference.Name);
            if (IsReflectiveMember(type, member)) found.Add($"{type}.{member}");
        }
        return [.. found];
    }

    [Test]
    public static void Shipped_code_makes_no_reflective_call_so_the_FileIdInfo_only_rule_cannot_be_bypassed()
    {
        // The source: no file under src names any way to find or invoke a method by reflection (C3-M03)
        var textHits = FilesMatching(NativeReflectionPattern);
        Assert.Equal(0, textHits.Count, "reflection in src: " + string.Join(", ", textHits));

        // The compiled assemblies that can reach NativeMethods (it is internal, and only Core itself and History see it): no member
        // reference to any of them, whatever the source looked like. The App is left to the text rule above: its generated WPF/XAML
        // code legitimately calls Delegate.CreateDelegate, and A-13 keeps it out of Core's internals.
        foreach (var assembly in new[] { typeof(StorageScanResult).Assembly, typeof(StorageInventory.History.Identity.IdentityMatching).Assembly })
        {
            var violations = ReflectiveMemberViolations(assembly);
            Assert.Equal(0, violations.Count, $"{assembly.GetName().Name} refers to reflective members: {string.Join(", ", violations)}");
        }
    }

    [Test]
    public static void The_reflection_rules_reject_the_bypass_the_review_demonstrated_and_its_variants()
    {
        // Negative self-tests (A-21) for C3-M03. The first snippet is the reviewer's mutant C7, whose own regular expressions found nothing.
        var bypasses = new Dictionary<string, string>
        {
            ["src/Rogue/C7.cs"] = "typeof(NativeMethods).GetMethod(\"GetFileInformationByHandleEx\", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object?[] { h, Enum.ToObject(m.GetParameters()[1].ParameterType, 5), null, 24u });",
            ["src/Rogue/Typeof.cs"] = "var t = typeof( NativeMethods );",
            ["src/Rogue/Members.cs"] = "var all = someType.GetMethods(BindingFlags.Static);",
            ["src/Rogue/Runtime.cs"] = "var m = RuntimeReflectionExtensions.GetRuntimeMethod(t, \"x\", types);",
            ["src/Rogue/Delegate.cs"] = "var d = Delegate.CreateDelegate(typeof(Func<int>), m); d.DynamicInvoke();",
            ["src/Rogue/Invoke.cs"] = "type.InvokeMember(\"GetFileInformationByHandleEx\", flags, null, null, args);",
            ["src/Rogue/Enum.cs"] = "var c = Enum.ToObject(someType, 5);",
            ["src/Rogue/Expression.cs"] = "var e = Expression.Call(method, args);",
            ["src/Rogue/Using.cs"] = "using System.Reflection;",
            ["src/Rogue/Spaced.cs"] = "var m = typeof (NativeMethods). GetMethod (\"GetFileInformationByHandleEx\", flags); var v = Enum . ToObject (t, 5);",
        };
        var hits = FilesMatching(NativeReflectionPattern, bypasses);
        foreach (var file in bypasses.Keys) Assert.True(hits.Contains(file), $"the reflection text rule misses {file}: {string.Join(", ", hits)}");

        // comments mentioning reflection are not code, and a delegate's own Invoke is not reflection
        var fine = new Dictionary<string, string>
        {
            ["src/Fine/Comment.cs"] = "// do not use MethodInfo or GetMethod( here\nvar x = 1;",
            ["src/Fine/Callback.cs"] = "onRow?.Invoke(rows); callback.Invoke(1);",
        };
        Assert.Equal(0, FilesMatching(NativeReflectionPattern, fine).Count, "legitimate code is accepted");

        // The compiled rule, over a real assembly that does it: this test assembly calls Type.GetMethod and MethodBase.Invoke
        // (here and in RogueReflectionFixture, which makes exactly the call of mutant C7 and is never invoked)
        var compiled = ReflectiveMemberViolations(typeof(SecurityAuditTests).Assembly);
        foreach (var member in new[] { "System.Type.GetMethod", "System.Reflection.MethodBase.Invoke", "System.Enum.ToObject" })
        {
            Assert.True(compiled.Contains(member), $"the compiled rule misses {member}: {string.Join(", ", compiled)}");
        }
        Assert.True(IsReflectiveMember("System.Delegate", "DynamicInvoke") && IsReflectiveMember("System.Type", "GetMethods") && !IsReflectiveMember("System.Type", "GetTypeFromHandle"),
            "the member list names the delegate and lookup routes, and not a plain typeof");
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

        // and neither project's source or project file carries a package reference or SQLite (A-12: SQLite is the Library's alone;
        // the exact approved set is A-11, in LibrarySecurityAuditTests)
        var packageFree = FilesMatching(@"<PackageReference|SQLitePCL|Microsoft\.Data\.Sqlite",
            Sources.Value.Where(kv => kv.Key.Replace('\\', '/').StartsWith("src/StorageInventory.Core/", StringComparison.Ordinal) || kv.Key.Replace('\\', '/').StartsWith("src/StorageInventory.History/", StringComparison.Ordinal))
                .ToDictionary(kv => kv.Key, kv => kv.Value));
        Assert.Equal(0, packageFree.Count, "Core and History stay package-free: " + string.Join(", ", packageFree));
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
        var friendViolations = FriendViolations("Core", visibleTo, approved, approved);   // A-13 from C4 on: exactly History, Library and the four test assemblies
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
            ["StorageInventory.History.Tests", "StorageInventory.IntegrationTests", "StorageInventory.Library", "StorageInventory.Library.Tests"]);   // C4 adds the Library (which applies the path rules) and its tests
        Assert.Equal(0, friendViolations.Count, string.Join("; ", friendViolations));
    }
}

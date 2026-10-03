using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using StorageInventory.Core;
using StorageInventory.Testing;

namespace StorageInventory.IntegrationTests;

/// <summary>
/// TEST-A1 for the v1.1 C4 rules of §16.4 (A-05 to A-07, A-09 to A-12, A-14, A-15, A-16, A-18, A-20, A-22 and A-25 parts (a) and
/// (c)); the older rules are in <see cref="SecurityAuditTests"/>, updated there. Every rule is replaced by an equally narrow exact
/// rule and has a negative self-test (A-21, SEC-30): a violating snippet or fixture that the rule must reject. A-23, A-24 and A-26
/// need real SQLite files and run in the Library test suite (LifecycleTests, QueryPlanTests).
/// </summary>
public static class LibrarySecurityAuditTests
{
    private static Dictionary<string, string> Library => SecurityAuditTests.Sources.Value
        .Where(kv => kv.Key.Replace('\\', '/').StartsWith("src/StorageInventory.Library/", StringComparison.Ordinal) && kv.Key.EndsWith(".cs", StringComparison.Ordinal))
        .ToDictionary(kv => kv.Key, kv => kv.Value);

    private static bool IsSqlFile(string file) => file.Replace('\\', '/').StartsWith("src/StorageInventory.Library/Sql/", StringComparison.Ordinal);

    private static string Code(string raw) => string.Join("\n", raw.Split('\n').Select(SecurityAuditTests.StripComment));

    // ================================================================ A-05: SQL is constant and parameterised

    /// <summary>SQL-looking text in a string literal (with the keywords in capitals, as the constants are written), outside Sql/*.cs.</summary>
    private const string SqlLiteralPattern =
        @"""[^""\n]*(\bSELECT\s|\bINSERT\s+INTO\b|\bUPDATE\s+\w+\s+SET\b|\bDELETE\s+FROM\b|\bCREATE\s+(TABLE|INDEX|UNIQUE|TRIGGER|VIEW)\b|\bPRAGMA\s|\bBEGIN\s+IMMEDIATE|\bCOMMIT\b|\bROLLBACK\b|\bATTACH\b|\bDETACH\b|\bVACUUM\b|\bDROP\s|\bEXPLAIN\b)";

    /// <summary>The A-05 text rules over a set of Library sources: SQL text only in Sql/*.cs; command text only from a constant; every
    /// SQL argument a constant; and the forbidden statements nowhere.</summary>
    internal static List<string> SqlViolations(IReadOnlyDictionary<string, string> sources)
    {
        var violations = new List<string>();
        foreach (var (file, raw) in sources)
        {
            var code = Code(raw);
            var sqlFile = IsSqlFile(file);
            if (!sqlFile)
            {
                foreach (Match m in Regex.Matches(code, SqlLiteralPattern)) violations.Add($"{file}: SQL text outside Sql/*.cs: {m.Value.Trim()}");
                foreach (Match m in Regex.Matches(code, @"CommandText\s*=\s*([^;]*);"))
                {
                    var value = m.Groups[1].Value.Trim();
                    if (!Regex.IsMatch(value, @"^(sql|setSql|getSql|[A-Za-z]+Sql.[A-Za-z0-9]+)$")) violations.Add($"{file}: command text assigned from '{value}' (must be a Sql constant or a forwarded SQL parameter)");
                }
                // a pragma helper forwards its two SQL parameters, so each call names two OpenSql constants
                foreach (Match m in Regex.Matches(code, @"\bSetAndReadBack\(([^;]*)\)\s*;"))
                {
                    if (Regex.Matches(m.Groups[1].Value, @"\bOpenSql\.[A-Za-z]+").Count != 2 && !Regex.IsMatch(m.Groups[1].Value, @"^\s*SqliteConnection connection, string setSql")) violations.Add($"{file}: SetAndReadBack({m.Groups[1].Value.Trim()}) does not pass two OpenSql constants");
                }
                // the SQL argument of every call that runs a statement is a Sql constant (or the forwarding parameter 'sql')
                foreach (var (call, index) in new (string, int)[] { ("Prepare", 0), ("Scalar", 0), ("Rows", 0), ("Query", 0), ("Long", 0), ("PragmaText", 0) })
                {
                    foreach (var callArguments in CallArguments(code, $".{call}("))
                    {
                        var arguments = SplitTopLevel(callArguments);
                        var offset = arguments.Count > 0 && arguments[0].Trim() == "lease" ? 1 : 0;   // the writer's methods take the lease first
                        if (arguments.Count <= index + offset) continue;
                        var argument = arguments[index + offset].Trim();
                        if (!Regex.IsMatch(argument, @"^(sql|[A-Za-z]+Sql.[A-Za-z0-9]+)$")) violations.Add($"{file}: .{call}({argument}...) does not pass a Sql constant");
                    }
                }
            }
            foreach (Match m in Regex.Matches(code, @"\b(ATTACH|DETACH|VACUUM|load_extension|writable_schema)\b", RegexOptions.IgnoreCase))
            {
                violations.Add($"{file}: forbidden statement or function '{m.Value}'");
            }
            if (Regex.IsMatch(code, @"\bDROP\s+(TABLE|INDEX|VIEW|TRIGGER)\b")) violations.Add($"{file}: DROP");
            foreach (Match m in Regex.Matches(code, @"journal_mode\s*=\s*(\w+)", RegexOptions.IgnoreCase))
            {
                if (!m.Groups[1].Value.Equals("TRUNCATE", StringComparison.Ordinal)) violations.Add($"{file}: journal_mode set to {m.Groups[1].Value}");
            }
            if (Regex.IsMatch(code, @"ORDER\s+BY[^;""]*\b(utf16|name_utf16)\b", RegexOptions.IgnoreCase)) violations.Add($"{file}: ORDER BY on a name BLOB (A-05)");
            // the only DELETE statements are T-DELETE's, each constrained by snapshot_id
            foreach (Match m in Regex.Matches(code, @"DELETE\s+FROM\s+\w+([^""]*)""", RegexOptions.IgnoreCase))
            {
                if (!file.Replace('\\', '/').EndsWith("Sql/DeleteSql.cs", StringComparison.Ordinal)) violations.Add($"{file}: a DELETE statement outside DeleteSql.cs");
                else if (!Regex.IsMatch(m.Groups[1].Value, @"WHERE\s+snapshot_id\s*=\s*\$snapshot_id")) violations.Add($"{file}: a DELETE not constrained by snapshot_id: {m.Value.Trim()}");
            }
            if (Regex.IsMatch(code, @"max_page_count") && !file.Replace('\\', '/').EndsWith("Sql/FaultSql.cs", StringComparison.Ordinal)) violations.Add($"{file}: max_page_count outside the fault-injection constants");
        }
        return violations;
    }

    /// <summary>The argument text of every call that starts with <paramref name="opening"/> (for example ".Prepare("), up to its matching
    /// closing parenthesis.</summary>
    private static IEnumerable<string> CallArguments(string code, string opening)
    {
        for (var at = code.IndexOf(opening, StringComparison.Ordinal); at >= 0; at = code.IndexOf(opening, at + 1, StringComparison.Ordinal))
        {
            var start = at + opening.Length;
            int depth = 1, i = start;
            var quoted = false;
            for (; i < code.Length && depth > 0; i++)
            {
                if (code[i] == '"') quoted = !quoted;
                if (quoted) continue;
                if (code[i] == '(') depth++;
                else if (code[i] == ')') depth--;
            }
            yield return code[start..Math.Max(start, i - 1)];
        }
    }

    private static List<string> SplitTopLevel(string text)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '"') quoted = !quoted;
            if (quoted) continue;
            if (text[i] is '(' or '[' or '{') depth++;
            else if (text[i] is ')' or ']' or '}') depth--;
            else if (text[i] == ',' && depth == 0) { parts.Add(text[start..i]); start = i + 1; }
        }
        parts.Add(text[start..]);
        return parts;
    }

    [Test]
    public static void A_05_sql_is_constant_text_in_Sql_files_and_every_value_is_a_bound_parameter()
    {
        var sources = Library;
        Assert.True(sources.Keys.Count(IsSqlFile) >= 5, "the Sql constant files were found");
        var violations = SqlViolations(sources);
        Assert.Equal(0, violations.Count, string.Join(Environment.NewLine, violations));

        // the constants themselves: reflection over every Sql class of the Library assembly
        var constants = SqlConstants().ToList();
        Assert.True(constants.Count > 60, $"{constants.Count} SQL constants were found");
        foreach (var (name, text) in constants)
        {
            Assert.False(Regex.IsMatch(text, @"\b(ATTACH|DETACH|VACUUM|load_extension|writable_schema|DROP)\b", RegexOptions.IgnoreCase), name + " contains a forbidden statement");
            Assert.False(Regex.IsMatch(text, @"'[^']*'") && !name.StartsWith("SchemaSql.", StringComparison.Ordinal), name + " embeds a literal string value (values are bound parameters)");
        }
    }

    internal static IEnumerable<(string Name, string Text)> SqlConstants() => typeof(StorageInventory.Library.LibraryNames).Assembly.GetTypes()
        .Where(t => t.Name.EndsWith("Sql", StringComparison.Ordinal) && t.IsAbstract && t.IsSealed && t.Namespace == "StorageInventory.Library")
        .SelectMany(t => t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => ($"{t.Name}.{f.Name}", (string)f.GetRawConstantValue()!)));

    [Test]
    public static void A_05_rejects_violating_snippets()
    {
        var rogue = new Dictionary<string, string>
        {
            ["src/StorageInventory.Library/Rogue1.cs"] = "command.CommandText = \"SELECT count(*) FROM snapshot\";",
            ["src/StorageInventory.Library/Rogue2.cs"] = "command.CommandText = $\"SELECT * FROM {table}\";",
            ["src/StorageInventory.Library/Rogue3.cs"] = "writer.Prepare(lease, \"x\" + name);",
            ["src/StorageInventory.Library/Rogue4.cs"] = "var text = \"ATTACH DATABASE 'x' AS y\";",
            ["src/StorageInventory.Library/Rogue5.cs"] = "var text = \"PRAGMA journal_mode = WAL\";",
            ["src/StorageInventory.Library/Rogue6.cs"] = "reader.Query(userText);",
            ["src/StorageInventory.Library/Sql/Rogue7.cs"] = "internal const string A = \"SELECT x FROM name ORDER BY utf16\";",
            ["src/StorageInventory.Library/Sql/DeleteSql.cs"] = "internal const string A = \"DELETE FROM file_obs WHERE size_bytes = 0\";",
            ["src/StorageInventory.Library/Sql/Rogue8.cs"] = "internal const string A = \"DELETE FROM file_obs WHERE snapshot_id = $snapshot_id\"; internal const string B = \"VACUUM\";",
            ["src/StorageInventory.Library/Rogue9.cs"] = "var text = \"PRAGMA max_page_count = 5\";",
            ["src/StorageInventory.Library/Rogue10.cs"] = "SetAndReadBack(_connection, userSet, userGet, \"x\", \"y\");",
        };
        var violations = SqlViolations(rogue);
        foreach (var file in new[] { "Rogue1", "Rogue2", "Rogue3", "Rogue4", "Rogue5", "Rogue6", "Rogue7", "DeleteSql", "Rogue8", "Rogue9", "Rogue10" })
        {
            Assert.True(violations.Any(v => v.Contains(file + ".cs", StringComparison.Ordinal)), $"the SQL rule misses {file}: {string.Join(" | ", violations)}");
        }
        var fine = new Dictionary<string, string>
        {
            ["src/StorageInventory.Library/Fine.cs"] = "// SELECT in a comment is not code\ncommand.CommandText = sql; writer.Prepare(lease, ImportSql.InsertName, \"$utf16\"); reader.Long(ReadSql.CountFileRows, (\"$snapshot_id\", 1));",
            ["src/StorageInventory.Library/Sql/DeleteSql.cs"] = "internal const string A = \"DELETE FROM x WHERE snapshot_id = $snapshot_id\";",
        };
        Assert.Equal(0, SqlViolations(fine).Count, string.Join(" | ", SqlViolations(fine)));
    }

    // ================================================================ A-06, A-07, A-10: connections, extensions, the provider

    internal static List<string> ConnectionViolations(IReadOnlyDictionary<string, string> sources)
    {
        var violations = new List<string>();
        foreach (var (file, raw) in sources)
        {
            var code = Code(raw);
            var isDatabase = file.Replace('\\', '/').EndsWith("StorageInventory.Library/LibraryDatabase.cs", StringComparison.Ordinal);
            if (!isDatabase && Regex.IsMatch(code, @"new\s+SqliteConnection\s*\(")) violations.Add($"{file}: SqliteConnection constructed outside LibraryDatabase.cs");
            if (Regex.IsMatch(code, @"ReadWriteCreate|SqliteOpenMode\.Memory|SqliteCacheMode\.Shared|Mode\s*=\s*Memory|Cache\s*=\s*Shared|:memory:|file::memory|\bMemory\s*=\s*true", RegexOptions.IgnoreCase))
            {
                violations.Add($"{file}: a create mode, an in-memory database or shared cache");
            }
            foreach (Match m in Regex.Matches(code, @"new\s+SqliteConnectionStringBuilder\s*\{([^}]*)\}"))
            {
                var text = m.Groups[1].Value;
                if (!Regex.IsMatch(text, @"Mode\s*=\s*SqliteOpenMode\.(ReadWrite|ReadOnly)\b")) violations.Add($"{file}: a connection string without an explicit Mode of ReadWrite or ReadOnly: {text.Trim()}");
                if (!Regex.IsMatch(text, @"Pooling\s*=\s*false")) violations.Add($"{file}: a connection string without Pooling = false: {text.Trim()}");
            }
            foreach (Match m in Regex.Matches(code, @"SqliteOpenMode\.(\w+)"))
            {
                if (m.Groups[1].Value is not ("ReadWrite" or "ReadOnly")) violations.Add($"{file}: SqliteOpenMode.{m.Groups[1].Value}");
            }
        }
        return violations;
    }

    [Test]
    public static void A_06_connections_are_built_only_in_LibraryDatabase_with_an_explicit_Mode_and_no_pooling()
    {
        var violations = ConnectionViolations(Library);
        Assert.Equal(0, violations.Count, string.Join(Environment.NewLine, violations));
        var database = Code(Library.Single(kv => kv.Key.EndsWith("LibraryDatabase.cs", StringComparison.Ordinal)).Value);
        Assert.Equal(2, Regex.Matches(database, @"new\s+SqliteConnectionStringBuilder").Count, "two connection factories: the writer and the reader");
        Assert.Equal(1, Regex.Matches(database, @"SqliteOpenMode\.ReadWrite\b").Count, "the writer factory is the only ReadWrite");
        Assert.Equal(1, Regex.Matches(database, @"SqliteOpenMode\.ReadOnly\b").Count, "the reader factory is the only ReadOnly");
        Assert.True(Regex.IsMatch(database, @"OpenWriter[\s\S]*SqliteOpenMode\.ReadWrite[\s\S]*OpenReader[\s\S]*SqliteOpenMode\.ReadOnly"), "ReadWrite belongs to OpenWriter and ReadOnly to OpenReader");
    }

    [Test]
    public static void A_06_rejects_violating_snippets()
    {
        var rogue = new Dictionary<string, string>
        {
            ["src/StorageInventory.Library/R1.cs"] = "var c = new SqliteConnection(cs);",
            ["src/StorageInventory.Library/R2.cs"] = "var b = new SqliteConnectionStringBuilder { DataSource = p, Pooling = false };",
            ["src/StorageInventory.Library/R3.cs"] = "var b = new SqliteConnectionStringBuilder { DataSource = p, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false };",
            ["src/StorageInventory.Library/R4.cs"] = "var b = new SqliteConnectionStringBuilder { DataSource = p, Mode = SqliteOpenMode.ReadOnly };",
            ["src/StorageInventory.Library/R5.cs"] = "var b = new SqliteConnectionStringBuilder { DataSource = \":memory:\", Mode = SqliteOpenMode.Memory, Pooling = false };",
            ["src/StorageInventory.Library/R6.cs"] = "var b = new SqliteConnectionStringBuilder { DataSource = p, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Shared, Pooling = false };",
        };
        var violations = ConnectionViolations(rogue);
        foreach (var file in new[] { "R1", "R2", "R3", "R4", "R5", "R6" }) Assert.True(violations.Any(v => v.Contains(file + ".cs", StringComparison.Ordinal)), $"the connection rule misses {file}: {string.Join(" | ", violations)}");
        var fine = new Dictionary<string, string> { ["src/StorageInventory.Library/LibraryDatabase.cs"] = "var b = new SqliteConnectionStringBuilder { DataSource = f, Mode = SqliteOpenMode.ReadOnly, Pooling = false }; var c = new SqliteConnection(b.ConnectionString);" };
        Assert.Equal(0, ConnectionViolations(fine).Count);
    }

    [Test]
    public static void A_07_extensions_are_never_enabled_and_A_10_the_provider_is_set_once_without_a_native_resolver()
    {
        // A-07
        Assert.Equal(0, SecurityAuditTests.FilesMatching(@"EnableExtensions|LoadExtension|load_extension").Count, "extension loading never appears in src");
        var rogue = new Dictionary<string, string> { ["src/x.cs"] = "connection.EnableExtensions(true); connection.LoadExtension(\"x\");", ["src/y.cs"] = "var s = \"SELECT load_extension('x')\";" };
        Assert.Equal(2, SecurityAuditTests.FilesMatching(@"EnableExtensions|LoadExtension|load_extension", rogue).Count, "A-07 negative self-test");

        // A-10: no custom native resolver and no use of the executable's folder for native loading
        const string forbidden = @"\bNativeLibrary\b|SetDllImportResolver|AppContext\s*\.\s*BaseDirectory|\bBatteries(_V2)?\b|SQLitePCL\.Batteries";
        Assert.Equal(0, SecurityAuditTests.FilesMatching(forbidden).Count, "no resolver, no BaseDirectory, no Batteries initialiser in src");
        var rogueNative = new Dictionary<string, string>
        {
            ["src/a.cs"] = "NativeLibrary.SetDllImportResolver(asm, r);", ["src/b.cs"] = "var d = AppContext.BaseDirectory;", ["src/c.cs"] = "SQLitePCL.Batteries_V2.Init();",
            ["src/d.cs"] = "AssemblyLoadContext.Default.ResolvingUnmanagedDll += (a, n) => 0; SetDllImportResolver(x, y);",
        };
        Assert.Equal(4, SecurityAuditTests.FilesMatching(forbidden, rogueNative).Count, "A-10 negative self-test");

        // SetProvider exactly once, in LibraryDatabase.cs, and before any connection is constructed
        SecurityAuditTests.OnlyIn("SQLitePCL.raw.SetProvider", @"\braw\s*\.\s*SetProvider\s*\(", "src/StorageInventory.Library/LibraryDatabase.cs");
        var database = Code(Library.Single(kv => kv.Key.EndsWith("LibraryDatabase.cs", StringComparison.Ordinal)).Value);
        Assert.Equal(1, Regex.Matches(database, @"\braw\s*\.\s*SetProvider\s*\(").Count, "called exactly once");
        Assert.Contains("new SQLite3Provider_e_sqlite3()", database);
        foreach (var factory in new[] { "OpenWriter", "OpenReader" })
        {
            var start = database.IndexOf("static " + (factory == "OpenWriter" ? "WriterConnection" : "ReaderConnection") + " " + factory, StringComparison.Ordinal);
            Assert.True(start >= 0, factory + " found");
            var body = database[start..];
            Assert.True(body.IndexOf("EnsureProvider()", StringComparison.Ordinal) is var ensure && ensure >= 0 && ensure < body.IndexOf("new SqliteConnection(", StringComparison.Ordinal), $"{factory} sets the provider before it constructs a connection");
        }
        // the set-provider guard runs under a lock and a flag: exactly once for the process
        Assert.True(Regex.IsMatch(database, @"lock\s*\(ProviderLock\)[\s\S]*if\s*\(_providerSet\)\s*return;[\s\S]*SetProvider[\s\S]*_providerSet\s*=\s*true"), "guarded: exactly once");
    }

    // ================================================================ A-09: third-party natives

    [Test]
    public static void A_09_only_the_SQLitePCLRaw_provider_imports_a_native_module_and_only_e_sqlite3()
    {
        var directory = Path.GetDirectoryName(typeof(StorageInventory.Library.LibraryNames).Assembly.Location)!;
        var assemblies = new[] { "Microsoft.Data.Sqlite", "SQLitePCLRaw.core", "SQLitePCLRaw.provider.e_sqlite3", "SQLitePCLRaw.batteries_v2" };
        var imports = new List<string>();
        foreach (var name in assemblies)
        {
            var path = Path.Combine(directory, name + ".dll");
            if (!File.Exists(path)) { Assert.True(name == "SQLitePCLRaw.batteries_v2", $"{name}.dll is in the output"); continue; }
            imports.AddRange(NativeImports(path).Select(i => $"{name}|{i.Module}|{i.Entry}"));
        }
        Assert.False(File.Exists(Path.Combine(directory, "SQLitePCLRaw.batteries_v2.dll")), "the batteries assembly is not part of the approved set and is not in the output");
        var violations = NativeImportViolations(imports);
        Assert.Equal(0, violations.Count, string.Join("; ", violations));
        var provider = imports.Where(i => i.StartsWith("SQLitePCLRaw.provider.e_sqlite3|", StringComparison.Ordinal)).ToList();
        var distinct = provider.Select(p => p.Split('|')[2]).Distinct().Count();
        Console.WriteLine($"A-09: {provider.Count} P/Invoke declarations in SQLitePCLRaw.provider.e_sqlite3 (lib/net6.0-windows7.0), all into 'e_sqlite3', {distinct} distinct entry points");
        Assert.True(provider.Count > 100 && provider.All(p => p.Split('|')[1] == "e_sqlite3"), "every provider import targets the one module e_sqlite3");
        Assert.Equal(imports.Count, provider.Count, "no other approved assembly declares a native import");
        Assert.Equal(150, provider.Count, "the provider's P/Invoke declarations, as measured in C4 (the specification's 164 is a count of distinct sqlite3_ string tokens in the provider file, not of entry points: see docs/v1.1-c4-repair.md, section 5)");
        Assert.Equal(141, distinct, "distinct entry points");
    }

    /// <summary>Every P/Invoke declaration of an assembly, from its metadata (the ImplMap table): the module and the entry point.</summary>
    internal static List<(string Module, string Entry)> NativeImports(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
        var md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
        var list = new List<(string, string)>();
        foreach (var handle in md.MethodDefinitions)
        {
            var import = md.GetMethodDefinition(handle).GetImport();
            if (import.Module.IsNil) continue;
            list.Add((md.GetString(md.GetModuleReference(import.Module).Name), md.GetString(import.Name)));
        }
        return list;
    }

    /// <summary>A-09 as a function: a native import is allowed only in <c>SQLitePCLRaw.provider.e_sqlite3</c> and only to <c>e_sqlite3</c>.</summary>
    internal static List<string> NativeImportViolations(IEnumerable<string> imports) => imports
        .Select(i => i.Split('|'))
        .Where(p => !(p[0] == "SQLitePCLRaw.provider.e_sqlite3" && p[1] == "e_sqlite3"))
        .Select(p => $"{p[0]} imports {p[2]} from {p[1]}")
        .ToList();

    [Test]
    public static void A_09_rejects_a_stray_native_import()
    {
        Assert.Equal(0, NativeImportViolations(["SQLitePCLRaw.provider.e_sqlite3|e_sqlite3|sqlite3_open"]).Count);
        Assert.Equal(1, NativeImportViolations(["SQLitePCLRaw.provider.e_sqlite3|e_sqlite3|sqlite3_open", "SQLitePCLRaw.provider.e_sqlite3|winsqlite3|sqlite3_open"]).Count, "another module");
        Assert.Equal(1, NativeImportViolations(["SQLitePCLRaw.core|e_sqlite3|sqlite3_open"]).Count, "another assembly");
        Assert.Equal(1, NativeImportViolations(["Microsoft.Data.Sqlite|kernel32.dll|CreateFileW"]).Count, "a framework-style import in an approved assembly");
        Assert.Equal(1, NativeImportViolations(["SQLitePCLRaw.batteries_v2|e_sqlite3|sqlite3_open"]).Count, "the batteries assembly");
    }

    // ================================================================ A-11: package references are exactly the approved set

    private static readonly (string Id, string Version, string ContentHash)[] Approved =
    [
        ("Microsoft.Data.Sqlite.Core", "10.0.12", "2aL9eL5HQr0V4b8V909SD+jQAx6qnTTl7sT/v3D8dIvmr2UGdpZIQxqm1pj2Js3e4EYTvpw0oMH7DjapJJuPPw=="),
        ("SQLitePCLRaw.core", "2.1.12", "ETpNw9DY3ckWLgRRAeCHj+GKOuPi61aeczkXhgHexUvqoZBAYg8RYESE2J7O1M7+o6QbdSEZwrw9bfqztUVWXg=="),
        ("SQLitePCLRaw.lib.e_sqlite3", "2.1.12", "fWi8Dbknuhgg72fWinIdjXVaqO1hHL4YBBwVLnr7e1c9TAZwJ0QE38j9syW1hwx6HaqEVTwI+O07WPdZn8Rp0w=="),
        ("SQLitePCLRaw.provider.e_sqlite3", "2.1.12", "W3oH4XIfCzFrgUSDKHhN6N+dgzA5YHOR2VxX8GB6Qy7CyrJJgxPEG8NirgYWlPQC5P2jz2knSsexWu4tDUL33g=="),
    ];

    private static IEnumerable<string> Projects(params string[] roots) => roots.SelectMany(r => Directory.EnumerateFiles(Path.Combine(TestEnvironment.RepoRoot, r), "*.csproj", SearchOption.AllDirectories))
        .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    /// <summary>A-11 as a function over project files (path → XML) and the repository's other package-related files.</summary>
    internal static List<string> PackageReferenceViolations(IReadOnlyDictionary<string, string> projects)
    {
        var violations = new List<string>();
        var found = new List<(string Project, string Id, string Version, string? PrivateAssets)>();
        foreach (var (path, xml) in projects)
        {
            var document = XDocument.Parse(xml);
            var name = Path.GetFileName(path);
            foreach (var reference in document.Descendants("PackageReference"))
            {
                found.Add((name, (string?)reference.Attribute("Include") ?? "?", (string?)reference.Attribute("Version") ?? reference.Element("Version")?.Value ?? "?", (string?)reference.Attribute("PrivateAssets")));
            }
            foreach (var tag in new[] { "GlobalPackageReference", "PackageVersion", "PackageDownload" })
            {
                // PackageDownload is the runtime-pack mechanism of v1 (App.csproj), outside the lock files; nothing else may use it
                if (document.Descendants(tag).Any() && !(tag == "PackageDownload" && name == "StorageInventory.App.csproj")) violations.Add($"{name}: <{tag}>");
            }
            if (document.Descendants("Reference").Any(r => r.Attribute("HintPath") is not null || r.Element("HintPath") is not null)) violations.Add($"{name}: a <Reference HintPath=...>");
        }
        foreach (var (project, id, version, privateAssets) in found)
        {
            if (project != "StorageInventory.Library.csproj") violations.Add($"{project}: <PackageReference Include=\"{id}\"> outside StorageInventory.Library.csproj");
            var approved = Approved.FirstOrDefault(a => a.Id == id);
            if (approved.Id is null) violations.Add($"{project}: package {id} is not in the approved set");
            else if (approved.Version != version) violations.Add($"{project}: {id} {version} (approved: {approved.Version})");
            if (!Regex.IsMatch(version, @"^\d+\.\d+\.\d+$")) violations.Add($"{project}: {id} version '{version}' is not an exact version (no floating or range)");
            if (privateAssets != "compile") violations.Add($"{project}: {id} PrivateAssets is '{privateAssets}', must be 'compile'");
        }
        var library = found.Where(f => f.Project == "StorageInventory.Library.csproj").Select(f => (f.Id, f.Version)).OrderBy(f => f.Id, StringComparer.Ordinal).ToList();
        var expected = Approved.Select(a => (a.Id, a.Version)).OrderBy(a => a.Id, StringComparer.Ordinal).ToList();
        if (!library.SequenceEqual(expected)) violations.Add($"StorageInventory.Library.csproj references [{string.Join(", ", library.Select(l => l.Id + " " + l.Version))}], expected exactly [{string.Join(", ", expected.Select(l => l.Id + " " + l.Version))}]");
        return violations;
    }

    /// <summary>The lock-file half of A-11 over (path → JSON): the Library's direct packages are exactly the four with their pinned
    /// content hashes; no lock file anywhere names <c>System.Memory</c> or another package; the transitive sets are the four.</summary>
    internal static List<string> LockFileViolations(IReadOnlyDictionary<string, string> locks)
    {
        var violations = new List<string>();
        var ids = Approved.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, json) in locks)
        {
            using var document = JsonDocument.Parse(json);
            var project = Path.GetFileName(Path.GetDirectoryName(path)!);
            foreach (var target in document.RootElement.GetProperty("dependencies").EnumerateObject())
            {
                foreach (var dependency in target.Value.EnumerateObject())
                {
                    var type = dependency.Value.GetProperty("type").GetString();
                    if (type == "Project") continue;
                    if (dependency.Name.Contains("System.Memory", StringComparison.OrdinalIgnoreCase)) violations.Add($"{project}: System.Memory appears in the lock file (a stop condition, BLD-03)");
                    if (!ids.Contains(dependency.Name)) violations.Add($"{project}: lock file names {dependency.Name}, not in the approved set");
                    if (type == "Direct" && project != "StorageInventory.Library") violations.Add($"{project}: a Direct package ({dependency.Name}) outside the Library");
                    var approved = Approved.FirstOrDefault(a => string.Equals(a.Id, dependency.Name, StringComparison.OrdinalIgnoreCase));
                    if (approved.Id is not null && !target.Name.Contains('/', StringComparison.Ordinal))
                    {
                        if (dependency.Value.GetProperty("resolved").GetString() != approved.Version) violations.Add($"{project}: {dependency.Name} resolved to {dependency.Value.GetProperty("resolved").GetString()}");
                        if (dependency.Value.GetProperty("contentHash").GetString() != approved.ContentHash) violations.Add($"{project}: {dependency.Name} has content hash {dependency.Value.GetProperty("contentHash").GetString()}, pinned {approved.ContentHash}");
                    }
                }
            }
            var main = document.RootElement.GetProperty("dependencies").EnumerateObject().First(t => !t.Name.Contains('/', StringComparison.Ordinal)).Value;
            var packages = main.EnumerateObject().Where(d => d.Value.GetProperty("type").GetString() != "Project").Select(d => d.Name).Order(StringComparer.Ordinal).ToList();
            if (project == "StorageInventory.Library" && !packages.SequenceEqual(ids.Order(StringComparer.Ordinal))) violations.Add($"{project}: lock file packages are [{string.Join(", ", packages)}], expected exactly the four");
            if (packages.Count != 0 && packages.Count != 4) violations.Add($"{project}: lock file holds {packages.Count} packages (the four, or none)");
        }
        return violations;
    }

    [Test]
    public static void A_11_package_references_are_exactly_the_approved_set_pinned_by_the_lock_files()
    {
        var root = TestEnvironment.RepoRoot;
        var projects = Projects("src", "tests").ToDictionary(p => p, File.ReadAllText);
        Assert.True(projects.Count >= 9, $"{projects.Count} projects found");
        var violations = PackageReferenceViolations(projects);
        Assert.Equal(0, violations.Count, string.Join(Environment.NewLine, violations));

        foreach (var file in new[] { "Directory.Packages.props", "Directory.Build.targets", "global.json" })
        {
            Assert.False(File.Exists(Path.Combine(root, file)) && file == "Directory.Packages.props", file + " (central package management) is not used");
        }
        Assert.Equal(0, XDocument.Parse(File.ReadAllText(Path.Combine(root, "Directory.Build.props"))).Descendants().Count(e => e.Name.LocalName is "PackageReference" or "GlobalPackageReference" or "PackageVersion"), "Directory.Build.props references no package");

        // every project has a committed lock file, in sync with its project file (locked-mode restore proves it; here: presence and content)
        var locks = new Dictionary<string, string>();
        foreach (var project in projects.Keys)
        {
            var lockFile = Path.Combine(Path.GetDirectoryName(project)!, "packages.lock.json");
            Assert.True(File.Exists(lockFile), "a committed packages.lock.json beside " + Path.GetFileName(project));
            locks[lockFile] = File.ReadAllText(lockFile);
        }
        var lockViolations = LockFileViolations(locks);
        Assert.Equal(0, lockViolations.Count, string.Join(Environment.NewLine, lockViolations));
        Assert.False(locks.Values.Any(l => l.Contains("System.Memory", StringComparison.OrdinalIgnoreCase)), "System.Memory is absent from every lock file");

        // Core and History are package-free, in their lock files too
        foreach (var name in new[] { "StorageInventory.Core", "StorageInventory.History", "StorageInventory.Core.Tests", "StorageInventory.History.Tests" })
        {
            var path = locks.Keys.Single(k => Path.GetFileName(Path.GetDirectoryName(k)) == name);
            Assert.False(locks[path].Contains("SQLitePCLRaw", StringComparison.Ordinal) || locks[path].Contains("Microsoft.Data.Sqlite", StringComparison.Ordinal), name + " has no SQLite in its lock file");
        }

        // nuget.config: the source mapping names exactly the six IDs (two runtime packs, four packages), no wildcard
        var config = XDocument.Load(Path.Combine(root, "nuget.config"));
        var patterns = config.Descendants("package").Select(p => (string?)p.Attribute("pattern")).Order(StringComparer.Ordinal).ToList();
        string[] expectedPatterns = ["Microsoft.Data.Sqlite.Core", "Microsoft.NETCore.App.Runtime.win-x64", "Microsoft.WindowsDesktop.App.Runtime.win-x64", "SQLitePCLRaw.core", "SQLitePCLRaw.lib.e_sqlite3", "SQLitePCLRaw.provider.e_sqlite3"];
        Assert.SequenceEqual(expectedPatterns, patterns!, "nuget.config source mapping");
        Assert.False(patterns.Any(p => p!.Contains('*', StringComparison.Ordinal)), "no wildcard pattern");
        Assert.Equal(1, config.Descendants("add").Count(a => (string?)a.Attribute("key") == "nuget.org" && ((string?)a.Attribute("value") ?? "").Contains("api.nuget.org", StringComparison.Ordinal)), "the one source is nuget.org");

        // the build policy (BLD-02, BLD-17): lock files, win-x64 restore, locked mode in CI
        var props = File.ReadAllText(Path.Combine(root, "Directory.Build.props"));
        Assert.Contains("<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>", props);
        Assert.Contains("<RuntimeIdentifiers>win-x64</RuntimeIdentifiers>", props);
        Assert.Contains("RestoreLockedMode", props);

        // the native binary the package carries (§5.5): its size and hash, from the restored package, are pinned
        var native = Path.Combine(root, "packages", "sqlitepclraw.lib.e_sqlite3", "2.1.12", "runtimes", "win-x64", "native", "e_sqlite3.dll");
        Assert.True(File.Exists(native), "the package's win-x64 e_sqlite3.dll is restored");
        Assert.Equal(1_978_880L, new FileInfo(native).Length, "e_sqlite3.dll is 1,978,880 bytes (§5.5)");
        Assert.Equal("B7385D722C83FB52142A00477A726723745916D22A555711EE89834C1111FB2E", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(native))), "e_sqlite3.dll SHA-256");
    }

    [Test]
    public static void A_11_rejects_violating_inputs()
    {
        string Project(string packages) => $"<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>{packages}</ItemGroup></Project>";
        string Reference(string id, string version, string assets = "compile") => $"<PackageReference Include=\"{id}\" Version=\"{version}\" PrivateAssets=\"{assets}\" />";
        var good = string.Concat(Approved.Select(a => Reference(a.Id, a.Version)));
        var libraryPath = @"x\src\StorageInventory.Library\StorageInventory.Library.csproj";

        Assert.Equal(0, PackageReferenceViolations(new Dictionary<string, string> { [libraryPath] = Project(good) }).Count, "the approved set is accepted");
        Assert.True(PackageReferenceViolations(new Dictionary<string, string> { [libraryPath] = Project(good + Reference("SQLitePCLRaw.bundle_e_sqlite3", "2.1.12")) }).Any(v => v.Contains("not in the approved set")), "a fifth package (the bundle)");
        Assert.True(PackageReferenceViolations(new Dictionary<string, string> { [libraryPath] = Project(good + Reference("System.Memory", "4.5.3")) }).Any(v => v.Contains("System.Memory")), "System.Memory");
        Assert.True(PackageReferenceViolations(new Dictionary<string, string> { [libraryPath] = Project(good.Replace("10.0.12", "10.0.*")) }).Any(v => v.Contains("exact")), "a floating version");
        Assert.True(PackageReferenceViolations(new Dictionary<string, string> { [libraryPath] = Project(good.Replace("2.1.12", "2.1.13")) }).Any(v => v.Contains("approved: 2.1.12")), "a newer version");
        Assert.True(PackageReferenceViolations(new Dictionary<string, string> { [libraryPath] = Project(good.Replace("PrivateAssets=\"compile\"", "PrivateAssets=\"all\"")) }).Any(v => v.Contains("PrivateAssets")), "wrong PrivateAssets");
        Assert.True(PackageReferenceViolations(new Dictionary<string, string> { [libraryPath] = Project(good.Replace(" PrivateAssets=\"compile\"", "")) }).Any(v => v.Contains("PrivateAssets")), "missing PrivateAssets");
        Assert.True(PackageReferenceViolations(new Dictionary<string, string> { [libraryPath] = Project(good), [@"x\src\StorageInventory.App\StorageInventory.App.csproj"] = Project(Reference("Microsoft.Data.Sqlite.Core", "10.0.12")) }).Any(v => v.Contains("outside StorageInventory.Library.csproj")), "a package in another project");
        Assert.True(PackageReferenceViolations(new Dictionary<string, string> { [libraryPath] = Project(good + "<GlobalPackageReference Include=\"x\" Version=\"1.0.0\" />") }).Any(v => v.Contains("GlobalPackageReference")), "a global reference");
        Assert.True(PackageReferenceViolations(new Dictionary<string, string> { [libraryPath] = Project(good + "<Reference Include=\"x\"><HintPath>y.dll</HintPath></Reference>") }).Any(v => v.Contains("HintPath")), "a HintPath reference");
        Assert.True(PackageReferenceViolations(new Dictionary<string, string> { [libraryPath] = Project(Reference("SQLitePCLRaw.core", "2.1.12")) }).Any(v => v.Contains("expected exactly")), "a missing package");

        // lock files
        string Lock(string extra = "", string hash = "2aL9eL5HQr0V4b8V909SD+jQAx6qnTTl7sT/v3D8dIvmr2UGdpZIQxqm1pj2Js3e4EYTvpw0oMH7DjapJJuPPw==") =>
            "{\"version\":1,\"dependencies\":{\"net10.0-windows7.0\":{" + string.Join(",", Approved.Select(a => $"\"{a.Id}\":{{\"type\":\"Direct\",\"requested\":\"[{a.Version}, )\",\"resolved\":\"{a.Version}\",\"contentHash\":\"{(a.Id == "Microsoft.Data.Sqlite.Core" ? hash : a.ContentHash)}\"}}")) + extra + "}}}";
        var libraryLock = @"x\src\StorageInventory.Library\packages.lock.json";
        Assert.Equal(0, LockFileViolations(new Dictionary<string, string> { [libraryLock] = Lock() }).Count, "the pinned lock file is accepted");
        Assert.True(LockFileViolations(new Dictionary<string, string> { [libraryLock] = Lock(",\"System.Memory\":{\"type\":\"Transitive\",\"resolved\":\"4.5.3\",\"contentHash\":\"x\"}") }).Any(v => v.Contains("System.Memory")), "System.Memory in a lock file");
        Assert.True(LockFileViolations(new Dictionary<string, string> { [libraryLock] = Lock(hash: "AAAA") }).Any(v => v.Contains("content hash")), "a changed package byte (hash)");
        Assert.True(LockFileViolations(new Dictionary<string, string> { [libraryLock] = Lock(",\"Evil.Package\":{\"type\":\"Transitive\",\"resolved\":\"1.0.0\",\"contentHash\":\"x\"}") }).Any(v => v.Contains("not in the approved set")), "a fifth package");
    }

    // ================================================================ A-12: dependencies

    [Test]
    public static void A_12_the_Library_has_no_UI_process_or_network_dependency_and_only_it_references_SQLite()
    {
        var violations = new List<string>();
        var references = new Dictionary<string, Assembly>
        {
            ["Library"] = typeof(StorageInventory.Library.LibraryNames).Assembly,
            ["Core"] = typeof(StorageScanResult).Assembly,
            ["History"] = typeof(StorageInventory.History.Identity.IdentityMatching).Assembly,
#if !AUDIT_WITHOUT_APP
            ["App"] = typeof(StorageInventory.App.App).Assembly,
#endif
        };
        foreach (var (name, assembly) in references)
        {
            var referenced = assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();
            violations.AddRange(LibraryReferenceViolations(name, referenced, allowSqlite: name == "Library", checkUiProcessNetwork: name != "App"));
        }
        Assert.Equal(0, violations.Count, string.Join("; ", violations));
        Assert.True(typeof(StorageInventory.Library.LibraryNames).Assembly.GetReferencedAssemblies().Any(a => a.Name == "Microsoft.Data.Sqlite"), "the Library does reference Microsoft.Data.Sqlite (the check is not vacuous)");
        // Core and History do not reference the Library either (A-16), and History references only Core among first-party assemblies
        Assert.False(typeof(StorageScanResult).Assembly.GetReferencedAssemblies().Any(a => a.Name == "StorageInventory.Library"), "Core has no reference to the Library (OBS-06, A-16)");
        Assert.False(typeof(StorageInventory.History.Identity.IdentityMatching).Assembly.GetReferencedAssemblies().Any(a => a.Name == "StorageInventory.Library"), "History has no reference to the Library");
        var libraryFirstParty = typeof(StorageInventory.Library.LibraryNames).Assembly.GetReferencedAssemblies().Select(a => a.Name!).Where(n => n.StartsWith("StorageInventory", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
        Assert.SequenceEqual(["StorageInventory.Core", "StorageInventory.History"], libraryFirstParty, "the Library references Core and History among first-party assemblies, never the App");

        // the project files agree: the App references the Library for its runtime assets only
        var app = File.ReadAllText(Path.Combine(TestEnvironment.RepoRoot, "src", "StorageInventory.App", "StorageInventory.App.csproj"));
        Assert.Contains("StorageInventory.Library.csproj", app);
        Assert.False(Regex.IsMatch(app, @"PackageReference|Microsoft\.Data\.Sqlite|SQLitePCL"), "the App project names no SQLite package");
    }

    /// <summary>A-12 for one assembly: no UI, process, network or registry assembly; and SQLite only where <paramref name="allowSqlite"/>.</summary>
    internal static List<string> LibraryReferenceViolations(string assembly, IEnumerable<string> references, bool allowSqlite, bool checkUiProcessNetwork = true)
    {
        string[] forbidden = ["PresentationFramework", "PresentationCore", "WindowsBase", "System.Windows.Forms", "System.Diagnostics.Process", "System.Net.Http", "System.Net.Sockets", "System.Net.Primitives",
            "System.Net.Requests", "System.Net.WebClient", "System.Net.NameResolution", "Microsoft.Win32.Registry"];
        return references
            .Where(r => (checkUiProcessNetwork && forbidden.Contains(r)) || (!allowSqlite && (r.StartsWith("Microsoft.Data.Sqlite", StringComparison.Ordinal) || r.StartsWith("SQLitePCL", StringComparison.Ordinal))))
            .Select(r => $"{assembly} references {r}")
            .ToList();
    }

    [Test]
    public static void A_12_rejects_violating_reference_lists()
    {
        Assert.Equal(0, LibraryReferenceViolations("Library", ["System.Runtime", "Microsoft.Data.Sqlite", "SQLitePCLRaw.core", "StorageInventory.Core"], allowSqlite: true).Count);
        var bad = LibraryReferenceViolations("Library", ["PresentationFramework", "System.Net.Http", "System.Diagnostics.Process", "Microsoft.Win32.Registry", "System.Net.Sockets"], allowSqlite: true);
        Assert.Equal(5, bad.Count, string.Join("; ", bad));
        Assert.Equal(2, LibraryReferenceViolations("App", ["Microsoft.Data.Sqlite", "SQLitePCLRaw.provider.e_sqlite3", "System.Runtime", "PresentationFramework"], allowSqlite: false, checkUiProcessNetwork: false).Count, "the App may not compile against SQLite (it references WPF, which is its job)");
        Assert.Equal(1, LibraryReferenceViolations("Core", ["SQLitePCLRaw.core"], allowSqlite: false).Count);
    }

    // ================================================================ SEC-15: data read from the Library is never a path

    private static readonly string[] DatabaseReadingFiles = ["LibraryCatalog.cs", "RowReader.cs", "SnapshotVerifier.cs", "ImportModel.cs"];

    /// <summary>The SEC-15 text rules: the files that decode what the Library holds touch no file-system, process or path API, and
    /// the file that touches the file system (<c>LibraryStore.cs</c>) refers to no type that carries database rows.</summary>
    internal static List<string> UntrustedDataViolations(IReadOnlyDictionary<string, string> sources)
    {
        var violations = new List<string>();
        foreach (var (file, raw) in sources)
        {
            var code = Code(raw);
            var name = Path.GetFileName(file);
            if (DatabaseReadingFiles.Contains(name) && Regex.IsMatch(code, @"\b(File|Directory|Path|Process|FileInfo|DirectoryInfo|FileStream|Environment)\s*\.|new\s+(FileInfo|DirectoryInfo|FileStream)\b|ProcessStartInfo"))
            {
                violations.Add($"{file}: a file-system, path or process API in code that handles database contents");
            }
            if (name == "LibraryStore.cs" && Regex.IsMatch(code, @"\b(IRowReader|SqliteDataReader|ReaderConnection|SnapshotSummary|LibraryCatalog)\b"))
            {
                violations.Add($"{file}: the file-system layer refers to a type that carries database contents");
            }
        }
        return violations;
    }

    [Test]
    public static void SEC_15_data_read_from_the_Library_is_never_used_as_a_path()
    {
        var violations = UntrustedDataViolations(Library);
        Assert.Equal(0, violations.Count, string.Join(Environment.NewLine, violations));
        foreach (var file in DatabaseReadingFiles) Assert.True(Library.Keys.Any(k => k.EndsWith(file, StringComparison.Ordinal)), file + " was found");

        var rogue = new Dictionary<string, string>
        {
            ["src/StorageInventory.Library/LibraryCatalog.cs"] = "var p = Path.Combine(root, name); File.Delete(p);",
            ["src/StorageInventory.Library/RowReader.cs"] = "Process.Start(new ProcessStartInfo(text));",
            ["src/StorageInventory.Library/LibraryStore.cs"] = "var rows = reader.Query(sql); IRowReader r = null;",
        };
        var found = UntrustedDataViolations(rogue);
        Assert.Equal(3, found.Count, string.Join(" | ", found));
        Assert.Equal(0, UntrustedDataViolations(new Dictionary<string, string> { ["src/StorageInventory.Library/LibraryCatalog.cs"] = "// Path.Combine in a comment\nvar n = 1;" }).Count);
    }

    // ================================================================ A-14, A-15

    [Test]
    public static void A_14_the_Library_location_is_resolved_only_in_the_App_location_file()
    {
        SecurityAuditTests.OnlyIn("SpecialFolder.LocalApplicationData", @"SpecialFolder\s*\.\s*LocalApplicationData|LOCALAPPDATA|GetEnvironmentVariable\(\s*""(LOCALAPPDATA|APPDATA)""",
            "src/StorageInventory.App/Services/LibraryLocation.cs");
        var location = SecurityAuditTests.Sources.Value[Path.Combine("src", "StorageInventory.App", "Services", "LibraryLocation.cs")];
        Assert.Contains("\"Library\"", location);
        Assert.Contains("\"StorageInventory\"", location);
        // nothing in the Library or History resolves a location itself, and no setting stores one (LIB-11)
        Assert.Equal(0, SecurityAuditTests.FilesMatching(@"SpecialFolder\.(ApplicationData|CommonApplicationData|LocalApplicationData)|GetTempPath|GetTempFileName").Where(f => !f.EndsWith("LibraryLocation.cs", StringComparison.Ordinal)).Count(), "no other special-folder resolution");
        var rogue = new Dictionary<string, string> { ["src/StorageInventory.Library/R.cs"] = "var p = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);", ["src/StorageInventory.App/Services/OtherFile.cs"] = "var e = Environment.GetEnvironmentVariable(\"LOCALAPPDATA\");" };
        Assert.Equal(2, SecurityAuditTests.FilesMatching(@"SpecialFolder\s*\.\s*LocalApplicationData|LOCALAPPDATA", rogue).Count, "A-14 negative self-test");
    }

    internal static List<string> LockViolations(IReadOnlyDictionary<string, string> sources)
    {
        var violations = new List<string>();
        foreach (var (file, raw) in sources)
        {
            var code = Code(raw);
            if (!file.Replace('\\', '/').EndsWith("StorageInventory.Library/LibraryStore.cs", StringComparison.Ordinal) && Regex.IsMatch(code, @"FileShare\s*\.\s*None"))
            {
                violations.Add($"{file}: FileShare.None outside LibraryStore.cs");
            }
            if (Regex.IsMatch(code, @"new\s+(System\.Threading\.)?(Mutex|Semaphore|EventWaitHandle)\s*\(|\b(Mutex|Semaphore|EventWaitHandle)\s*\.\s*OpenExisting|\bNamedPipe")) violations.Add($"{file}: a named kernel object");
        }
        return violations;
    }

    [Test]
    public static void A_15_the_writer_lock_is_a_file_open_in_LibraryStore_and_there_is_no_named_kernel_object()
    {
        var violations = LockViolations(SecurityAuditTests.Sources.Value);
        Assert.Equal(0, violations.Count, string.Join("; ", violations));
        var store = Code(SecurityAuditTests.Sources.Value[Path.Combine("src", "StorageInventory.Library", "LibraryStore.cs")]);
        Assert.True(Regex.Matches(store, @"FileShare\s*\.\s*None").Count >= 2, "the lock open and create use FileShare.None");
        Assert.Equal(0, Regex.Matches(store, @"FileShare\.None[^;]*FileAccess\.ReadWrite|FileAccess\.ReadWrite[^;]*FileShare\.None").Count, "the lock is never opened for writing contents");
        var rogue = new Dictionary<string, string>
        {
            ["src/StorageInventory.Library/R1.cs"] = "using var m = new Mutex(false, \"Local\\\\StorageInventory\");",
            ["src/StorageInventory.Core/R2.cs"] = "var s = new Semaphore(1, 1, \"x\");",
            ["src/StorageInventory.App/R3.cs"] = "var e = new EventWaitHandle(false, EventResetMode.AutoReset, \"n\");",
            ["src/StorageInventory.Core/R4.cs"] = "new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.None);",
        };
        var rogueViolations = LockViolations(rogue);
        foreach (var file in new[] { "R1", "R2", "R3", "R4" }) Assert.True(rogueViolations.Any(v => v.Contains(file + ".cs", StringComparison.Ordinal)), $"A-15 misses {file}: {string.Join("; ", rogueViolations)}");
        Assert.Equal(0, LockViolations(new Dictionary<string, string> { ["src/StorageInventory.Core/Fine.cs"] = "var s = new SemaphoreSlim(1);" }).Count, "SemaphoreSlim is an in-process primitive, not a named kernel object");
    }

    // ================================================================ A-22 (static half): pragmas are constants

    [Test]
    public static void A_22_pragmas_are_set_only_at_open_from_constants_and_read_back()
    {
        var pragmaFiles = SecurityAuditTests.FilesMatching(@"PRAGMA\s").Where(f => f.StartsWith("src/StorageInventory.Library/", StringComparison.Ordinal)).ToList();
        Assert.True(pragmaFiles.All(f => f.StartsWith("src/StorageInventory.Library/Sql/", StringComparison.Ordinal)), "PRAGMA text only in Sql/*.cs: " + string.Join(", ", pragmaFiles));
        var open = typeof(StorageInventory.Library.LibraryNames).Assembly.GetType("StorageInventory.Library.OpenSql")!;
        string[] pragmas = ["JournalMode", "Synchronous", "LockingMode", "BusyTimeout", "TempStore", "ForeignKeys", "TrustedSchema"];
        var database = Code(Library.Single(kv => kv.Key.EndsWith("LibraryDatabase.cs", StringComparison.Ordinal)).Value);
        foreach (var pragma in pragmas)
        {
            Assert.True(open.GetField("Set" + pragma, BindingFlags.Static | BindingFlags.NonPublic) is not null && open.GetField("Get" + pragma, BindingFlags.Static | BindingFlags.NonPublic) is not null, pragma + " has a Set and a Get constant");
            Assert.True(database.Contains($"OpenSql.Set{pragma}, OpenSql.Get{pragma}", StringComparison.Ordinal), pragma + " is asserted by reading it back in the writer's Configure");
        }
        Assert.Equal(1, Regex.Matches(database, @"OpenSql\.SetImportCacheSize, OpenSql\.GetCacheSize").Count, "the import cache size is read back too");
        Assert.True((string)open.GetField("SetJournalMode", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()! == "PRAGMA journal_mode = TRUNCATE", "journal_mode is TRUNCATE");
        Assert.Equal("PRAGMA synchronous = FULL", (string)open.GetField("SetSynchronous", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!);
        Assert.Equal("PRAGMA foreign_keys = ON", (string)open.GetField("SetForeignKeys", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!);
        Assert.Equal("PRAGMA trusted_schema = OFF", (string)open.GetField("SetTrustedSchema", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!);
        Assert.Equal("PRAGMA temp_store = MEMORY", (string)open.GetField("SetTempStore", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!);
        Assert.Equal("PRAGMA cache_size = -65536", (string)open.GetField("SetImportCacheSize", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!);
        Assert.Equal("PRAGMA busy_timeout = 5000", (string)open.GetField("SetBusyTimeout", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!);
        Assert.Equal("PRAGMA locking_mode = NORMAL", (string)open.GetField("SetLockingMode", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!);
        var rogue = new Dictionary<string, string> { ["src/StorageInventory.Library/Rogue.cs"] = "command.CommandText = \"PRAGMA synchronous = OFF\";" };
        Assert.Equal(1, SecurityAuditTests.FilesMatching(@"PRAGMA\s", rogue).Count, "A-22 negative self-test");
    }

    // ================================================================ A-20: the native file set in the publish output

    [Test]
    public static void A_20_the_CI_checks_the_native_file_set_of_the_publish_output()
    {
        var script = Path.Combine(TestEnvironment.RepoRoot, "tests", "smoke", "Test-NativeFileSet.ps1");
        Assert.True(File.Exists(script), "tests/smoke/Test-NativeFileSet.ps1 exists");
        var text = File.ReadAllText(script);
        foreach (var native in new[] { "D3DCompiler_47_cor3.dll", "PenImc_cor3.dll", "PresentationNative_cor3.dll", "vcruntime140_cor3.dll", "wpfgfx_cor3.dll", "e_sqlite3.dll" })
        {
            Assert.Contains(native, text);
        }
        Assert.Contains("B7385D722C83FB52142A00477A726723745916D22A555711EE89834C1111FB2E", text);
        var workflow = File.ReadAllText(Path.Combine(TestEnvironment.RepoRoot, ".github", "workflows", "ci.yml"));
        Assert.Contains("Test-NativeFileSet.ps1", workflow);
        Assert.True(workflow.IndexOf("build.ps1 -Target Publish", StringComparison.Ordinal) < workflow.IndexOf("Test-NativeFileSet.ps1", StringComparison.Ordinal), "the check runs on the publish output, after the publish");
    }

    // ================================================================ A-21: every rule has its negative self-test

    [Test]
    public static void A_21_every_C4_audit_rule_has_a_negative_self_test()
    {
        var rules = new (string Rule, string Positive, string Negative)[]
        {
            ("A-01/A-03", "File_creation_happens_only_in_ReportRun_and_LibraryStore", "The_LibraryStore_FileStream_rule_rejects_violating_snippets"),
            ("A-04", "Deletes_and_renames_happen_only_in_ReportRun_and_the_set_aside_and_nothing_deletes_folders", "The_LibraryStore_FileStream_rule_rejects_violating_snippets"),
            ("A-05", nameof(A_05_sql_is_constant_text_in_Sql_files_and_every_value_is_a_bound_parameter), nameof(A_05_rejects_violating_snippets)),
            ("A-06", nameof(A_06_connections_are_built_only_in_LibraryDatabase_with_an_explicit_Mode_and_no_pooling), nameof(A_06_rejects_violating_snippets)),
            ("A-07/A-10", nameof(A_07_extensions_are_never_enabled_and_A_10_the_provider_is_set_once_without_a_native_resolver), nameof(A_07_extensions_are_never_enabled_and_A_10_the_provider_is_set_once_without_a_native_resolver)),
            ("A-09", nameof(A_09_only_the_SQLitePCLRaw_provider_imports_a_native_module_and_only_e_sqlite3), nameof(A_09_rejects_a_stray_native_import)),
            ("A-11", nameof(A_11_package_references_are_exactly_the_approved_set_pinned_by_the_lock_files), nameof(A_11_rejects_violating_inputs)),
            ("A-12", nameof(A_12_the_Library_has_no_UI_process_or_network_dependency_and_only_it_references_SQLite), nameof(A_12_rejects_violating_reference_lists)),
            ("A-14", nameof(A_14_the_Library_location_is_resolved_only_in_the_App_location_file), nameof(A_14_the_Library_location_is_resolved_only_in_the_App_location_file)),
            ("A-15", nameof(A_15_the_writer_lock_is_a_file_open_in_LibraryStore_and_there_is_no_named_kernel_object), nameof(A_15_the_writer_lock_is_a_file_open_in_LibraryStore_and_there_is_no_named_kernel_object)),
            ("A-22", nameof(A_22_pragmas_are_set_only_at_open_from_constants_and_read_back), nameof(A_22_pragmas_are_set_only_at_open_from_constants_and_read_back)),
            ("SEC-15", nameof(SEC_15_data_read_from_the_Library_is_never_used_as_a_path), nameof(SEC_15_data_read_from_the_Library_is_never_used_as_a_path)),
            ("A-25 (a)", nameof(LeaseAuditTests.A_25_every_mutation_in_every_first_party_assembly_needs_a_lease_part_a), nameof(LeaseAuditTests.A_25_part_a_rejects_every_violating_fixture_for_its_specific_rule)),
            ("A-25 (c)", nameof(LeaseAuditTests.A_25_part_c_leases_are_constructed_only_by_the_interlock_and_no_public_member_exposes_one), nameof(LeaseAuditTests.A_25_part_c_rejects_forged_leases_and_public_exposure)),
            ("C4-M17 (file system)", nameof(LeaseAuditTests.C4_M17_only_LibraryStore_touches_the_file_system), nameof(LeaseAuditTests.C4_M17_file_system_rule_rejects_a_call_outside_LibraryStore)),
            ("C4-M17 (lock first)", nameof(LeaseAuditTests.C4_M17_every_member_access_follows_the_writer_lock), nameof(LeaseAuditTests.C4_M17_lock_first_rule_rejects_an_inspection_before_the_lock)),
            ("C4-M13 (const SQL)", nameof(LeaseAuditTests.C4_M13_every_field_of_the_Sql_classes_is_a_constant_and_every_forwarder_is_fed_a_constant), nameof(LeaseAuditTests.C4_M13_rejects_a_static_readonly_statement_a_built_string_and_a_non_constant_forwarder_argument)),
        };
        var methods = typeof(LibrarySecurityAuditTests).GetMethods(BindingFlags.Public | BindingFlags.Static).Concat(typeof(LeaseAuditTests).GetMethods(BindingFlags.Public | BindingFlags.Static)).Concat(typeof(SecurityAuditTests).GetMethods(BindingFlags.Public | BindingFlags.Static)).Select(m => m.Name).ToHashSet();
        foreach (var (rule, positive, negative) in rules.Where(r => !r.Rule.StartsWith("A-01", StringComparison.Ordinal) && r.Rule != "A-04"))
        {
            Assert.True(methods.Contains(positive), $"{rule}: test {positive} exists");
            Assert.True(methods.Contains(negative), $"{rule}: negative self-test {negative} exists");
        }
        // A-01, A-03 and A-04 are rules of SecurityAuditTests; their negative self-test is the next test
        Assert.True(methods.Contains("File_creation_happens_only_in_ReportRun_and_LibraryStore") && methods.Contains("Deletes_and_renames_happen_only_in_ReportRun_and_the_set_aside_and_nothing_deletes_folders"));
    }

    [Test]
    public static void The_LibraryStore_FileStream_rule_rejects_violating_snippets()
    {
        // negative self-test (A-21) of the A-03 and A-04 rules as extended for LibraryStore
        var rogue = new Dictionary<string, string>
        {
            ["src/StorageInventory.Library/LibraryStore.cs"] = """
                var a = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                var b = new FileStream(path, FileMode.CreateNew, FileAccess.Read, FileShare.None);
                var c = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write);
                """,
        };
        var violations = SecurityAuditTests.LibraryStoreFileStreamViolations(rogue);
        Assert.Equal(3, violations.Count, string.Join(" | ", violations));
        var fine = new Dictionary<string, string>
        {
            ["src/StorageInventory.Library/LibraryStore.cs"] = "var a = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.None); var b = new FileStream(p, FileMode.CreateNew, FileAccess.Write, FileShare.None);",
        };
        Assert.Equal(0, SecurityAuditTests.LibraryStoreFileStreamViolations(fine).Count);

        // a rename outside the set-aside, a Library delete, and a folder delete are all rejected by the OnlyIn patterns
        var renames = new Dictionary<string, string> { ["src/StorageInventory.Library/LibrarySession.cs"] = "File.Move(a, b);", ["src/StorageInventory.Library/Other.cs"] = "File.Delete(p); Directory.Delete(d, true);" };
        Assert.Equal(1, SecurityAuditTests.FilesMatching(@"File\.Move\(", renames).Count);
        Assert.Equal(1, SecurityAuditTests.FilesMatching(@"File\.Delete\(", renames).Count);
        Assert.Equal(1, SecurityAuditTests.FilesMatching(@"(Directory\.(Delete|Move)|\.Delete\(\s*(true|recursive))", renames).Count);
    }
}

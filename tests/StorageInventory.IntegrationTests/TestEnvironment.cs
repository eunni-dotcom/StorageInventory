using System.Diagnostics;
using System.Text;
using System.Text.Json;
using StorageInventory.Testing;

namespace StorageInventory.IntegrationTests;

/// <summary>TEST CODE ONLY. Locates the repo and the repo-local tools, and runs helper processes (pwsh, cmd, subst).
/// The product itself never launches processes; building hostile fixtures here requires it.</summary>
public static class TestEnvironment
{
    public static string RepoRoot { get; } = FindRepoRoot();
    public static string Pwsh => Path.Combine(RepoRoot, "tools", "pwsh", "pwsh.exe");
    public static string ReferenceScript => Path.Combine(RepoRoot, "powershell", "StorageInventory.ps1");
    public static string TestLib => Path.Combine(RepoRoot, "powershell", "tests", "TestLib.ps1");
    public static string WorkRoot { get; } = Path.Combine(Path.GetTempPath(), "StorageInventoryTests");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "StorageInventory.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not find StorageInventory.sln above the test binaries.");
    }

    public static void RequirePwsh()
    {
        if (!File.Exists(Pwsh)) Assert.Skip("tools\\pwsh\\pwsh.exe is missing (run tools\\fetch-tools.ps1)");
    }

    public static string NewWorkFolder(string label)
    {
        var path = Path.Combine(WorkRoot, $"cs_{label}_{Guid.NewGuid().ToString("N")[..6]}");
        Directory.CreateDirectory(path);
        return path;
    }

    public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
    {
        public string All => StdOut + StdErr;
    }

    public static ProcessResult Run(string fileName, params string[] arguments) => RunIn(null, fileName, arguments);

    public static ProcessResult RunIn(string? workingDirectory, string fileName, params string[] arguments)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (workingDirectory is not null) psi.WorkingDirectory = workingDirectory;
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {fileName}");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return new ProcessResult(p.ExitCode, stdout.Result, stderr.Result);
    }

    /// <summary>Runs PowerShell code in the repo-local pwsh with TestLib.ps1 dot-sourced.</summary>
    public static ProcessResult RunTestLib(string code)
    {
        RequirePwsh();
        var script = $". '{TestLib.Replace("'", "''")}'; $ErrorActionPreference = 'Continue'; {code}";
        return Run(Pwsh, "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", script);
    }

    public static string Quote(string s) => "'" + s.Replace("'", "''") + "'";

    public static bool CreateJunction(string link, string target)
    {
        Run("cmd.exe", "/c", "mklink", "/J", link, target);
        return Directory.Exists(link);
    }

    /// <summary>Returns the 8.3 short form of an existing path, or null if the volume has short names disabled.</summary>
    public static string? GetShortPath(string path)
    {
        var r = Run("cmd.exe", "/c", $"for %I in (\"{path}\") do @echo %~sI");
        var s = r.StdOut.Trim();
        return s.Length > 0 && !string.Equals(s, path, StringComparison.OrdinalIgnoreCase) && Directory.Exists(s) ? s : null;
    }

    public static char? FreeDriveLetter()
    {
        for (var c = 'Z'; c >= 'F'; c--)
        {
            if (!Directory.Exists($"{c}:\\") && !DriveInfo.GetDrives().Any(d => char.ToUpperInvariant(d.Name[0]) == c)) return c;
        }
        return null;
    }

    public static T ParseJson<T>(string json) => JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidOperationException("empty JSON");
}

/// <summary>The Phase A adversarial fixture (TestLib.ps1 New-Fixture), built by the reference test library so that
/// both implementations are tested against exactly the same tree.</summary>
public sealed class PhaseAFixture : IDisposable
{
    private PhaseAFixture(string baseDir, string root, string outside, List<ExpectedFile> expected, Dictionary<string, string> features)
    {
        Base = baseDir;
        Root = root;
        Outside = outside;
        Expected = expected;
        Features = features;
    }

    public string Base { get; }
    public string Root { get; }
    public string Outside { get; }
    public IReadOnlyList<ExpectedFile> Expected { get; }
    public IReadOnlyDictionary<string, string> Features { get; }
    public bool SymlinksCreated => Features.TryGetValue("Symlinks", out var v) && v == "created";

    public sealed record ExpectedFile(string RelativePath, long Size);
    private sealed record FixtureJson(string Root, string Outside, List<ExpectedFile> Expected, Dictionary<string, string> Features);

    public static PhaseAFixture Create()
    {
        var baseDir = TestEnvironment.NewWorkFolder("fixture");
        var main = Path.Combine(baseDir, "main");
        var r = TestEnvironment.RunTestLib(
            $"$f = New-Fixture -Base {TestEnvironment.Quote(main)}; " +
            "$features = @{}; foreach ($k in $f.Features.Keys) { $features[$k] = [string]$f.Features[$k] }; " +
            "[pscustomobject]@{ Root = $f.Root; Outside = $f.Outside; Expected = @($f.Expected | ForEach-Object { [pscustomobject]@{ RelativePath = $_.RelativePath; Size = [long]$_.Size } }); Features = $features } | ConvertTo-Json -Depth 4 -Compress");
        var jsonLine = r.StdOut.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith('{'))
            ?? throw new InvalidOperationException("New-Fixture produced no JSON:\n" + r.All);
        var data = TestEnvironment.ParseJson<FixtureJson>(jsonLine);
        return new PhaseAFixture(baseDir, data.Root, data.Outside, data.Expected, data.Features);
    }

    public void Dispose() => Remove(Base);

    private static readonly Lazy<PhaseAFixture> SharedInstance = new(Create);

    /// <summary>One fixture shared by all read-only tests in a run; removed by <see cref="DisposeShared"/>.
    /// Tests that must alter a tree create their own.</summary>
    public static PhaseAFixture Shared => SharedInstance.Value;

    public static void DisposeShared()
    {
        if (SharedInstance.IsValueCreated) SharedInstance.Value.Dispose();
    }

    /// <summary>Junction-safe removal through TestLib's Remove-Fixture (lifts deny ACEs, removes links without
    /// following them, refuses anything outside %TEMP%).</summary>
    public static void Remove(string baseDir) => TestEnvironment.RunTestLib($"Remove-Fixture -Base {TestEnvironment.Quote(baseDir)}");
}

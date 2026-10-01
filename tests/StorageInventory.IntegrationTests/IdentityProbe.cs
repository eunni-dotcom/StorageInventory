using System.Text;
using System.Text.Json;
using StorageInventory.Core.Identity;
using StorageInventory.History.Identity;

namespace StorageInventory.IntegrationTests;

/// <summary>
/// TEST CODE ONLY: the reproducible probe behind the C3 platform evidence (Q-02, Q-13, Q-19; manual TEST-I2, TEST-I4, TEST-I6).
/// It runs the PRODUCT's own capture code (<see cref="WindowsEvidenceSource"/>, <see cref="RootIdentityHold"/>) and the
/// product's classification and re-verification rules against whatever paths it is given, and prints what Windows returned,
/// including every Win32 error, so a matrix of source types can be recorded without a debugger. It reads and writes nothing
/// on the volumes it probes.
/// <code>
/// StorageInventory.IntegrationTests --identity-probe  &lt;path&gt; [&lt;path&gt;...] [--label text] [--json file]
/// StorageInventory.IntegrationTests --identity-hold   &lt;path&gt; [--release-file file | Enter] [--timeout seconds] [--label text]
/// </code>
/// <c>--identity-probe</c> prints, per path, the raw items of one reading (E0), the confidence the matching rules would give,
/// which ID-13 items are therefore available, and the outcome of an immediate E0..E3 cycle. <c>--identity-hold</c> opens the
/// handle (E1), prints <c>HELD</c>, waits for the experimenter (a release file appears, or Enter is pressed) while they
/// remove media, re-point a letter, rename the folder or try "Safely remove", then reads E2 through the held handle and E3
/// through a fresh open and prints the verdict. It always exits 0: the outcome is the data.
/// </summary>
public static class IdentityProbe
{
    public static int RunProbe(string[] args)
    {
        var (paths, label, json) = ParseProbeArguments(args);
        if (paths.Count == 0)
        {
            Console.Error.WriteLine("usage: --identity-probe <path> [<path>...] [--label text] [--json file]");
            return 2;
        }

        var results = paths.Select(p => Probe(p, label)).ToList();
        PrintEnvironment();
        foreach (var r in results) PrintBlock(r);
        PrintMatrix(results);
        if (json is not null) File.WriteAllText(json, JsonSerializer.Serialize(results.Select(r => r.ToJson()), new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        return 0;
    }

    public static int RunHold(string[] args)
    {
        var path = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? "";
        if (path.Length == 0)
        {
            Console.Error.WriteLine("usage: --identity-hold <path> [--release-file file] [--timeout seconds] [--label text]");
            return 2;
        }
        var releaseFile = Option(args, "--release-file");
        var timeout = int.TryParse(Option(args, "--timeout"), out var t) ? t : 300;
        var label = Option(args, "--label") ?? "";

        PrintEnvironment();
        Console.WriteLine($"## Identity hold: {label} `{path}`");
        var e0 = PreflightEvidence.Collect(WindowsEvidenceSource.Instance, path);
        Console.WriteLine($"E0   {Summary(e0)}");
        using var hold = RootIdentityHold.Open(WindowsEvidenceSource.Instance, path);
        Console.WriteLine($"E1   {Summary(hold.E1)}");
        Console.WriteLine("HELD");   // the cue for a script: the window is open and the handle is held
        Console.Out.Flush();

        if (releaseFile is not null)
        {
            var deadline = DateTime.UtcNow.AddSeconds(timeout);
            while (!File.Exists(releaseFile) && DateTime.UtcNow < deadline) Thread.Sleep(200);
            if (!File.Exists(releaseFile)) Console.WriteLine($"(no release file after {timeout} s; reading now)");
        }
        else
        {
            Console.WriteLine("Perform the experiment now (remove or swap the media, re-point the letter, rename the folder, try Safely remove), then press Enter.");
            Console.ReadLine();
        }

        WindowEndReadings end;
        try
        {
            end = hold.Finish();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Finish threw {ex.GetType().Name}: {ex.Message}");
            return 0;
        }
        Console.WriteLine($"E2   {Summary(end.E2)}   (through the HELD handle)");
        Console.WriteLine($"E3   {Summary(end.E3)}   (through a FRESH open of '{path}')");
        var verdict = Reverification.Evaluate(e0, hold.E1, end.E2, end.E3);
        Console.WriteLine($"RESULT={verdict.Outcome}");
        foreach (var f in verdict.Failures) Console.WriteLine($"  {f.Stage} {f.Item} {f.Kind}: expected {f.Expected ?? "-"}, actual {f.Actual ?? "-"}{(f.Missing is null ? "" : $" ({f.Missing.Status}, {f.Missing.Call}, Win32 {f.Missing.Win32Error})")}");
        foreach (var m in verdict.MinimumMissing) Console.WriteLine($"  minimum evidence missing at E0: {m.Item} ({m.Status}, {m.Call}, Win32 {m.Win32Error})");
        Console.WriteLine("DONE");
        return 0;
    }

    /// <summary>
    /// CONTROL for the hold experiments: what a scan already does. Opens a directory enumeration on the path and leaves it
    /// part-way through (a scan always has one open while it lists), prints <c>HELD</c>, and waits for the release file. The
    /// experiments run the same actions against this and against <c>--identity-hold</c>, to show that holding the identity
    /// handle adds no kind of effect that listing a folder does not already have. It reads names only and never opens a file.
    /// </summary>
    public static int RunEnumerateHold(string[] args)
    {
        var path = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? "";
        var releaseFile = Option(args, "--release-file");
        var timeout = int.TryParse(Option(args, "--timeout"), out var t) ? t : 300;
        if (path.Length == 0 || releaseFile is null)
        {
            Console.Error.WriteLine("usage: --identity-enumerate <path> --release-file file [--timeout seconds]");
            return 2;
        }

        using var entries = new DirectoryInfo(path).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true }).GetEnumerator();
        var any = entries.MoveNext();   // the enumeration handle is open from here until the enumerator is disposed or exhausted
        Console.WriteLine($"ENUMERATING {path} (an entry was read: {any})");
        Console.WriteLine("HELD");
        Console.Out.Flush();
        var deadline = DateTime.UtcNow.AddSeconds(timeout);
        while (!File.Exists(releaseFile) && DateTime.UtcNow < deadline) Thread.Sleep(200);
        Console.WriteLine("DONE");
        return 0;
    }

    // ---- one path ----

    private sealed record ProbeResult(string Label, string Path, VolumeEvidence E0, IdentityAssessment Assessment, ReverificationResult Cycle, string DriveInfoText)
    {
        public object ToJson() => new
        {
            label = Label,
            path = Path,
            open = Item(E0.HandleOpened),
            canonicalPath = Item(E0.CanonicalPath),
            kind = E0.Kind.ToString(),
            fileSystem = Item(E0.FileSystemName),
            serial32 = Item(E0.VolumeSerial32, v => $"0x{v:X8}"),
            serial64 = Item(E0.VolumeSerial64, v => $"0x{v:X16}"),
            rootDirectoryFileId = Item(E0.RootDirectoryFileId),
            volumeLabel = Item(E0.VolumeLabel, v => $"'{v}'"),
            fileSystemFlags = Item(E0.FileSystemFlags, v => $"0x{v:X8}"),
            mountPoint = Item(E0.MountPoint),
            capacityBytes = Item(E0.CapacityBytes),
            freeBytes = Item(E0.FreeBytes),
            confidence = Assessment.Confidence.ToString(),
            confidenceReasons = Assessment.Reasons.Select(r => r.Kind + (r.Detail is null ? "" : $" ({r.Detail})")).ToArray(),
            canSave = Assessment.CanSave,
            minimumMissing = Assessment.Minimum.Missing.Select(m => $"{m.Item}: {m.Status} {m.Call} Win32 {m.Win32Error}").ToArray(),
            rootInVolume = Assessment.RootInVolume,
            networkRoot = Assessment.NetworkRoot,
            immediateCycle = Cycle.Outcome.ToString(),
            frameworkDriveInfo = DriveInfoText,
        };
    }

    private static ProbeResult Probe(string path, string label)
    {
        var e0 = PreflightEvidence.Collect(WindowsEvidenceSource.Instance, path);
        var assessment = IdentityClassifier.Assess(e0);
        ReverificationResult cycle;
        using (var hold = RootIdentityHold.Open(WindowsEvidenceSource.Instance, path))
        {
            var end = hold.Finish();
            cycle = Reverification.Evaluate(e0, hold.E1, end.E2, end.E3);
        }
        return new ProbeResult(label, path, e0, assessment, cycle, FrameworkDrive(path));
    }

    /// <summary>What the framework's own (path-based) drive query says, as an independent cross-check.</summary>
    private static string FrameworkDrive(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return $"(UNC path; DriveInfo not applicable) type=Network";
            var d = new DriveInfo(root);
            return d.IsReady ? $"type={d.DriveType} format={d.DriveFormat} label='{d.VolumeLabel}' total={d.TotalSize}" : $"type={d.DriveType} not ready";
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return "DriveInfo failed: " + ex.Message;
        }
    }

    // ---- output ----

    private static string Item<T>(EvidenceItem<T> item, Func<T, string>? format = null) => item.Status switch
    {
        EvidenceStatus.Available => format is null ? $"{item.Value}" : format(item.Value!),
        EvidenceStatus.Unavailable => $"unavailable ({item.Detail})",
        EvidenceStatus.NotProvided => $"NOT PROVIDED by the source ({item.Call}, Win32 error {item.Win32Error})",
        _ => $"FAILED ({item.Call}, Win32 error {item.Win32Error})",
    };

    private static string Summary(VolumeEvidence e) =>
        $"open={(e.HandleOpened.IsAvailable ? "OK" : Item(e.HandleOpened))}; canonical={Item(e.CanonicalPath)}; fs={Item(e.FileSystemName)}; serial32={Item(e.VolumeSerial32, v => $"0x{v:X8}")}; " +
        $"serial64={Item(e.VolumeSerial64, v => $"0x{v:X16}")}; rootId={Item(e.RootDirectoryFileId)}";

    private static void PrintEnvironment()
    {
        Console.WriteLine($"<!-- identity probe: {DateTime.UtcNow:u}; {Environment.OSVersion}; .NET {Environment.Version}; elevated={IsElevated()} -->");
    }

    private static bool IsElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    private static void PrintBlock(ProbeResult r)
    {
        var e = r.E0;
        Console.WriteLine();
        Console.WriteLine($"### {(r.Label.Length > 0 ? r.Label + ": " : "")}`{r.Path}`");
        Console.WriteLine();
        Console.WriteLine("| Item | Result |");
        Console.WriteLine("|---|---|");
        Console.WriteLine($"| 0-access open (`CreateFileW`, access 0, share read/write/delete, backup semantics) | {(e.HandleOpened.IsAvailable ? "OK" : Item(e.HandleOpened))} |");
        Console.WriteLine($"| `GetFinalPathNameByHandleW` | {Item(e.CanonicalPath)} |");
        Console.WriteLine($"| `GetVolumeInformationByHandleW` filesystem | {Item(e.FileSystemName)} |");
        Console.WriteLine($"| ... 32-bit serial | {Item(e.VolumeSerial32, v => $"0x{v:X8}")} |");
        Console.WriteLine($"| ... label | {Item(e.VolumeLabel, v => $"'{v}'")} |");
        Console.WriteLine($"| ... flags | {Item(e.FileSystemFlags, v => $"0x{v:X8}")} |");
        Console.WriteLine($"| `FileIdInfo` 64-bit serial | {Item(e.VolumeSerial64, v => $"0x{v:X16}")} |");
        Console.WriteLine($"| `FileIdInfo` root directory file ID | {Item(e.RootDirectoryFileId)} |");
        Console.WriteLine($"| `GetVolumePathNameW` | {Item(e.MountPoint)} |");
        Console.WriteLine($"| `GetDiskFreeSpaceExW` capacity / free | {Item(e.CapacityBytes)} / {Item(e.FreeBytes)} |");
        Console.WriteLine($"| Framework `DriveInfo` (cross-check) | {r.DriveInfoText} |");
        Console.WriteLine($"| Source kind | {e.Kind} (root `{r.Assessment.RootInVolume ?? "?"}`{(r.Assessment.NetworkRoot is null ? "" : $", share `{r.Assessment.NetworkRoot}`")}) |");
        Console.WriteLine($"| Confidence | {r.Assessment.Confidence}{(r.Assessment.Reasons.Count == 0 ? "" : " (" + string.Join("; ", r.Assessment.Reasons.Select(x => x.Kind + (x.Detail is null ? "" : " " + x.Detail) + (x.Failure is null ? "" : $", {x.Failure.Status} Win32 {x.Failure.Win32Error}"))) + ")")} |");
        Console.WriteLine($"| ID-13 items available | {Id13(e)} |");
        Console.WriteLine($"| Save to History | {(r.Assessment.CanSave ? "available" : "UNAVAILABLE: " + string.Join("; ", r.Assessment.Minimum.Missing.Select(m => $"{m.Item} {m.Status} Win32 {m.Win32Error}")))} |");
        Console.WriteLine($"| Immediate E0..E3 cycle, nothing changed | {r.Cycle.Outcome} |");
    }

    private static string Id13(VolumeEvidence e) =>
        $"canonical path {Tick(e.CanonicalPath.IsAvailable)}; " +
        (e.Kind == SourceKind.LocalVolume
            ? $"filesystem name {Tick(e.FileSystemName.IsAvailable)} + 32-bit serial {Tick(e.VolumeSerial32.IsAvailable)} (required for a local volume); "
            : $"filesystem name {Tick(e.FileSystemName.IsAvailable)} + 32-bit serial {Tick(e.VolumeSerial32.IsAvailable)} (network: required again only if returned); ") +
        $"64-bit serial + root file ID {Tick(e.VolumeSerial64.IsAvailable && e.RootDirectoryFileId.IsAvailable)} (required later only if returned)";

    private static string Tick(bool available) => available ? "yes" : "no";

    private static void PrintMatrix(IReadOnlyList<ProbeResult> results)
    {
        Console.WriteLine();
        Console.WriteLine("### Matrix");
        Console.WriteLine();
        Console.WriteLine("| Source | Input path | Open | Filesystem | Serial32 | Serial64 | Root file ID | Label | Capacity / free | Kind | Confidence | FileIdInfo | Save |");
        Console.WriteLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var r in results)
        {
            var e = r.E0;
            Console.WriteLine($"| {r.Label} | `{r.Path}` | {(e.HandleOpened.IsAvailable ? "OK" : "FAILED " + e.HandleOpened.Win32Error)} | {Short(e.FileSystemName)} | {Short(e.VolumeSerial32, v => $"0x{v:X8}")} | {Short(e.VolumeSerial64, v => $"0x{v:X16}")} | {Short(e.RootDirectoryFileId)} | {Short(e.VolumeLabel, v => $"'{v}'")} | {Short(e.CapacityBytes)} / {Short(e.FreeBytes)} | {e.Kind} | {r.Assessment.Confidence} | {(e.VolumeSerial64.IsAvailable ? "provided" : e.VolumeSerial64.Status == EvidenceStatus.NotProvided ? "not provided (Win32 " + e.VolumeSerial64.Win32Error + ")" : e.VolumeSerial64.Status.ToString())} | {(r.Assessment.CanSave ? "yes" : "no")} |");
        }
    }

    private static string Short<T>(EvidenceItem<T> item, Func<T, string>? format = null) => item.Status switch
    {
        EvidenceStatus.Available => format is null ? $"{item.Value}" : format(item.Value!),
        EvidenceStatus.Unavailable => "unavailable",
        _ => $"{item.Status} Win32 {item.Win32Error}",
    };

    // ---- arguments ----

    private static (List<string> Paths, string Label, string? Json) ParseProbeArguments(string[] args)
    {
        var paths = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--label" or "--json") { i++; continue; }
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) paths.Add(args[i]);
        }
        return (paths, Option(args, "--label") ?? "", Option(args, "--json"));
    }

    private static string? Option(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}

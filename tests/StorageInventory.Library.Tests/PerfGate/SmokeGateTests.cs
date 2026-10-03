using System.Diagnostics;
using System.Text.Json;
using StorageInventory.Testing;

namespace StorageInventory.Library.Tests.PerfGate;

/// <summary>
/// The end-to-end SMOKE GATE: the real path of TEST-P1 at ~50,000 files for one first save (F) and one re-scan (R): the orchestrator
/// (tests/perf/perf_session.py, Python) declares a session, builds the prefills in separate processes, copies each into a fresh directory,
/// imports with the real probes in a fresh child per run, writes the run records, runs the attribution gate on the pre-import copy and the
/// pre-COMMIT journal (PASS), the four cancel points, the kill and the timed recovery, the deletion, and the negative control (which must
/// FAIL). The quiet check is skipped with the explicit <c>--no-load-validity</c> flag, which marks the session NOT a gate session; and a
/// session that is declared the gate session at this scale is refused.
/// </summary>
public static class SmokeGateTests
{
    private static string? FindPython()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("python", "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });
            p!.WaitForExit(15_000);
            return p.ExitCode == 0 ? "python" : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static List<string> HarnessArguments()
    {
        var host = Environment.ProcessPath!;
        return string.Equals(Path.GetFileNameWithoutExtension(host), "dotnet", StringComparison.OrdinalIgnoreCase)
            ? ["--exe", host, "--dll", System.Reflection.Assembly.GetEntryAssembly()!.Location]
            : ["--exe", host];
    }

    private static (int Code, string Output, string Error) Python(string python, IEnumerable<string> args, int timeoutMs)
    {
        var info = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var p = Process.Start(info)!;
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs)) { p.Kill(entireProcessTree: true); throw new AssertionException("the orchestrator timed out: " + output.Result[^Math.Min(800, output.Result.Length)..]); }
        return (p.ExitCode, output.Result, error.Result);
    }

    [Test]
    public static void SmokeGateRunsTheWholePathAndMarksItselfNotAGateSession()
    {
        var python = FindPython() ?? throw new SkipException("python is not on PATH: the smoke gate drives the Python orchestrator");
        var script = Path.Combine(WorkloadClassTests.FindRepoRoot(), "tests", "perf", "perf_session.py");
        var work = Path.Combine(World.RunRoot, "smoke-gate");
        var evidence = Path.Combine(work, "evidence");
        var common = new List<string> { script };

        // a gate session at smoke scale is refused before anything runs
        var refused = Python(python, [.. common, "declare", "--smoke", "--gate", "--evidence", Path.Combine(work, "refused"), .. HarnessArguments(), "--prefill-cache", Path.Combine(work, "cache")], 120_000);
        Assert.Equal(2, refused.Code, "declaring a scaled session the gate session is refused: " + refused.Error);
        Assert.Contains("REFUSED", refused.Error);
        Assert.False(Directory.Exists(Path.Combine(work, "refused")) && Directory.EnumerateFiles(Path.Combine(work, "refused"), "manifest.json", SearchOption.AllDirectories).Any(), "...and no manifest was written");

        var run = Python(python, [.. common, "go", "--smoke", "--no-load-validity", "--evidence", evidence, .. HarnessArguments(), "--prefill-cache", Path.Combine(work, "cache")], 600_000);
        Assert.Equal(0, run.Code, "the smoke session ran: " + run.Error + "\n" + run.Output[^Math.Min(1500, run.Output.Length)..]);

        var session = Directory.GetDirectories(evidence).Single();
        using var manifestDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(session, "manifest.json")));
        var manifest = manifestDoc.RootElement;
        Assert.False(manifest.GetProperty("gate").GetBoolean(), "the manifest, written before the session started, says it is not the gate session");
        Assert.False(manifest.GetProperty("loadValidity").GetBoolean(), "...and that load validity is off");
        Assert.True(manifest.GetProperty("notGateBecause").EnumerateArray().Any(r => r.GetString()!.Contains("--no-load-validity", StringComparison.Ordinal)), "...because of --no-load-validity");
        Assert.True(DateTime.Parse(manifest.GetProperty("declaredUtc").GetString()!).ToUniversalTime() < File.GetCreationTimeUtc(Path.Combine(session, "runs.jsonl")).AddSeconds(1), "the manifest precedes the first run record");

        using var sessionDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(session, "session.json")));
        var s = sessionDoc.RootElement;
        Assert.Equal("complete", s.GetProperty("status").GetString(), "status");
        Assert.True(s.GetProperty("quietChecks").EnumerateArray().All(q => q.GetProperty("verdict").GetString() == "SKIPPED"), "the quiet checks were skipped, explicitly");
        var runs = s.GetProperty("runs").EnumerateArray().ToList();
        Assert.True(runs.All(r => r.GetProperty("status").GetString() == "ok"), "every run is ok: " + string.Join(", ", runs.Where(r => r.GetProperty("status").GetString() != "ok").Select(r => r.GetProperty("runId").GetString())));
        string[] kinds = ["timed", "attribution", "cancel:1", "cancel:2", "cancel:3", "cancel:4", "crash"];
        foreach (var cell in new[] { "F-2M-25-system", "R-2M-25-system-1-05-05" })
            foreach (var kind in kinds)
                Assert.True(runs.Any(r => r.GetProperty("cell").GetString() == cell && r.GetProperty("kind").GetString() == kind), $"{cell} has a {kind} run");
        Assert.Equal(2, runs.Count(r => r.GetProperty("kind").GetString() == "attribution" && r.GetProperty("metrics").GetProperty("attribution").GetString() == "PASS"), "both attributions PASS");
        var control = s.GetProperty("negativeControl").EnumerateArray().ToList();
        foreach (var variant in new[] { "new", "existing" })
            Assert.True(control.Any(c => c.GetProperty("variant").GetString() == variant && c.GetProperty("verdict").GetString() == "FAIL"), $"the negative control ({variant}) FAILs");
        Assert.True(!control.Any(c => c.GetProperty("verdict").GetString() == "PASS"), "no control passed");
        var evaluation = s.GetProperty("evaluation");
        Assert.False(evaluation.GetProperty("judged").GetBoolean(), "the session judges no budget");
        foreach (var budget in evaluation.GetProperty("budgets").EnumerateObject())
            Assert.Equal("NOT JUDGED", budget.Value.GetProperty("outcome").GetString(), budget.Name);

        // the raw table holds every attribution's verdict file and the run records name only bare file names
        var raw = File.ReadAllLines(Path.Combine(session, "runs.jsonl")).Select(l => JsonDocument.Parse(l).RootElement).ToList();
        Assert.True(raw.Count(r => r.GetProperty("kind").GetString() == "attribution") == 2, "two attribution records in the raw file");
        foreach (var a in raw.Where(r => r.GetProperty("kind").GetString() == "attribution").Select(r => r.GetProperty("attribution")))
        {
            Assert.Equal(64, a.GetProperty("databaseCopySha256").GetString()!.Length, "the copy's SHA-256 is in the record");
            Assert.True(a.GetProperty("target").GetProperty("source").GetString() is "new" or "existing", "the target is in the record");
        }
        Assert.True(File.Exists(Path.Combine(session, "machine.json")) && File.Exists(Path.Combine(session, "collections.jsonl")), "the machine record and the load series were kept");
    }
}

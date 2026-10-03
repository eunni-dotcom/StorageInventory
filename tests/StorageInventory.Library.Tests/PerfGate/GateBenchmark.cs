using System.Globalization;
using System.Text.Json;

namespace StorageInventory.Library.Tests.PerfGate;

/// <summary>
/// <c>StorageInventory.Library.Tests.exe --benchmark gate &lt;command&gt;</c>: the commands the TEST-P1 orchestrator
/// (tests/perf/perf_session.py) drives. Each command is one fresh process; the orchestrator owns the session. See tests/perf/README.md.
/// <list type="bullet">
/// <item><c>plan [--scale s] [--binary-commit c] [--dirty]</c>: the matrix as JSON: every cell's parameters, class (computed from the
/// parameters), generator, prefill, budgets and run kinds, and the binary's identity.</item>
/// <item><c>prefill --cell id --scale s --out file</c>: builds a cell's prefilled Library (three snapshots).</item>
/// <item><c>describe --cell id --scale s --out file</c>: the target's realised generator statistics.</item>
/// <item><c>run --cell id --mode timed|attribution|cancel:1..4|cancel-after-final|crash|delete --scale s --prefill-from file
/// [--stats file] [--analysis-dir dir] --label text --binary-commit c [--dirty]</c>: one run; its record is the last line.</item>
/// <item><c>recover --dir d --appdata a [--snapshots n]</c>: the start-up open after a killed run.</item>
/// <item><c>control --variant new|existing --out dir --label text [--names n] [--target-names n] [--binary-commit c]</c>: the negative control.</item>
/// <item><c>machine</c>: what only the product knows about itself (engine, page size, cache size).</item>
/// </list>
/// </summary>
internal static class GateBenchmark
{
    internal static int Run(string[] args)
    {
        try
        {
            return args switch
            {
                ["plan", ..] => Plan(args[1..]),
                ["prefill", ..] => GateRunner.Prefill(args[1..]),
                ["describe", ..] => GateRunner.Describe(args[1..]),
                ["run", ..] => RunCell(args[1..]),
                ["recover", ..] => GateRunner.Recover(args[1..]),
                ["control", ..] => Control(args[1..]),
                ["machine"] => Machine(),
                _ => Usage(),
            };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidCellException)
        {
            Console.Error.WriteLine("gate: " + ex.Message);
            return 2;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("usage: --benchmark gate plan | prefill | describe | run | recover | control | machine   (see tests/perf/README.md)");
        return 2;
    }

    private static BinaryRecord Binary(string[] args, string engine) =>
        BinaryIdentity.Current(GateSupport.Opt(args, "binary-commit", "unknown"), GateSupport.Flag(args, "dirty"), engine);

    private static string Engine()
    {
        var scratch = Path.Combine(GateSupport.BenchRoot(), "SI-Gate-Engine-" + Guid.NewGuid().ToString("N")[..8]);
        File.WriteAllBytes(scratch, []);
        try
        {
            return RawSqlite.Scalar(scratch, "SELECT sqlite_version()")?.ToString() ?? "unknown";
        }
        finally
        {
            try { File.Delete(scratch); } catch (IOException) { }
        }
    }

    private static int Machine()
    {
        Console.WriteLine(JsonSerializer.Serialize(new { engine = Engine(), pageSize = 4096, importCacheKiB = GateConstants.ImportCacheKiB, generatorVersion = GateConstants.GeneratorVersion, runtime = Environment.Version.ToString() }, RecordJson.Options));
        return 0;
    }

    private static int Plan(string[] args)
    {
        var scale = double.Parse(GateSupport.Opt(args, "scale", "1"), CultureInfo.InvariantCulture);
        var binary = Binary(args, Engine());
        var cells = GateMatrix.Cells.Select(c =>
        {
            var b = c.Budgets;
            var g = c.Cell;
            var kinds = new List<string> { "timed", "attribution" };
            if (b.Perf15CSeconds > 0) kinds.AddRange(["cancel:1", "cancel:2", "cancel:3", "cancel:4"]);
            if (b.Recovery) kinds.Add("crash");
            var scaled = c.Scaled(scale);
            return new
            {
                id = c.Id,
                label = g.Label,
                @class = WorkloadClassifier.Name(c.Class),
                statedClass = GateMatrix.StatedClass[c.Id],
                nominal = new { kind = g.Kind.ToString(), files = g.Files, d = g.D, model = g.Model, rho = g.Rho, delta = g.Delta, alpha = g.Alpha, family = g.Family?.ToString(), intoNewSource = g.IntoNewSource, familyRescan = g.FamilyRescan },
                library = c.Library.ToString(),
                runFamily = c.RunFamily,
                targetFiles = scaled.TargetFiles,
                prefillPerSnapshot = scaled.PrefillPerSnapshot,
                newSource = c.NewSource,
                generatorParameters = c.Params,
                churn = c.Churn,
                prefillKey = BinaryIdentity.PrefillKey(scaled.Prefill, binary),
                budgets = new { perf01 = b.Perf01, perf14Target = b.Perf14Target, perf14StopSeconds = b.Perf14StopSeconds, perf15a = b.Perf15A, perf15cSeconds = b.Perf15CSeconds, recovery = b.Recovery },
                kinds,
            };
        }).ToList();
        Console.WriteLine(JsonSerializer.Serialize(new { schema = GateConstants.RecordSchema, scale, generatorVersion = GateConstants.GeneratorVersion, binary, cells }, RecordJson.Options));
        return 0;
    }

    private static int RunCell(string[] args)
    {
        var scale = double.Parse(GateSupport.Opt(args, "scale", "1"), CultureInfo.InvariantCulture);
        var mode = GateSupport.Opt(args, "mode", "timed");
        if (mode is not ("timed" or "attribution" or "cancel:1" or "cancel:2" or "cancel:3" or "cancel:4" or "cancel-after-final" or "crash" or "delete"))
            throw new ArgumentException("unknown mode " + mode);
        var spec = GateMatrix.Find(GateSupport.Req(args, "cell")).Scaled(scale);
        var options = new RunOptions(spec, scale, mode, GateSupport.Req(args, "prefill-from"), GateSupport.Opt(args, "stats", "") is { Length: > 0 } s ? s : null,
            GateSupport.Opt(args, "analysis-dir", Path.GetTempPath()), GateSupport.Opt(args, "label", spec.Id + "-" + mode.Replace(':', '-')), Binary(args, Engine()));
        var record = GateRunner.RunCell(options);
        var code = ExitCodeOf(mode, record, out var problems);
        if (code != 0) Console.Error.WriteLine("gate: the run record is not fit as raw evidence: " + string.Join("; ", problems));
        return code;
    }

    /// <summary>The exit code of a measuring child (§15.4 "Validity"). <b>0</b> whenever its record can be interpreted, WHATEVER the run's result: a save that
    /// failed (even at BEGIN, before a second IMP-11 check), a cancellation the product did not honour (the save published), a cancel point that was never reached
    /// and a rollback that did not hold are MEASURED results that the orchestrator judges (MISSED or STOP) and never replaces (C4R-M03). <b>3</b> only for a record
    /// that cannot be interpreted as raw evidence (a missing or malformed field), which the orchestrator treats as an invalid run. A crash run's record is the one
    /// printed before the kill; the orchestrator judges it with its recovery.</summary>
    internal static int ExitCodeOf(string mode, RunRecord record, out List<string> problems)
    {
        problems = RecordJson.Validate(RecordJson.Serialize(record));
        return problems.Count > 0 && mode != "crash" ? 3 : 0;
    }

    private static int Control(string[] args)
    {
        var json = NegativeControl.Build(GateSupport.Req(args, "out"), GateSupport.Req(args, "variant"), GateSupport.Opt(args, "label", "negative-control"),
            int.Parse(GateSupport.Opt(args, "names", "20000"), CultureInfo.InvariantCulture), int.Parse(GateSupport.Opt(args, "target-names", "8000"), CultureInfo.InvariantCulture));
        Console.WriteLine(json);
        return 0;
    }
}

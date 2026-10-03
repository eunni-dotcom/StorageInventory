using System.Globalization;

namespace StorageInventory.Library.Tests.PerfGate;

/// <summary>Constants of the TEST-P1 gate tooling.</summary>
internal static class GateConstants
{
    /// <summary>Bumped whenever a generator changes what a prefill contains, so a cached prefill of an older generator can never be
    /// reused (the prefill cache key carries it). Version 4 is the first of the C4 repair's port (ObjGenerator.cs).</summary>
    internal const int GeneratorVersion = 4;

    /// <summary>The run record's schema version (RunRecord.cs; tests/perf/attribute_run.py reads it).</summary>
    internal const int RecordSchema = 1;

    /// <summary>The page cache the importer sets (OpenSql.SetImportCacheSize: 64 MiB) and the default page size, for the machine record.</summary>
    internal const long ImportCacheKiB = 65_536;
}

/// <summary>Which snapshots the Library holds before the import (§15.4 "The Library before the import").</summary>
internal enum LibraryBefore
{
    /// <summary>L2: three snapshots of the target's size in two sources (p1 and p3 of source A, p2 of source B).</summary>
    L2,

    /// <summary>L1: three snapshots of 250k files (the append control's Library).</summary>
    L1,

    /// <summary>Three 2M-file snapshots (the 10M cell of N2).</summary>
    ThreeTwoMillion,
}

/// <summary>The content of a prefill (cache key part): which generator builds the three snapshots, how large they are, with which
/// parameters.</summary>
internal sealed record PrefillSpec(string Family, long PerSnapshot, string Params);

/// <summary>
/// A TEST-P1 cell as the harness runs it: the numeric cell of §15.4 (<see cref="Cell"/>, whose class the classifier computes from its
/// PARAMETERS alone) plus how it is generated (the run family of the design-review harness, the sizes, the target source).
/// </summary>
/// <param name="Id">A stable identifier (a file-name safe slug).</param>
/// <param name="Cell">The matrix cell's numeric parameters.</param>
/// <param name="Library">The Library before the import.</param>
/// <param name="RunFamily">"obj" (first save), "objrescan" (re-scan), "hash" (N2), "mixed" (N3), "rescan" (N2 re-scan), "append" (N1).</param>
/// <param name="TargetFiles">The target's nominal files (before <see cref="GateMatrix.Scaled"/>).</param>
/// <param name="PrefillPerSnapshot">The nominal files of each prefilled snapshot.</param>
/// <param name="NewSource">The target is the first snapshot of a new source.</param>
/// <param name="Params">The objective generator's parameter text ("d=0.25;model=system;rho=0.01;...") of an obj / objrescan cell; empty otherwise.</param>
/// <param name="Churn">The churn of the N2 re-scan family.</param>
internal sealed record CellSpec(string Id, GateCell Cell, LibraryBefore Library, string RunFamily, long TargetFiles, long PrefillPerSnapshot, bool NewSource, string Params, double Churn = 0)
{
    /// <summary>The class of the cell, from its parameters.</summary>
    internal WorkloadClass Class => WorkloadClassifier.Classify(Cell);

    /// <summary>Number of snapshots in the Library before (always three: two of source A, one of source B).</summary>
    internal PrefillSpec Prefill => new(RunFamily switch { "obj" or "objrescan" => "obj", "rescan" => "hash", var f => f }, PrefillPerSnapshot,
        RunFamily is "obj" or "objrescan" ? ObjParams.PrefillPart(Params) : "");

    /// <summary>The folders of a snapshot of <paramref name="files"/> files (about 0.25 folders per file, branching 6).</summary>
    internal static long Folders(long files) => Math.Max(10, files / 4);

    /// <summary>The budgets the cell is judged against (§15.4 "Budgets by class" and §15.3). A cell of the matrix below 1M files
    /// gates only PERF-01 and the cancellation budgets; PERF-14 is judged at 2M files.</summary>
    internal BudgetApplicability Budgets
    {
        get
        {
            var c = Class;
            var informational = c == WorkloadClass.Informational;
            var representative = c == WorkloadClass.Representative;
            var twoMillion = Cell.Files == 2_000_000;
            return new BudgetApplicability(
                Perf01: representative,
                Perf14Target: twoMillion && c is WorkloadClass.Representative or WorkloadClass.Stress,
                Perf14StopSeconds: twoMillion ? (c == WorkloadClass.WorstCase ? 90.0 : c is WorkloadClass.Representative or WorkloadClass.Stress ? 67.5 : 0.0) : 0.0,
                Perf15A: true,
                Perf15CSeconds: informational ? 0.0 : representative ? 1.75 : 4.75,
                Recovery: !informational);
        }
    }

    /// <summary>The scaled copy of the cell: file counts multiplied by <paramref name="scale"/> (smoke scale). The numeric cell, and so
    /// its class, is the NOMINAL one: a class is never decided by the size actually run, and a scaled run gates nothing.</summary>
    internal CellSpec Scaled(double scale) => scale == 1.0 ? this : this with
    {
        TargetFiles = Math.Max(1_000, (long)Math.Round(TargetFiles * scale)),
        PrefillPerSnapshot = Math.Max(1_000, (long)Math.Round(PrefillPerSnapshot * scale)),
    };
}

/// <summary>Which budgets apply to a cell.</summary>
/// <param name="Perf01">PERF-01 (median rows/s of five valid runs at least 100,000; stop below 50,000).</param>
/// <param name="Perf14Target">PERF-14's engine target (T-IMPORT at most 45 s) at 2M files in a representative or stress cell.</param>
/// <param name="Perf14StopSeconds">PERF-14's engine stop threshold (67.5 s representative and stress, 90 s worst-case), 0 when none.</param>
/// <param name="Perf15A">PERF-15 (a) key-range attribution (every cell).</param>
/// <param name="Perf15CSeconds">PERF-15 (c) cancellation budget (1.75 s representative; 4.75 s stress and worst-case), 0 when none.</param>
/// <param name="Recovery">The start-up open after a kill at the journal's peak (at most 5 s) is judged.</param>
internal sealed record BudgetApplicability(bool Perf01, bool Perf14Target, double Perf14StopSeconds, bool Perf15A, double Perf15CSeconds, bool Recovery);

/// <summary>§15.4's matrix (TEST-P1): its 14 rows as 16 cells (the N1 row names three sizes).</summary>
internal static class GateMatrix
{
    private const long M = 1_000_000;
    private const long K = 1_000;

    private const string Sys25 = "d=0.25;model=system";

    private static string Re(string first, string rho, string delta, string alpha) => $"{first};rho={rho};delta={delta};alpha={alpha}";

    /// <summary>The matrix, in the order of §15.4's table.</summary>
    internal static readonly IReadOnlyList<CellSpec> Cells =
    [
        new("F-1M-25-system", GateCell.F(1 * M, 25m, "system"), LibraryBefore.L2, "obj", 1 * M, 1 * M, true, Sys25),
        new("F-2M-25-system", GateCell.F(2 * M, 25m, "system"), LibraryBefore.L2, "obj", 2 * M, 2 * M, true, Sys25),
        new("R-1M-25-system-1-05-05", GateCell.R(1 * M, 25m, "system", 1m, 0.5m, 0.5m), LibraryBefore.L2, "objrescan", 1 * M, 1 * M, false, Re(Sys25, "0.01", "0.005", "0.005")),
        new("R-2M-25-system-1-05-05", GateCell.R(2 * M, 25m, "system", 1m, 0.5m, 0.5m), LibraryBefore.L2, "objrescan", 2 * M, 2 * M, false, Re(Sys25, "0.01", "0.005", "0.005")),
        new("F-2M-60-data", GateCell.F(2 * M, 60m, "data"), LibraryBefore.L2, "obj", 2 * M, 2 * M, true, "d=0.6;model=data"),
        new("R-2M-25-system-5-25-25", GateCell.R(2 * M, 25m, "system", 5m, 2.5m, 2.5m), LibraryBefore.L2, "objrescan", 2 * M, 2 * M, false, Re(Sys25, "0.05", "0.025", "0.025")),
        new("R-2M-25-system-10-5-5", GateCell.R(2 * M, 25m, "system", 10m, 5m, 5m), LibraryBefore.L2, "objrescan", 2 * M, 2 * M, false, Re(Sys25, "0.1", "0.05", "0.05")),
        new("R-2M-60-data-1-05-05", GateCell.R(2 * M, 60m, "data", 1m, 0.5m, 0.5m), LibraryBefore.L2, "objrescan", 2 * M, 2 * M, false, Re("d=0.6;model=data", "0.01", "0.005", "0.005")),
        new("F-2M-100-data", GateCell.F(2 * M, 100m, "data"), LibraryBefore.L2, "obj", 2 * M, 2 * M, true, "d=1;model=data"),
        new("N2-2M-existing", GateCell.Named(NameFamily.N2, 2 * M), LibraryBefore.L2, "hash", 2 * M, 2 * M, false, ""),
        new("N3-2M-existing", GateCell.Named(NameFamily.N3, 2 * M), LibraryBefore.L2, "mixed", 2 * M, 2 * M, false, ""),
        new("N2-rescan-2M", GateCell.Named(NameFamily.N2, 2 * M, rescan: true), LibraryBefore.L2, "rescan", 2 * M, 2 * M, false, "", Churn: 0.01),
        new("N1-1M", GateCell.Named(NameFamily.N1, 1 * M), LibraryBefore.L1, "append", 1 * M, 250 * K, false, ""),
        new("N1-2M", GateCell.Named(NameFamily.N1, 2 * M), LibraryBefore.L1, "append", 2 * M, 250 * K, false, ""),
        new("N1-10M", GateCell.Named(NameFamily.N1, 10 * M), LibraryBefore.L1, "append", 10 * M, 250 * K, false, ""),
        new("N2-10M", GateCell.Named(NameFamily.N2, 10 * M), LibraryBefore.ThreeTwoMillion, "hash", 10 * M, 2 * M, false, ""),
    ];

    /// <summary>§15.4's Class column for each cell, written out here separately from the classifier so that the test compares them.</summary>
    internal static readonly IReadOnlyDictionary<string, string> StatedClass = new Dictionary<string, string>
    {
        ["F-1M-25-system"] = "Representative", ["F-2M-25-system"] = "Representative", ["R-1M-25-system-1-05-05"] = "Representative",
        ["R-2M-25-system-1-05-05"] = "Representative", ["F-2M-60-data"] = "Stress", ["R-2M-25-system-5-25-25"] = "Stress",
        ["R-2M-25-system-10-5-5"] = "Stress", ["R-2M-60-data-1-05-05"] = "Stress", ["F-2M-100-data"] = "Worst-case",
        ["N2-2M-existing"] = "Worst-case", ["N3-2M-existing"] = "Worst-case", ["N2-rescan-2M"] = "Worst-case",
        ["N1-1M"] = "Informational", ["N1-2M"] = "Informational", ["N1-10M"] = "Informational", ["N2-10M"] = "Informational",
    };

    internal static CellSpec Find(string id) => Cells.FirstOrDefault(c => c.Id == id) ?? throw new ArgumentException("unknown cell " + id);

    /// <summary>The cell ids that the smoke plans run: one first save and one re-scan, at the scale given.</summary>
    internal static readonly string[] SmokeCells = ["F-2M-25-system", "R-2M-25-system-1-05-05"];

    internal static string Format(double v) => v.ToString("R", CultureInfo.InvariantCulture);
}

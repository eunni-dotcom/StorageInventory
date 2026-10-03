using System.Globalization;

namespace StorageInventory.Library.Tests.PerfGate;

/// <summary>The four workload classes of §15.4, in the order of their hardness (informational is outside the order: no budget is judged
/// on it).</summary>
internal enum WorkloadClass
{
    Representative = 0,
    Stress = 1,
    WorstCase = 2,
    Informational = 3,
}

/// <summary>A named family of the C4 design review (§15.4: N1 append, N2 interleaved, N3 mixed names).</summary>
internal enum NameFamily
{
    /// <summary>N1: C4's append-shaped control, the best case (informational at any size).</summary>
    N1,

    /// <summary>N2: names interleaved over the key space (the "hash" family of the design review).</summary>
    N2,

    /// <summary>N3: mixed names (the "mixed" family of the design review).</summary>
    N3,
}

/// <summary>Which snapshot a cell imports.</summary>
internal enum CellKind
{
    /// <summary>F(n, d, m): the first save of a new source of the generator (§15.4).</summary>
    FirstSave,

    /// <summary>R(n, d, m; rho, delta, alpha): a re-scan into the existing source whose first snapshot is F(n, d, m).</summary>
    Rescan,

    /// <summary>A named family (N1, N2, N3), possibly a re-scan of an N2 source.</summary>
    Family,
}

/// <summary>
/// A TEST-P1 cell, fully described by NUMERIC input parameters fixed before it runs (D-53). Percentages are in percent
/// (<c>25m</c> is 25%) and exact: <c>decimal</c>, so "25% + one millionth" is strictly above 25%, as in the Python reference
/// (<c>workload_class.py</c>, which uses fractions).
/// </summary>
/// <param name="Kind">First save, re-scan or named family.</param>
/// <param name="Files">The target's files (the size).</param>
/// <param name="D">The source's distinct-name ratio in percent (generator cells only; 0 for a family cell).</param>
/// <param name="Model">Name model "system" or "data" (generator cells only).</param>
/// <param name="Rho">Re-scan: files renamed to names new to the source, in percent.</param>
/// <param name="Delta">Re-scan: files deleted, in percent.</param>
/// <param name="Alpha">Re-scan: files added with names already in the source, in percent.</param>
/// <param name="Family">The named family of a family cell.</param>
/// <param name="IntoNewSource">A family cell's target: a new source (true) or an existing one.</param>
/// <param name="FamilyRescan">A family cell that is the 1% re-scan of a source of the family.</param>
internal sealed record GateCell(CellKind Kind, long Files, decimal D = 0, string? Model = null, decimal Rho = 0, decimal Delta = 0, decimal Alpha = 0,
    NameFamily? Family = null, bool IntoNewSource = false, bool FamilyRescan = false)
{
    internal static GateCell F(long files, decimal d, string model) => new(CellKind.FirstSave, files, d, model);

    internal static GateCell R(long files, decimal d, string model, decimal rho, decimal delta, decimal alpha) => new(CellKind.Rescan, files, d, model, rho, delta, alpha);

    /// <summary>A named family's cell. The default is the matrix's: into the existing source.</summary>
    internal static GateCell Named(NameFamily family, long files, bool intoNewSource = false, bool rescan = false) =>
        new(CellKind.Family, files, Family: family, IntoNewSource: intoNewSource, FamilyRescan: rescan);

    /// <summary>A short label, the form of §15.4's matrix ("F(2M, 25%, system)").</summary>
    internal string Label
    {
        get
        {
            static string Size(long n) => n % 1_000_000 == 0 ? (n / 1_000_000).ToString(CultureInfo.InvariantCulture) + "M" : n % 1_000 == 0 ? (n / 1_000).ToString(CultureInfo.InvariantCulture) + "k" : n.ToString(CultureInfo.InvariantCulture);
            static string Pct(decimal v) => v.ToString("0.######", CultureInfo.InvariantCulture) + "%";
            return Kind switch
            {
                CellKind.FirstSave => $"F({Size(Files)}, {Pct(D)}, {Model})",
                CellKind.Rescan => $"R({Size(Files)}, {Pct(D)}, {Model}; {Pct(Rho)}, {Pct(Delta)}, {Pct(Alpha)})",
                _ => $"{Family}{(FamilyRescan ? " re-scan" : "")}{(IntoNewSource ? " into a new source" : "")} {Size(Files)}",
            };
        }
    }
}

/// <summary>The thrown form of "not a valid cell" (d = 0, a ratio outside its range, rho + delta above 100%, an unlisted family form).</summary>
internal sealed class InvalidCellException(string message) : Exception(message);

/// <summary>
/// §15.4's workload classes as a TOTAL function of a cell's numeric parameters (D-53; C4 design final repair C4DRR-M01), ported from
/// <c>docs/evidence/c4-design-review/workload_class.py</c> and the band table of §15.4. The class is decided in this order and every
/// VALID cell has exactly one; an invalid cell THROWS <see cref="InvalidCellException"/> (never silently unclassified, never a lookup
/// failure):
/// <list type="number">
/// <item>Informational: a named informational family (N1 at any size) or any cell of more than 2M files.</item>
/// <item>Worst-case: a named adversarial family: N2 or N3 into an existing source, or a re-scan of an N2 source.</item>
/// <item>Otherwise the hardest band among the generator parameters (F: its d; R: the source's d and the three churn ratios).</item>
/// </list>
/// <para><b>The family list is CLOSED (C4DRRR-O01).</b> The valid family forms are exactly: N1 into any source (informational: its
/// classification does not depend on the target); N2 and N3 into an EXISTING source; the re-scan of an N2 source. The seam the
/// final recheck found (N2 or N3 into a NEW source, a re-scan of an N1 source, a re-scan of an N3 source) is resolved by its first
/// option: those cells are rejected as not valid cells, exactly as d = 0 is, rather than widening step 2 to "any source". (The
/// Python reference classifies N2 or N3 into any source as worst-case and raises KeyError on an unlisted family; the C# port is
/// stricter, and the golden sweep compares only the valid forms.)</para>
/// </summary>
internal static class WorkloadClassifier
{
    internal const long TwoMillion = 2_000_000;

    /// <summary>The band bounds of §15.4's table, in percent: (upper bound of Representative, upper bound of Stress); above the second
    /// is Worst-case. Every band is a half-open interval (lo, hi] (or [0, hi] for the churn ratios), so the bands are disjoint and
    /// cover the parameter's range.</summary>
    private static (decimal Representative, decimal Stress) DBounds(string model) => model switch
    {
        "system" => (25m, 60m),    // 0 < d <= 25% < d <= 60% < d <= 100%
        "data" => (0m, 60m),       // no representative band: 0 < d <= 60% < d <= 100%
        _ => throw new InvalidCellException($"unknown name model '{model}'"),
    };

    private static readonly (decimal Representative, decimal Stress) RhoBounds = (1m, 10m);
    private static readonly (decimal Representative, decimal Stress) DeltaBounds = (0.5m, 5m);
    private static readonly (decimal Representative, decimal Stress) AlphaBounds = (0.5m, 5m);

    private static WorkloadClass Band(decimal value, (decimal Representative, decimal Stress) bounds) =>
        value <= bounds.Representative ? WorkloadClass.Representative : value <= bounds.Stress ? WorkloadClass.Stress : WorkloadClass.WorstCase;

    /// <summary>Throws <see cref="InvalidCellException"/> when the cell is not a valid cell.</summary>
    internal static void Validate(GateCell cell)
    {
        if (cell.Files <= 0) throw new InvalidCellException("a cell needs at least one file");
        switch (cell.Kind)
        {
            case CellKind.Family:
                if (cell.Family is null) throw new InvalidCellException("a family cell names its family");
                if (cell.D != 0 || cell.Model is not null || cell.Rho != 0 || cell.Delta != 0 || cell.Alpha != 0)
                    throw new InvalidCellException("a family cell carries no generator parameters");
                var ok = cell.Family switch
                {
                    NameFamily.N1 => !cell.FamilyRescan,                          // N1 into any source; no re-scan of an N1 source
                    NameFamily.N2 => !cell.IntoNewSource,                         // N2 into an existing source, or the re-scan of an N2 source (existing by definition)
                    NameFamily.N3 => !cell.IntoNewSource && !cell.FamilyRescan,   // N3 into an existing source only
                    _ => false,
                };
                if (!ok) throw new InvalidCellException($"'{cell.Label}' is not a named family cell of §15.4 (N1 into any source; N2 or N3 into an existing source; the re-scan of an N2 source)");
                break;
            case CellKind.FirstSave:
            case CellKind.Rescan:
                if (cell.Family is not null || cell.IntoNewSource || cell.FamilyRescan) throw new InvalidCellException("a generator cell carries no family");
                if (cell.Model is not ("system" or "data")) throw new InvalidCellException($"unknown name model '{cell.Model}'");
                if (!(cell.D > 0 && cell.D <= 100)) throw new InvalidCellException($"d = {cell.D}% is outside (0, 100%]");
                if (cell.Kind == CellKind.FirstSave)
                {
                    if (cell.Rho != 0 || cell.Delta != 0 || cell.Alpha != 0) throw new InvalidCellException("a first save has no churn ratios");
                }
                else
                {
                    foreach (var (name, v) in new[] { ("rho", cell.Rho), ("delta", cell.Delta), ("alpha", cell.Alpha) })
                        if (!(v >= 0 && v <= 100)) throw new InvalidCellException($"{name} = {v}% is outside [0, 100%]");
                    if (cell.Rho + cell.Delta > 100) throw new InvalidCellException($"rho + delta = {cell.Rho + cell.Delta}% exceeds 100%");
                }
                break;
            default:
                throw new InvalidCellException("unknown cell kind");
        }
    }

    /// <summary>The class of a valid cell; throws <see cref="InvalidCellException"/> otherwise.</summary>
    internal static WorkloadClass Classify(GateCell cell)
    {
        Validate(cell);
        // 1. informational: a named informational family at any size, or any cell above 2M files
        if ((cell.Kind == CellKind.Family && cell.Family == NameFamily.N1) || cell.Files > TwoMillion) return WorkloadClass.Informational;
        // 2. worst-case: a named adversarial family (valid forms only: N2 or N3 into an existing source, the re-scan of an N2 source)
        if (cell.Kind == CellKind.Family) return WorkloadClass.WorstCase;
        // 3. the hardest band among the parameters
        var hardest = Band(cell.D, DBounds(cell.Model!));
        if (cell.Kind == CellKind.Rescan)
        {
            hardest = (WorkloadClass)Math.Max((int)hardest, (int)Band(cell.Rho, RhoBounds));
            hardest = (WorkloadClass)Math.Max((int)hardest, (int)Band(cell.Delta, DeltaBounds));
            hardest = (WorkloadClass)Math.Max((int)hardest, (int)Band(cell.Alpha, AlphaBounds));
        }
        return hardest;
    }

    /// <summary>The single character of a class in the golden sweep file; 'X' for a rejected cell.</summary>
    internal static char Code(GateCell cell)
    {
        try
        {
            return Classify(cell) switch { WorkloadClass.Representative => 'R', WorkloadClass.Stress => 'S', WorkloadClass.WorstCase => 'W', _ => 'I' };
        }
        catch (InvalidCellException)
        {
            return 'X';
        }
    }

    /// <summary>The name of a class as §15.4's matrix writes it.</summary>
    internal static string Name(WorkloadClass c) => c switch
    {
        WorkloadClass.Representative => "Representative",
        WorkloadClass.Stress => "Stress",
        WorkloadClass.WorstCase => "Worst-case",
        _ => "Informational",
    };
}

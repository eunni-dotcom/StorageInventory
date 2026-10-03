using System.IO.Compression;
using StorageInventory.Testing;

namespace StorageInventory.Library.Tests.PerfGate;

/// <summary>
/// §15.4's workload classes as a total function of the cell's parameters (D-53; C4DRR-M01; C4DRRR-O01): the boundaries and the value
/// just above each, the invalid cells, the low-churn re-scans of 25%, 60% and 100%-distinct sources, the matrix's Class column, and a
/// sweep of 331,356 cells compared with the Python reference's stored verdicts.
/// </summary>
public static class WorkloadClassTests
{
    private const long M = 1_000_000;
    private const decimal Eps = 0.000001m;

    private static void Is(WorkloadClass expected, GateCell cell, string what) =>
        Assert.Equal(expected, WorkloadClassifier.Classify(cell), what + " (" + cell.Label + ")");

    [Test]
    public static void FirstSaveBoundariesOnBothNameModels()
    {
        // system: 0 < d <= 25% < d <= 60% < d <= 100%
        Is(WorkloadClass.Representative, GateCell.F(2 * M, Eps, "system"), "d = epsilon");
        Is(WorkloadClass.Representative, GateCell.F(2 * M, 25m, "system"), "d = 25%");
        Is(WorkloadClass.Stress, GateCell.F(2 * M, 25m + Eps, "system"), "d = 25% + epsilon");
        Is(WorkloadClass.Stress, GateCell.F(2 * M, 60m, "system"), "d = 60%");
        Is(WorkloadClass.WorstCase, GateCell.F(2 * M, 60m + Eps, "system"), "d = 60% + epsilon");
        Is(WorkloadClass.WorstCase, GateCell.F(2 * M, 100m, "system"), "d = 100%");
        // data: no representative band: 0 < d <= 60% < d <= 100%
        Is(WorkloadClass.Stress, GateCell.F(2 * M, Eps, "data"), "data d = epsilon");
        Is(WorkloadClass.Stress, GateCell.F(2 * M, 25m, "data"), "data d = 25%");
        Is(WorkloadClass.Stress, GateCell.F(2 * M, 60m, "data"), "data d = 60%");
        Is(WorkloadClass.WorstCase, GateCell.F(2 * M, 60m + Eps, "data"), "data d = 60% + epsilon");
        Is(WorkloadClass.WorstCase, GateCell.F(2 * M, 100m, "data"), "data d = 100%");
    }

    [Test]
    public static void RescanBoundariesOfEveryChurnRatio()
    {
        GateCell R(decimal rho = 1m, decimal delta = 0.5m, decimal alpha = 0.5m, decimal d = 25m, string model = "system") => GateCell.R(2 * M, d, model, rho, delta, alpha);
        Is(WorkloadClass.Representative, R(), "the routine re-scan");
        Is(WorkloadClass.Representative, R(0, 0, 0), "no churn");
        Is(WorkloadClass.Stress, R(rho: 1m + Eps), "rho = 1% + epsilon");
        Is(WorkloadClass.Stress, R(rho: 10m), "rho = 10%");
        Is(WorkloadClass.WorstCase, R(rho: 10m + Eps), "rho = 10% + epsilon");
        Is(WorkloadClass.Stress, R(delta: 0.5m + Eps), "delta = 0.5% + epsilon");
        Is(WorkloadClass.Stress, R(delta: 5m), "delta = 5%");
        Is(WorkloadClass.WorstCase, R(delta: 5m + Eps), "delta = 5% + epsilon");
        Is(WorkloadClass.Stress, R(alpha: 0.5m + Eps), "alpha = 0.5% + epsilon");
        Is(WorkloadClass.Stress, R(alpha: 5m), "alpha = 5%");
        Is(WorkloadClass.WorstCase, R(alpha: 5m + Eps), "alpha = 5% + epsilon");
    }

    [Test]
    public static void LowChurnRescansTakeTheirSourcesBand()
    {
        // a routine re-scan imports into the dictionary of its source, so the source's band counts (D-53)
        Is(WorkloadClass.Representative, GateCell.R(2 * M, 25m, "system", 1m, 0.5m, 0.5m), "25%-distinct system source");
        Is(WorkloadClass.Stress, GateCell.R(2 * M, 25m, "data", 1m, 0.5m, 0.5m), "25%-distinct data source");
        Is(WorkloadClass.Stress, GateCell.R(2 * M, 60m, "data", 1m, 0.5m, 0.5m), "60%-distinct data source");
        Is(WorkloadClass.Stress, GateCell.R(2 * M, 60m, "system", 1m, 0.5m, 0.5m), "60%-distinct system source");
        Is(WorkloadClass.WorstCase, GateCell.R(2 * M, 100m, "data", 1m, 0.5m, 0.5m), "100%-distinct source");
        Is(WorkloadClass.WorstCase, GateCell.R(2 * M, 100m, "system", 0, 0, 0), "100%-distinct source without churn");
    }

    [Test]
    public static void MixedCasesAreTheHardestBand()
    {
        Is(WorkloadClass.WorstCase, GateCell.R(2 * M, 25m, "system", 1m, 8m, 0.5m), "rho 1%, delta 8%");
        Is(WorkloadClass.Stress, GateCell.R(2 * M, 60m, "data", 10m, 5m, 5m), "60% data source at the stress churn bounds");
        Is(WorkloadClass.Stress, GateCell.R(2 * M, 25m, "system", 5m, 0.5m, 0.5m), "rho 5%, delta and alpha 0.5%");
        Is(WorkloadClass.WorstCase, GateCell.R(2 * M, 25m, "system", 5m, 2.5m, 6m), "alpha above its stress bound");
    }

    [Test]
    public static void InformationalComesFirst()
    {
        Is(WorkloadClass.Informational, GateCell.F(10 * M, 25m, "system"), "10M first save");
        Is(WorkloadClass.Informational, GateCell.F(2 * M + 1, 100m, "data"), "2M + 1 worst-case parameters");
        Is(WorkloadClass.Informational, GateCell.R(3 * M, 100m, "data", 100m, 0, 100m), "3M re-scan");
        Is(WorkloadClass.Informational, GateCell.Named(NameFamily.N1, 1 * M), "N1 at 1M");
        Is(WorkloadClass.Informational, GateCell.Named(NameFamily.N1, 10 * M, intoNewSource: true), "N1 into a new source");
        Is(WorkloadClass.Informational, GateCell.Named(NameFamily.N2, 10 * M), "N2 at 10M");
        Is(WorkloadClass.Representative, GateCell.F(100_000, 25m, "system"), "a smoke-sized cell is classified by its parameters");
        Is(WorkloadClass.Representative, GateCell.R(1 * M, 25m, "system", 1m, 0.5m, 0.5m), "R at 1M");
    }

    [Test]
    public static void NamedAdversarialFamiliesAreWorstCase()
    {
        Is(WorkloadClass.WorstCase, GateCell.Named(NameFamily.N2, 2 * M), "N2 into an existing source");
        Is(WorkloadClass.WorstCase, GateCell.Named(NameFamily.N3, 2 * M), "N3 into an existing source");
        Is(WorkloadClass.WorstCase, GateCell.Named(NameFamily.N2, 2 * M, rescan: true), "the 1% re-scan of an N2 source");
        Is(WorkloadClass.WorstCase, GateCell.Named(NameFamily.N2, 1_000), "N2 at a smoke size");
    }

    [Test]
    public static void InvalidCellsAreRejectedNotClassified()
    {
        void Rejected(GateCell cell, string what)
        {
            var ex = Assert.Throws<InvalidCellException>(() => WorkloadClassifier.Classify(cell));
            Assert.True(ex.Message.Length > 0, what);
            Assert.Equal('X', WorkloadClassifier.Code(cell), what);
        }
        Rejected(GateCell.F(2 * M, 0m, "system"), "d = 0");
        Rejected(GateCell.F(2 * M, -1m, "system"), "d < 0");
        Rejected(GateCell.F(2 * M, 100m + Eps, "data"), "d > 100%");
        Rejected(GateCell.R(2 * M, 25m, "system", 60m, 50m, 0), "rho + delta > 100%");
        Rejected(GateCell.R(2 * M, 25m, "system", 100m, Eps, 0), "rho + delta just above 100%");
        Rejected(GateCell.R(2 * M, 25m, "system", -Eps, 0, 0), "rho < 0");
        Rejected(GateCell.R(2 * M, 25m, "system", 0, 0, 100m + Eps), "alpha > 100%");
        Rejected(GateCell.F(2 * M, 25m, "mixed"), "an unknown name model");
        Rejected(GateCell.F(0, 25m, "system"), "no files");
        // the family list is CLOSED (C4DRRR-O01): the forms outside it are not valid cells, whatever their size
        Rejected(GateCell.Named(NameFamily.N2, 2 * M, intoNewSource: true), "N2 into a new source");
        Rejected(GateCell.Named(NameFamily.N3, 2 * M, intoNewSource: true), "N3 into a new source");
        Rejected(GateCell.Named(NameFamily.N1, 2 * M, rescan: true), "a re-scan of an N1 source");
        Rejected(GateCell.Named(NameFamily.N3, 2 * M, rescan: true), "a re-scan of an N3 source");
        Rejected(GateCell.Named(NameFamily.N2, 10 * M, intoNewSource: true), "N2 into a new source at 10M (never informational by default)");
        // and the classes accept d = 100% and rho + delta = 100% exactly
        Is(WorkloadClass.WorstCase, GateCell.R(2 * M, 100m, "data", 50m, 50m, 0), "rho + delta = 100%");
    }

    [Test]
    public static void MatrixCellsMapToTheClassColumn()
    {
        Assert.Equal(16, GateMatrix.Cells.Count, "the matrix's 14 rows are 16 cells (the N1 row names three sizes)");
        foreach (var cell in GateMatrix.Cells)
            Assert.Equal(GateMatrix.StatedClass[cell.Id], WorkloadClassifier.Name(cell.Class), cell.Id);
        Assert.Equal(4, GateMatrix.Cells.Count(c => c.Class == WorkloadClass.Representative), "the four representative cells (two first saves and two re-scans)");
        Assert.Equal(4, GateMatrix.Cells.Count(c => c.Class == WorkloadClass.Stress), "four stress cells");
        Assert.Equal(4, GateMatrix.Cells.Count(c => c.Class == WorkloadClass.WorstCase), "four worst-case cells");
        Assert.Equal(4, GateMatrix.Cells.Count(c => c.Class == WorkloadClass.Informational), "four informational cells");
        Assert.True(GateMatrix.Cells.All(c => c.Class == WorkloadClass.Informational || c.Budgets.Perf15CSeconds > 0), "every non-informational cell has a cancellation budget");
        Assert.Equal(4, GateMatrix.Cells.Count(c => c.Budgets.Perf01), "PERF-01 is judged on the four representative cells");
        Assert.True(GateMatrix.Cells.Where(c => c.Cell.Files == 2_000_000 && c.Class != WorkloadClass.Informational).All(c => c.Budgets.Perf14StopSeconds > 0), "the stop threshold applies in every 2M cell that has a class budget");
        Assert.Equal(90.0, GateMatrix.Find("F-2M-100-data").Budgets.Perf14StopSeconds, "worst-case stop threshold");
        Assert.Equal(67.5, GateMatrix.Find("R-2M-60-data-1-05-05").Budgets.Perf14StopSeconds, "stress stop threshold");
        Assert.True(!GateMatrix.Find("F-2M-100-data").Budgets.Perf14Target, "a worst-case cell has no PERF-14 target (it is bounded by the stop threshold)");
        // the scaled cell keeps the NOMINAL parameters, so a scaled run never changes a class
        var scaled = GateMatrix.Find("F-2M-100-data").Scaled(0.01);
        Assert.Equal(WorkloadClass.WorstCase, scaled.Class, "scaled F(2M, 100%, data) stays worst-case");
        Assert.Equal(20_000L, scaled.TargetFiles, "scaled size");
    }

    [Test]
    public static void SweepAgreesWithTheReference()
    {
        // the golden file holds the Python reference's class (workload_class.py, from the specification's own band table) for 331,356
        // cells in a fixed enumeration order (golden/make_class_sweep.py); every cell has exactly one class or is rejected
        var path = Path.Combine(FindRepoRoot(), "tests", "StorageInventory.Library.Tests", "PerfGate", "golden", "class-sweep.txt.gz");
        string text;
        using (var z = new GZipStream(File.OpenRead(path), CompressionMode.Decompress))
        using (var reader = new StreamReader(z, System.Text.Encoding.ASCII)) text = reader.ReadToEnd();
        var newline = text.IndexOf('\n');
        var header = text[..newline].Split(' ');
        var golden = text[(newline + 1)..];
        Assert.Equal(int.Parse(header[0], System.Globalization.CultureInfo.InvariantCulture), golden.Length, "golden length");
        Assert.Equal(header[1], Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(golden))).ToLowerInvariant(), "golden hash");

        long[] sizes = [100_000, 1_000_000, 2_000_000, 2_000_001, 3_000_000, 10_000_000];
        string[] models = ["system", "data"];
        decimal[] dValues = [.. "0 0.000001 1 10 24.999999 25 25.000001 30 40 50 59.999999 60 60.000001 70 80 90 99.999999 100 100.000001".Split(' ').Select(Dec)];
        decimal[] rhoValues = [.. "0 0.5 0.999999 1 1.000001 5 9.999999 10 10.000001 50 100 100.000001".Split(' ').Select(Dec)];
        decimal[] churnValues = [.. "0 0.25 0.499999 0.5 0.500001 2.5 4.999999 5 5.000001 50 100".Split(' ').Select(Dec)];

        var index = 0;
        var disagreements = new List<string>();
        var counts = new Dictionary<char, long>();
        void Check(GateCell cell)
        {
            var actual = WorkloadClassifier.Code(cell);
            counts[actual] = counts.GetValueOrDefault(actual) + 1;
            if (golden[index] != actual && disagreements.Count < 10) disagreements.Add($"#{index} {cell.Label}: reference {golden[index]}, port {actual}");
            index++;
        }
        foreach (var n in sizes) foreach (var model in models) foreach (var d in dValues) Check(GateCell.F(n, d, model));
        foreach (var n in sizes)
            foreach (var model in models)
                foreach (var d in dValues)
                    foreach (var rho in rhoValues)
                        foreach (var delta in churnValues)
                            foreach (var alpha in churnValues) Check(GateCell.R(n, d, model, rho, delta, alpha));
        foreach (var family in new[] { NameFamily.N1, NameFamily.N2, NameFamily.N3 })
            foreach (var n in sizes)
                foreach (var intoNew in new[] { false, true })
                    foreach (var rescan in new[] { false, true }) Check(GateCell.Named(family, n, intoNew, rescan));
        Assert.Equal(golden.Length, index, "the sweep's size");
        Assert.True(disagreements.Count == 0, "the port disagrees with the reference: " + string.Join("; ", disagreements));
        foreach (var c in "RSWIX") Assert.True(counts.GetValueOrDefault(c) > 0, "the sweep covers " + c);
    }

    private static decimal Dec(string text) => decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture);

    internal static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "StorageInventory.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("the repository root was not found above " + AppContext.BaseDirectory);
    }
}

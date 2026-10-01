using System.Text;
using StorageInventory.Core;
using StorageInventory.Core.Reports;
using StorageInventory.Testing;

namespace StorageInventory.IntegrationTests;

/// <summary>
/// Behavioural parity with the PowerShell reference (the oracle), run in the repo-local PowerShell 7 on the SAME
/// trees. Files and Folders reports are compared row by row, every column, in order (ordering is part of the
/// specification: size descending, discovery order for ties). ScanErrors rows are compared by path, type and order;
/// messages are compared except for reparse-point descriptions, which intentionally differ (see
/// docs/native-parity-report.md).
/// </summary>
public static class ParityTests
{
    public sealed record ParityOutcome(int ReferenceExitCode, StorageScanResult Native, string ReferenceFolder, string WorkFolder);

    private static PhaseAFixture Fx => PhaseAFixture.Shared;

    /// <param name="shell">The reference shell: <see cref="TestEnvironment.ReferenceShell"/> unless given.</param>
    private static ParityOutcome RunBoth(string root, bool sort = true, string? shell = null)
    {
        TestEnvironment.RequirePwsh();
        // A fresh tree's first listing can show folder times NTFS has not yet propagated (see Snapshot); settle it so
        // both implementations observe the same values.
        Snapshot.Settle(root);
        var baseDir = Path.Combine(TestEnvironment.WorkRoot, "parity_" + Guid.NewGuid().ToString("N")[..6]);
        var psOut = Path.Combine(baseDir, "ps");
        var csOut = Path.Combine(baseDir, "cs");
        var args = new List<string> { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", TestEnvironment.ReferenceScript, "-RootPath", root, "-OutputPath", psOut, "-SkipExcel" };
        if (!sort) args.Add("-NoSort");
        var reference = TestEnvironment.Run(shell ?? TestEnvironment.ReferenceShell, [.. args]);
        var native = new InventoryScanner().Scan(new StorageScanOptions { RootPath = root, OutputPath = csOut, SortFiles = sort });
        return new ParityOutcome(reference.ExitCode, native, psOut, baseDir);
    }

    private static string ReferenceReport(string folder, string prefix) =>
        Directory.GetFiles(folder, prefix + "_*.csv").Single();

    private static void AssertIdenticalCsv(string referencePath, string nativePath, string what)
    {
        var expected = File.ReadAllText(referencePath, Encoding.UTF8).Split("\r\n");
        var actual = File.ReadAllText(nativePath, Encoding.UTF8).Split("\r\n");
        var differences = new List<string>();
        for (var i = 0; i < Math.Max(expected.Length, actual.Length) && differences.Count < 5; i++)
        {
            var e = i < expected.Length ? expected[i] : "<missing>";
            var a = i < actual.Length ? actual[i] : "<missing>";
            if (!string.Equals(e, a, StringComparison.Ordinal)) differences.Add($"line {i + 1}:\n   reference: {e}\n   native:    {a}");
        }
        Assert.True(differences.Count == 0, $"{what} differs from the reference ({expected.Length} vs {actual.Length} lines):\n" + string.Join("\n", differences));
    }

    private static void AssertEquivalentErrors(string referencePath, string nativePath)
    {
        var expected = ReportCsvReader.ReadAll(referencePath);
        var actual = ReportCsvReader.ReadAll(nativePath);
        Assert.Equal(expected.Count, actual.Count, "ScanErrors row count");
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i]["Path"], actual[i]["Path"], $"ScanErrors row {i + 1} Path");
            Assert.Equal(expected[i]["ErrorType"], actual[i]["ErrorType"], $"ScanErrors row {i + 1} ErrorType");
            if (expected[i]["ErrorType"] is "ReparsePointSkipped" or "ReparsePointFile" or "InvalidTimestamp")
            {
                // Same behaviour, different wording: link descriptions differ intentionally, and for invalid timestamps
                // PowerShell 7 reports its own null-expression message instead of .NET's (see the parity report).
                Assert.Equal(expected[i]["Message"].Split(':')[0], actual[i]["Message"].Split(':')[0], $"row {i + 1} message lead");
            }
            else
            {
                Assert.Equal(expected[i]["Message"], actual[i]["Message"], $"ScanErrors row {i + 1} Message");
            }
        }
    }

    private static void AssertFullParity(ParityOutcome o, bool expectComplete)
    {
        try { AssertFullParityCore(o, expectComplete); }
        finally { TestEnvironment.RemoveTree(o.WorkFolder); }
    }

    private static void AssertFullParityCore(ParityOutcome o, bool expectComplete)
    {
        Assert.Equal(expectComplete ? 0 : 2, o.ReferenceExitCode, "reference exit code (0 complete / 2 incomplete)");
        Assert.Equal(expectComplete ? ScanCompletionState.Complete : ScanCompletionState.Incomplete, o.Native.State);
        var reports = Assert.NotNull(o.Native.Reports);
        AssertIdenticalCsv(ReferenceReport(o.ReferenceFolder, "Files"), reports.FilesCsv, "Files report");
        AssertIdenticalCsv(ReferenceReport(o.ReferenceFolder, "Folders"), reports.FoldersCsv, "Folders report");
        AssertEquivalentErrors(ReferenceReport(o.ReferenceFolder, "ScanErrors"), reports.ErrorsCsv);
    }

    [Test]
    public static void Phase_A_adversarial_fixture_sorted_reports_are_identical()
        => AssertFullParity(RunBoth(Fx.Root), expectComplete: false);

    [Test]
    public static void Phase_A_adversarial_fixture_unsorted_reports_are_identical()
        => AssertFullParity(RunBoth(Fx.Root, sort: false), expectComplete: false);

    [Test]
    public static void Readable_subtree_is_complete_in_both()
        => AssertFullParity(RunBoth(Path.Combine(Fx.Root, "Kpop", "TWICE")), expectComplete: true);

    [Test]
    public static void Unicode_and_bracket_subtree_reports_are_identical()
        => AssertFullParity(RunBoth(Directory.GetDirectories(Fx.Root, "Weird*")[0]), expectComplete: true);

    [Test]
    public static void Real_file_reparse_points_are_treated_identically()
    {
        var aliases = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps");
        if (!Directory.Exists(aliases)) Assert.Skip("no WindowsApps folder");
        var o = RunBoth(aliases);
        AssertFullParity(o, expectComplete: o.ReferenceExitCode == 0);
    }

    [Test]
    public static void Ten_thousand_file_benchmark_tree_reports_are_identical()
    {
        var tree = Path.Combine(Path.GetTempPath(), "StorageInventoryPerf", "tree_10000");
        if (!Directory.Exists(tree)) Assert.Skip("benchmark tree %TEMP%\\StorageInventoryPerf\\tree_10000 not present");
        AssertFullParity(RunBoth(tree), expectComplete: true);
    }

    [Test]
    public static void Large_benchmark_trees_reports_are_identical_when_requested()
    {
        // Opt-in (slow: the reference takes about a minute at 250k files): SI_LARGE_PARITY=1.
        if (Environment.GetEnvironmentVariable("SI_LARGE_PARITY") != "1") Assert.Skip("set SI_LARGE_PARITY=1 to compare the 60k and 250k benchmark trees");
        foreach (var size in new[] { 60000, 250000 })
        {
            var tree = Path.Combine(Path.GetTempPath(), "StorageInventoryPerf", $"tree_{size}");
            if (!Directory.Exists(tree)) Assert.Skip($"benchmark tree {tree} not present");
            AssertFullParity(RunBoth(tree), expectComplete: true);
        }
    }

    [Test]
    public static void Blocked_output_inside_the_source_is_refused_by_both()
    {
        TestEnvironment.RequirePwsh();
        var inside = Path.Combine(Fx.Root, "InsideOut");
        var reference = TestEnvironment.Run(TestEnvironment.ReferenceShell, "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", TestEnvironment.ReferenceScript, "-RootPath", Fx.Root, "-OutputPath", inside);
        var native = new InventoryScanner().Scan(new StorageScanOptions { RootPath = Fx.Root, OutputPath = inside });
        Assert.True(reference.ExitCode != 0, "reference refuses");
        Assert.Equal(ScanFailureKind.InvalidPaths, native.Failure!.Kind);
        Assert.False(Directory.Exists(inside));
    }

    // v1.1 C1 (spec section 19): the refactored scanner must keep parity with BOTH PowerShell oracles. The suite runs
    // against the repo-local PowerShell 7 (above); these repeat the fixture comparisons against Windows PowerShell 5.1.

    private static string WindowsPowerShell()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
        if (!File.Exists(path)) Assert.Skip("Windows PowerShell 5.1 is not installed");
        return path;
    }

    [Test]
    public static void Windows_PowerShell_5_1_reference_agrees_on_the_Phase_A_fixture_sorted()
        => AssertFullParity(RunBoth(Fx.Root, shell: WindowsPowerShell()), expectComplete: false);

    [Test]
    public static void Windows_PowerShell_5_1_reference_agrees_on_the_Phase_A_fixture_unsorted()
        => AssertFullParity(RunBoth(Fx.Root, sort: false, shell: WindowsPowerShell()), expectComplete: false);

    [Test]
    public static void Windows_PowerShell_5_1_reference_agrees_on_the_readable_and_Unicode_subtrees()
    {
        AssertFullParity(RunBoth(Path.Combine(Fx.Root, "Kpop", "TWICE"), shell: WindowsPowerShell()), expectComplete: true);
        AssertFullParity(RunBoth(Directory.GetDirectories(Fx.Root, "Weird*")[0], shell: WindowsPowerShell()), expectComplete: true);
    }
}

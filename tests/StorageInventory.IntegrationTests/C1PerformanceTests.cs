using System.Globalization;
using StorageInventory.Testing;

namespace StorageInventory.IntegrationTests;

/// <summary>
/// C1 performance evidence on whatever machine runs the suite (CI included): the same generated tree, scanned in fresh
/// child processes, alternating the plain scan and the scan with the spool writer attached, sorted and unsorted.
/// Timings are printed, not asserted (shared runners are noisy); what is asserted is that the reports are identical
/// with and without the spool, that the spool verifies, and that it stays within its size budget (PERF-13).
/// </summary>
public static class C1PerformanceTests
{
    private const int Files = 25_000;
    private const int Rounds = 3;

    [Test]
    public static void Spool_overhead_and_size_are_measured_on_a_generated_tree()
    {
        var tree = Path.Combine(TestEnvironment.WorkRoot, $"c1_perf_tree_{Files}");
        try
        {
            Benchmarks.EnsureTree(tree, Files);
            Snapshot.Settle(tree);
            var results = new Dictionary<(bool Sort, bool Spool), List<Benchmarks.One>>();
            for (var round = 0; round < Rounds; round++)
            {
                foreach (var sort in new[] { true, false })
                {
                    foreach (var spool in new[] { false, true })
                    {
                        var o = Benchmarks.RunChild(tree, sort, spool);
                        Assert.NotNull(o, "benchmark child failed");
                        (results.TryGetValue((sort, spool), out var list) ? list : results[(sort, spool)] = []).Add(o!);
                    }
                }
            }

            Console.WriteLine($"      C1 overhead, {Files:N0} files, median of {Rounds} fresh processes per row:");
            foreach (var ((sort, spool), runs) in results.OrderBy(k => !k.Key.Sort).ThenBy(k => k.Key.Spool))
            {
                var median = runs.OrderBy(r => r.TotalMs).ElementAt(runs.Count / 2);
                Console.WriteLine("      " + Benchmarks.Row(median, sort, spool));
            }
            foreach (var sort in new[] { true, false })
            {
                double Median(bool spool, Func<Benchmarks.One, double> f) => results[(sort, spool)].Select(f).Order().ElementAt(Rounds / 2);
                var enumeration = Median(true, r => r.EnumerationMs) / Median(false, r => r.EnumerationMs);
                var total = Median(true, r => r.TotalMs) / Median(false, r => r.TotalMs);
                var peak = (Median(true, r => r.PeakWorkingSetBytes) - Median(false, r => r.PeakWorkingSetBytes)) / 1048576.0;
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "      {0}: spool/plain enumeration {1:0.000}x, total {2:0.000}x, peak working set {3:+0.0;-0.0} MB",
                    sort ? "sorted" : "unsorted", enumeration, total, peak));
            }

            foreach (var ((sort, spool), runs) in results)
            {
                foreach (var r in runs)
                {
                    Assert.Equal("Complete", r.State, $"{sort}/{spool}");
                    Assert.Equal((long)Files, r.Files);
                    Assert.Equal(results[(sort, false)][0].ReportsHash, r.ReportsHash, "reports are identical with and without the spool");
                    if (!spool) continue;
                    Assert.Equal("Sealed", r.SpoolState);
                    Assert.Equal("Passed", r.Verify);
                    Assert.True(r.SpoolBytes / (double)r.Files <= 120, $"PERF-13: {r.SpoolBytes / (double)r.Files:0.0} bytes per file");
                }
            }
        }
        finally { TestEnvironment.RemoveTree(tree); }
    }
}

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using StorageInventory.Core;

namespace StorageInventory.IntegrationTests;

/// <summary>
/// Native benchmarks (TEST CODE ONLY): `--benchmark [sizes]` runs each scan in a fresh child process
/// (`--benchmark-one tree sorted|unsorted output`) so the peak working set is measured by the process itself, exactly,
/// rather than sampled. Uses the synthetic trees in %TEMP%\StorageInventoryPerf. Results are measurements on this
/// machine for these trees only.
/// </summary>
public static class Benchmarks
{
    public sealed record One(long Files, long Folders, string State, double EnumerationMs, double AggregationMs, double FoldersReportMs,
        double SortingMs, double FilesReportMs, double TotalMs, long PeakWorkingSetBytes, long FilesCsvBytes);

    public static int RunOne(string tree, bool sort, string output)
    {
        var result = new InventoryScanner().Scan(new StorageScanOptions { RootPath = tree, OutputPath = output, SortFiles = sort });
        var t = result.Timings;
        var one = new One(result.Totals.Files, result.Totals.Folders, result.State.ToString(), t.Enumeration.TotalMilliseconds, t.Aggregation.TotalMilliseconds,
            t.FoldersReport.TotalMilliseconds, t.Sorting.TotalMilliseconds, t.FilesReport.TotalMilliseconds, t.Total.TotalMilliseconds,
            Process.GetCurrentProcess().PeakWorkingSet64, result.Reports is { } r ? new FileInfo(r.FilesCsv).Length : 0);
        Console.WriteLine("BENCH " + JsonSerializer.Serialize(one));
        return result.Finished ? 0 : 1;
    }

    public static int Run(string sizesArgument)
    {
        var perf = Path.Combine(Path.GetTempPath(), "StorageInventoryPerf");
        var sizes = sizesArgument.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToList();
        var self = Environment.ProcessPath!;
        var dll = typeof(Benchmarks).Assembly.Location;
        Console.WriteLine($"Native benchmark, {RuntimeDescription()}");
        Console.WriteLine($"{"Files",9} {"Mode",-8} {"Enum s",8} {"Aggr s",7} {"Fold s",7} {"Sort+copy s",11} {"Total s",8} {"Peak WS MB",10} {"µs/file",8} {"CSV MB",7}");
        foreach (var size in sizes)
        {
            var tree = Path.Combine(perf, $"tree_{size}");
            if (!Directory.Exists(tree)) { Console.WriteLine($"{size,9}  (tree missing: {tree})"); continue; }
            Snapshot.Settle(tree);   // full warm-up walk: every measured run sees the same warm cache
            foreach (var sort in new[] { true, false })
            {
                var output = Path.Combine(TestEnvironment.WorkRoot, $"bench_{size}_{(sort ? "sorted" : "unsorted")}_{Guid.NewGuid().ToString("N")[..6]}");
                // Same host as this process: `dotnet exec <dll>` when launched that way, else the apphost.
                var args = self.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase)
                    ? new[] { "exec", dll, "--benchmark-one", tree, sort ? "sorted" : "unsorted", output }
                    : ["--benchmark-one", tree, sort ? "sorted" : "unsorted", output];
                var r = TestEnvironment.Run(self, args);
                var line = r.StdOut.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("BENCH ", StringComparison.Ordinal));
                if (line is null) { Console.WriteLine($"{size,9}  failed: {r.All}"); continue; }
                var o = JsonSerializer.Deserialize<One>(line[6..])!;
                Console.WriteLine($"{o.Files,9:N0} {(sort ? "Sorted" : "NoSort"),-8} {o.EnumerationMs / 1000,8:0.00} {o.AggregationMs / 1000,7:0.00} {o.FoldersReportMs / 1000,7:0.00} {o.SortingMs / 1000,11:0.00} {o.TotalMs / 1000,8:0.00} {o.PeakWorkingSetBytes / 1048576.0,10:0} {o.TotalMs * 1000 / Math.Max(1, o.Files),8:0} {o.FilesCsvBytes / 1048576.0,7:0.0}");
                TestEnvironment.RemoveTree(output);
            }
        }
        return 0;
    }

    private static string RuntimeDescription() =>
        $"{System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}, {Environment.ProcessorCount} logical CPUs";
}

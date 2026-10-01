using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using StorageInventory.Core;
using StorageInventory.Core.Spool;

namespace StorageInventory.IntegrationTests;

/// <summary>
/// Native benchmarks (TEST CODE ONLY): `--benchmark [sizes]` runs each scan in a fresh child process
/// (`--benchmark-one tree sorted|unsorted output [spool]`) so the peak working set is measured by the process itself,
/// exactly, rather than sampled. Uses the synthetic trees in %TEMP%\StorageInventoryPerf. Results are measurements on
/// this machine for these trees only. The `spool` variant (C1) runs the same scan with the observation-spool writer
/// attached as an isolated observer, writing to a file this harness creates beside the output folder, then runs the
/// verification pass V on it.
/// </summary>
public static class Benchmarks
{
    public sealed record One(long Files, long Folders, string State, double EnumerationMs, double AggregationMs, double FoldersReportMs,
        double SortingMs, double FilesReportMs, double TotalMs, long PeakWorkingSetBytes, long FilesCsvBytes,
        string ReportsHash = "", long SpoolBytes = 0, string SpoolState = "", double VerifyMs = 0, string Verify = "");

    public static int RunOne(string tree, bool sort, string output, bool spool = false)
    {
        var options = new StorageScanOptions { RootPath = tree, OutputPath = output, SortFiles = sort };
        StorageScanResult result;
        long spoolBytes = 0;
        string spoolState = "", verify = "";
        double verifyMs = 0;
        if (!spool)
        {
            result = new InventoryScanner().Scan(options);
        }
        else
        {
            var spoolFolder = output + "_spool";
            Directory.CreateDirectory(spoolFolder);
            var token = RandomNumberGenerator.GetBytes(16);
            using var file = new FileStream(Path.Combine(spoolFolder, "bench.spool"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                4096, FileOptions.DeleteOnClose);
            using var writer = new SpoolWriter(file, token);
            result = new InventoryScanner().ScanObserved(options, [writer]).Scan;
            spoolBytes = file.Length;
            spoolState = writer.State.ToString();
            var clock = Stopwatch.StartNew();
            var v = SpoolReader.Verify(file, token, result.Totals);
            verifyMs = clock.Elapsed.TotalMilliseconds;
            verify = v.Passed ? "Passed" : v.Defect.ToString();
        }
        var t = result.Timings;
        var hash = result.Reports is { } reports
            ? Convert.ToHexString(SHA256.HashData([.. File.ReadAllBytes(reports.FilesCsv), .. File.ReadAllBytes(reports.FoldersCsv), .. File.ReadAllBytes(reports.ErrorsCsv)]))[..16]
            : "";
        var one = new One(result.Totals.Files, result.Totals.Folders, result.State.ToString(), t.Enumeration.TotalMilliseconds, t.Aggregation.TotalMilliseconds,
            t.FoldersReport.TotalMilliseconds, t.Sorting.TotalMilliseconds, t.FilesReport.TotalMilliseconds, t.Total.TotalMilliseconds,
            Process.GetCurrentProcess().PeakWorkingSet64, result.Reports is { } r ? new FileInfo(r.FilesCsv).Length : 0,
            hash, spoolBytes, spoolState, verifyMs, verify);
        Console.WriteLine("BENCH " + JsonSerializer.Serialize(one));
        return result.Finished ? 0 : 1;
    }

    /// <summary>Runs one measured scan in a fresh child process (the same host as this process).</summary>
    public static One? RunChild(string tree, bool sort, bool spool)
    {
        var self = Environment.ProcessPath!;
        var dll = typeof(Benchmarks).Assembly.Location;
        var output = Path.Combine(TestEnvironment.WorkRoot, $"bench_{Path.GetFileName(tree)}_{(sort ? "sorted" : "unsorted")}{(spool ? "_spool" : "")}_{Guid.NewGuid().ToString("N")[..6]}");
        string[] tail = ["--benchmark-one", tree, sort ? "sorted" : "unsorted", output, .. spool ? new[] { "spool" } : []];
        // Same host as this process: `dotnet exec <dll>` when launched through dotnet, else the apphost.
        var args = string.Equals(Path.GetFileNameWithoutExtension(self), "dotnet", StringComparison.OrdinalIgnoreCase) ? ["exec", dll, .. tail] : tail;
        try
        {
            var r = TestEnvironment.Run(self, args);
            var line = r.StdOut.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("BENCH ", StringComparison.Ordinal));
            if (line is null) { Console.WriteLine("      benchmark run failed: " + r.All); return null; }
            return JsonSerializer.Deserialize<One>(line[6..]);
        }
        finally
        {
            TestEnvironment.RemoveTree(output);
            TestEnvironment.RemoveTree(output + "_spool");
        }
    }

    /// <summary>The benchmark tree shape of powershell/tests (New-BulkFixture): 40 files per album, 25 albums per artist,
    /// sizes 0-599 bytes from Random(42). Built only if missing.</summary>
    public static void EnsureTree(string root, int files)
    {
        if (Directory.Exists(root)) return;
        var rng = new Random(42);
        var artists = Math.Max(1, (int)Math.Ceiling(files / 1000.0));
        for (var a = 0; a < artists; a++)
        {
            for (var b = 0; b < 25; b++)
            {
                var d = Path.Combine(root, $"Artist{a}", $"Album{b}");
                Directory.CreateDirectory(d);
                for (var t = 0; t < 40; t++)
                {
                    using var fs = File.Create(Path.Combine(d, $"track{t}.mp3"));
                    fs.SetLength(rng.Next(0, 600));
                }
            }
        }
    }

    public static int Run(string sizesArgument)
    {
        var perf = Path.Combine(Path.GetTempPath(), "StorageInventoryPerf");
        var sizes = sizesArgument.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToList();
        Console.WriteLine($"Native benchmark, {RuntimeDescription()}");
        Console.WriteLine($"{"Files",9} {"Mode",-14} {"Enum s",8} {"Aggr s",7} {"Fold s",7} {"Sort+copy s",11} {"Total s",8} {"Peak WS MB",10} {"µs/file",8} {"CSV MB",7} {"Spool B/file",12} {"V s",6}");
        foreach (var size in sizes)
        {
            var tree = Path.Combine(perf, $"tree_{size}");
            if (!Directory.Exists(tree)) { Console.WriteLine($"{size,9}  (tree missing: {tree})"); continue; }
            Snapshot.Settle(tree);   // full warm-up walk: every measured run sees the same warm cache
            foreach (var sort in new[] { true, false })
            {
                foreach (var spool in new[] { false, true })
                {
                    var o = RunChild(tree, sort, spool);
                    if (o is null) continue;
                    Console.WriteLine(Row(o, sort, spool));
                }
            }
        }
        return 0;
    }

    public static string Row(One o, bool sort, bool spool) =>
        $"{o.Files,9:N0} {(sort ? "Sorted" : "NoSort") + (spool ? "+Spool" : ""),-14} {o.EnumerationMs / 1000,8:0.00} {o.AggregationMs / 1000,7:0.00} {o.FoldersReportMs / 1000,7:0.00} {o.SortingMs / 1000,11:0.00} {o.TotalMs / 1000,8:0.00} {o.PeakWorkingSetBytes / 1048576.0,10:0} {o.TotalMs * 1000 / Math.Max(1, o.Files),8:0} {o.FilesCsvBytes / 1048576.0,7:0.0} {(spool ? (o.SpoolBytes / (double)Math.Max(1, o.Files)).ToString("0.0", CultureInfo.InvariantCulture) : "-"),12} {(spool ? (o.VerifyMs / 1000).ToString("0.00", CultureInfo.InvariantCulture) : "-"),6}";

    private static string RuntimeDescription() =>
        $"{System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}, {Environment.ProcessorCount} logical CPUs";
}

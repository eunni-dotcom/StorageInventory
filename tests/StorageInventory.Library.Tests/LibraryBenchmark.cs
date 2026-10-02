using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using StorageInventory.Core;

namespace StorageInventory.Library.Tests;

/// <summary>
/// TEST-P1 (§15.4, Q-17, Q-07): the engine benchmark. One-transaction imports of 1M, 2M and 10M file rows in key order with the SHIPPED
/// engine (SQLite 3.53.3 through <c>e_sqlite3.dll</c>) on Windows, using the FULL schema with dictionary interning (<c>name</c>,
/// <c>folder_path</c>), into a Library that ALREADY holds other snapshots and other sources (G0-O03): duration, throughput, peak
/// journal, peak working set and database growth; deletion of a 2M-file snapshot; the rollback when the database fills
/// (<c>SQLITE_FULL</c>); and Q-07, the same import with and without the optional foreign keys on the observation tables.
/// <para>Method (§15.4): a fresh process per run, the Library on the same SSD as the temporary folder, <c>PeakWorkingSet64</c> read in
/// process. The synthetic row source is deterministic and held in memory, so its own arrays (reported) are part of the working set;
/// the figures below subtract the working set measured just before the import starts. Run it with
/// <c>StorageInventory.Library.Tests.exe --benchmark all docs\benchmarks\library.md</c> (Release).</para>
/// </summary>
internal static class LibraryBenchmark
{
    internal sealed record Measurement(string Label, long TargetFiles, bool ForeignKeys, long PrefillFiles, long Files, long Folders, long NewNames,
        double ImportSeconds, double FilesPerSecond, double RowsPerSecond, long PeakJournalBytes, long DatabaseBytesBefore, long DatabaseBytesAfter,
        long WorkingSetBeforeBytes, long PeakWorkingSetBytes, double VerifySeconds, string? VerifyResult,
        double DeleteSeconds, long DeletePeakJournalBytes, string? FailureKind, double RollbackSeconds, long DatabaseBytesAfterFailure, bool OlderSnapshotsIntact);

    internal static int Run(string[] args)
    {
        if (args is ["child", ..]) return Child(args[1..]);
        var which = args.Length > 0 ? args[0] : "quick";
        if (which == "micro") return EngineMicroBenchmark.Run();
        var output = args.Length > 1 ? args[1] : null;
        return Orchestrate(which, output);
    }

    // ------------------------------------------------------------------------------------------------------------ the parent

    private static int Orchestrate(string which, string? output)
    {
        var runs = new List<(string Label, string[] Args)>();
        string[] Common(long files, bool fk, long prefill, string limit, bool delete) =>
            ["--files", files.ToString(CultureInfo.InvariantCulture), "--fk", fk ? "1" : "0", "--prefill", prefill.ToString(CultureInfo.InvariantCulture), "--limit", limit, "--delete", delete ? "1" : "0"];

        switch (which)
        {
            case "quick":
                runs.Add(("quick 200k", Common(200_000, false, 100_000, "none", true)));
                break;
            case "p1":
                foreach (var files in new long[] { 1_000_000, 2_000_000, 10_000_000 }) runs.Add(($"import {files / 1_000_000}M", Common(files, false, 250_000, "none", files == 2_000_000)));
                break;
            case "q07":
                for (var round = 0; round < 3; round++)
                {
                    runs.Add(($"2M, no foreign keys (round {round + 1})", Common(2_000_000, false, 250_000, "none", false)));
                    runs.Add(($"2M, foreign keys declared (round {round + 1})", Common(2_000_000, true, 250_000, "none", false)));
                }
                break;
            case "full":
                runs.Add(("2M, SQLITE_FULL at 80 MiB", Common(2_000_000, false, 100_000, "full", false)));
                break;
            case "all":
                for (var round = 1; round <= 3; round++) runs.Add(($"import 1M (run {round})", Common(1_000_000, false, 250_000, "none", false)));
                for (var round = 1; round <= 3; round++) runs.Add(($"import 2M (run {round})", Common(2_000_000, false, 250_000, "none", true)));
                runs.Add(("import 10M", Common(10_000_000, false, 250_000, "none", false)));
                for (var round = 0; round < 3; round++)
                {
                    runs.Add(($"2M, no foreign keys (round {round + 1})", Common(2_000_000, false, 250_000, "none", false)));
                    runs.Add(($"2M, foreign keys declared (round {round + 1})", Common(2_000_000, true, 250_000, "none", false)));
                }
                runs.Add(("2M, SQLITE_FULL at 80 MiB", Common(2_000_000, false, 100_000, "full", false)));
                break;
            default:
                Console.Error.WriteLine("usage: --benchmark quick|p1|q07|full|all [output.md]");
                return 2;
        }

        var results = new List<Measurement>();
        foreach (var (label, arguments) in runs)
        {
            Console.WriteLine($"=== {label}");
            var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var a in new[] { "--benchmark", "child" }.Concat(arguments).Concat(["--label", label])) info.ArgumentList.Add(a);
            using var process = Process.Start(info)!;
            var stderr = process.StandardError.ReadToEndAsync();
            string? json = null;
            while (process.StandardOutput.ReadLine() is { } line)
            {
                if (line.StartsWith('{')) json = line;
                else Console.WriteLine("  " + line);
            }
            process.WaitForExit();
            if (process.ExitCode != 0 || json is null)
            {
                Console.Error.WriteLine($"FAILED ({process.ExitCode}): {stderr.Result}");
                return 1;
            }
            var measurement = JsonSerializer.Deserialize<Measurement>(json)!;
            results.Add(measurement);
            Console.WriteLine($"  {measurement.Files:N0} files, {measurement.ImportSeconds:0.00} s, {measurement.FilesPerSecond:N0} file rows/s, peak journal {measurement.PeakJournalBytes / 1048576.0:0.0} MB, DB {measurement.DatabaseBytesAfter / 1048576.0:0.0} MB");
        }
        var markdown = Markdown(results);
        Console.WriteLine();
        Console.WriteLine(markdown);
        if (output is not null) File.WriteAllText(output, markdown);
        return 0;
    }

    private static string Markdown(List<Measurement> results)
    {
        var text = new StringBuilder();
        text.AppendLine("| Run | File rows | Folder rows | Import (BEGIN to COMMIT) | File rows/s | Peak journal | Journal / snapshot growth | DB before | DB after | Growth per file row | Peak working set above the baseline |");
        text.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var m in results.Where(r => r.FailureKind is null))
        {
            var growth = m.DatabaseBytesAfter - m.DatabaseBytesBefore;
            text.AppendLine($"| {m.Label} | {m.Files:N0} | {m.Folders:N0} | {m.ImportSeconds:0.00} s | {m.FilesPerSecond:N0} | {m.PeakJournalBytes / 1048576.0:0.00} MB | {(growth == 0 ? 0 : 100.0 * m.PeakJournalBytes / growth):0.000}% | {m.DatabaseBytesBefore / 1048576.0:0.0} MB | {m.DatabaseBytesAfter / 1048576.0:0.0} MB | {(double)growth / m.Files:0.0} B | {(m.PeakWorkingSetBytes - m.WorkingSetBeforeBytes) / 1048576.0:0} MB |");
        }
        var deletes = results.Where(r => r.DeleteSeconds > 0).ToList();
        if (deletes.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("| Deletion (T-DELETE of the snapshot just imported) | File rows | Duration | Peak journal | Verification of the committed snapshot (invariants 1 to 12) |");
            text.AppendLine("|---|---:|---:|---:|---|");
            foreach (var m in deletes) text.AppendLine($"| {m.Label} | {m.Files:N0} | {m.DeleteSeconds:0.00} s | {m.DeletePeakJournalBytes / 1048576.0:0.0} MB | {m.VerifySeconds:0.0} s, {m.VerifyResult ?? "all hold"} |");
        }
        var failures = results.Where(r => r.FailureKind is not null).ToList();
        if (failures.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("| Failure run | File rows attempted | Failure classified as | Time to fail and roll back | Database size before / after the failed import | Older snapshots intact |");
            text.AppendLine("|---|---:|---|---:|---|---|");
            foreach (var m in failures) text.AppendLine($"| {m.Label} | {m.TargetFiles:N0} | {m.FailureKind} | {m.RollbackSeconds:0.00} s (rollback alone) | {m.DatabaseBytesBefore / 1048576.0:0.0} MB / {m.DatabaseBytesAfterFailure / 1048576.0:0.0} MB | {(m.OlderSnapshotsIntact ? "yes" : "NO")} |");
        }
        return text.ToString();
    }

    // ------------------------------------------------------------------------------------------------------------ the child

    private static int Child(string[] args)
    {
        string Option(string name, string fallback) { var i = Array.IndexOf(args, "--" + name); return i >= 0 ? args[i + 1] : fallback; }
        Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;   // a busy workstation: less noise, still no elevation needed
        var label = Option("label", "run");
        var files = long.Parse(Option("files", "1000000"), CultureInfo.InvariantCulture);
        var fk = Option("fk", "0") == "1";
        var prefill = long.Parse(Option("prefill", "100000"), CultureInfo.InvariantCulture);
        var limit = Option("limit", "none");
        var delete = Option("delete", "0") == "1";

        var root = Path.Combine(Path.GetTempPath(), "SI-Library-Bench", Guid.NewGuid().ToString("N")[..10]);
        var appData = Path.Combine(root, "AppData");
        var directory = Path.Combine(appData, "Library");
        Directory.CreateDirectory(appData);
        var main = Path.Combine(directory, LibraryNames.MainFile);
        var journal = Path.Combine(directory, LibraryNames.JournalFile);
        try
        {
            var session = new LibrarySession(directory, appData);
            session.RunStartupOpen();
            using (var create = Lease(session, MutationKind.Create)) session.CreateLibrary(create);
            if (fk) DeclareForeignKeys(main);

            // the Library already holds other snapshots AND another source (G0-O03): source A twice, source B once
            long Folders(long n) => Math.Max(10, n / 4);
            ImportSourceSpec? sourceA = null;
            for (var i = 1; i <= 3; i++)
            {
                var other = i == 2;
                var snapshot = new SyntheticSnapshot((int)Folders(prefill), seed: i, label: "p" + i);
                var spec = other ? SyntheticSnapshot.NewSource(@"\Other", serial: 0x7777ABCD) : sourceA ?? SyntheticSnapshot.NewSource();
                var result = Import(session, snapshot, "prefill-" + i, spec, null, out _);
                if (i == 1) sourceA = new ImportSourceSpec.Existing(result.SourceId);
            }
            Console.WriteLine($"prefilled: 3 snapshots of about {prefill:N0} files in 2 sources; database {new FileInfo(main).Length / 1048576.0:0.0} MB");

            var target = new SyntheticSnapshot((int)Folders(files), seed: 4, label: "target");
            Console.WriteLine($"synthetic target: {target.Files:N0} files in {target.FolderCount:N0} folders");
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var dbBefore = new FileInfo(main).Length;
            var wsBefore = Process.GetCurrentProcess().WorkingSet64;
            // the measured import runs in the session that did the prefill, except for the SQLITE_FULL run, which needs the engine's own
            // size limit and so opens a session with it. (The foreign-key variant edits the schema, so a re-open would refuse it.)
            var measured = session;
            if (limit == "full")
            {
                session.TestOnlyShutdown();
                measured = new LibrarySession(directory, appData, new LibrarySessionOptions { Faults = new LibraryFaultInjection { LimitDatabaseToTwentyThousandPages = true } });
                var status = measured.RunStartupOpen();
                if (status.State != LibraryState.Available) throw new InvalidOperationException("not available: " + status.Message);
            }
            var peakJournal = 0L;
            using var stopSampling = new CancellationTokenSource();
            var sampler = Task.Run(() =>
            {
                while (!stopSampling.IsCancellationRequested)
                {
                    var length = File.Exists(journal) ? new FileInfo(journal).Length : 0;
                    if (length > peakJournal) peakJournal = length;
                    Thread.Sleep(5);
                }
            });

            string? failureKind = null;
            double importSeconds = 0, rollbackSeconds = 0;
            ImportResult? imported = null;
            var gcPauseBefore = GC.GetTotalPauseDuration();
            var gen2Before = GC.CollectionCount(2);
            var gen0Before = GC.CollectionCount(0);
            var stopwatch = Stopwatch.StartNew();
            try
            {
                imported = Import(measured, target, "target", sourceA!, null, out importSeconds);
            }
            catch (ImportException ex)
            {
                failureKind = $"{ex.Kind} ({(int)ex.Kind})";
                rollbackSeconds = stopwatch.Elapsed.TotalSeconds;   // time to fail, including the rollback, since Import returns after it
                importSeconds = rollbackSeconds;
            }
            var wall = stopwatch.Elapsed.TotalSeconds;
            Console.WriteLine($"GC during the import: pause {(GC.GetTotalPauseDuration() - gcPauseBefore).TotalMilliseconds:0} ms, gen0 {GC.CollectionCount(0) - gen0Before}, gen2 {GC.CollectionCount(2) - gen2Before}; allocated {GC.GetTotalAllocatedBytes() / 1048576.0:0} MB so far");
            var peakWs = Process.GetCurrentProcess().PeakWorkingSet64;
            Thread.Sleep(30);
            stopSampling.Cancel();
            sampler.Wait();
            if (imported is not null) peakJournal = Math.Max(peakJournal, imported.PeakJournalBytes);
            var dbAfter = new FileInfo(main).Length;
            if (imported?.Phases is { } ph) Console.WriteLine($"phases of the measured import (ms): folders {ph.Folders.TotalMilliseconds:0}, files {ph.Files.TotalMilliseconds:0}, errors {ph.Errors.TotalMilliseconds:0}, verification {ph.Verification.TotalMilliseconds:0}, commit {ph.Commit.TotalMilliseconds:0}");
            Console.WriteLine($"import wall time {wall:0.00} s; peak working set {peakWs / 1048576.0:0} MB (before: {wsBefore / 1048576.0:0} MB)");

            double verifySeconds = 0, deleteSeconds = 0;
            string? verifyResult = null;
            var deletePeak = 0L;
            var intact = true;
            if (failureKind is not null)
            {
                // the engine failed and rolled back: nothing partial is visible and the older snapshots verify
                measured.TestOnlyShutdown();
                var reopened = new LibrarySession(directory, appData);
                reopened.RunStartupOpen();
                var snapshots = reopened.Read(r => r.Long("SELECT count(*) FROM snapshot"));
                intact = snapshots == 3 && Enumerable.Range(1, 3).All(id => reopened.Read(r => SnapshotVerifier.VerifyPublished(r, id)) is null);
                Console.WriteLine($"after the failure: {snapshots} snapshots, older snapshots intact: {intact}, database {dbAfter / 1048576.0:0.0} MB (was {dbBefore / 1048576.0:0.0} MB)");
                reopened.TestOnlyShutdown();
            }
            else
            {
                var verifyWatch = Stopwatch.StartNew();
                verifyResult = measured.Read(r => SnapshotVerifier.VerifyPublished(r, imported!.SnapshotId));
                verifySeconds = verifyWatch.Elapsed.TotalSeconds;
                if (verifyResult is not null) throw new InvalidOperationException("verification of the committed snapshot failed: " + verifyResult);
                if (delete)
                {
                    var deletePeakBox = 0L;
                    using var stopDeleteSampling = new CancellationTokenSource();
                    var deleteSampler = Task.Run(() =>
                    {
                        while (!stopDeleteSampling.IsCancellationRequested)
                        {
                            var length = File.Exists(journal) ? new FileInfo(journal).Length : 0;
                            if (length > deletePeakBox) deletePeakBox = length;
                            Thread.Sleep(5);
                        }
                    });
                    var deleteWatch = Stopwatch.StartNew();
                    using (var lease = Lease(measured, MutationKind.Delete)) measured.DeleteSnapshotAsync(lease, imported!.SnapshotId).GetAwaiter().GetResult();
                    deleteSeconds = deleteWatch.Elapsed.TotalSeconds;
                    Thread.Sleep(30);
                    stopDeleteSampling.Cancel();
                    deleteSampler.Wait();
                    deletePeak = deletePeakBox;
                    Console.WriteLine($"deleted the snapshot in {deleteSeconds:0.00} s, peak journal {deletePeak / 1048576.0:0.0} MB");
                }
                measured.TestOnlyShutdown();
            }

            var m = new Measurement(label, files, fk, prefill, imported?.Files ?? target.Files, imported?.Folders ?? target.FolderCount, imported?.NewNames ?? 0,
                imported?.Elapsed.TotalSeconds ?? importSeconds, imported is null ? 0 : imported.Files / imported.Elapsed.TotalSeconds,
                imported is null ? 0 : (imported.Files + imported.Folders) / imported.Elapsed.TotalSeconds, peakJournal, dbBefore, dbAfter, wsBefore, peakWs,
                verifySeconds, verifyResult, deleteSeconds, deletePeak, failureKind, rollbackSeconds, dbAfter, intact);
            Console.WriteLine(JsonSerializer.Serialize(m));
            return 0;
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* held until exit */ }
        }
    }

    private static MutationLease Lease(LibrarySession session, MutationKind kind)
    {
        if (!session.Interlock.TryBeginMutation(kind, 0, out var lease, out var refusal)) throw new InvalidOperationException("lease refused: " + refusal);
        return lease;
    }

    /// <summary>One capture's Save lease and import. <paramref name="seconds"/> is the import's own duration, BEGIN to COMMIT.</summary>
    private static ImportResult Import(LibrarySession session, SyntheticSnapshot snapshot, string runId, ImportSourceSpec source, ImportOptions? options, out double seconds)
    {
        var captureId = session.NewCaptureId();
        using var prepare = Lease(session, MutationKind.Prepare);
        var attempt = session.RecordAttemptStartAsync(prepare, new AttemptStart(new byte[16], null, Utf16.ToBytes(@"D:\Media"), null, runId, DateTime.UtcNow.Ticks)).GetAwaiter().GetResult();
        _ = captureId;
        using var save = prepare.HandOffToObservation().HandOffToSave(out _);
        var watch = Stopwatch.StartNew();
        var result = session.ImportSnapshotAsync(save, attempt, source, snapshot.Header(runId), snapshot, options).GetAwaiter().GetResult();
        seconds = watch.Elapsed.TotalSeconds;
        return result;
    }

    /// <summary>Q-07: the same observation tables with the optional foreign keys declared, replacing the four tables of the freshly
    /// created (empty) Library. Done through the raw connection of the tests, before any row exists.</summary>
    private static void DeclareForeignKeys(string main)
    {
        RawSqlite.Execute(main,
            "DROP TABLE file_obs", "DROP TABLE folder_obs", "DROP TABLE scan_error", "DROP TABLE snapshot_extension_total",
            """
            CREATE TABLE folder_obs (
              snapshot_id INTEGER NOT NULL REFERENCES snapshot (snapshot_id), path_id INTEGER NOT NULL REFERENCES folder_path (path_id),
              discovery_index INTEGER NOT NULL, status INTEGER NOT NULL, status_reason INTEGER, subtree_complete INTEGER NOT NULL, attributes INTEGER NOT NULL,
              created_utc INTEGER, modified_utc INTEGER, direct_bytes INTEGER NOT NULL, total_bytes INTEGER NOT NULL, direct_files INTEGER NOT NULL, total_files INTEGER NOT NULL,
              direct_subfolders INTEGER NOT NULL, total_subfolders INTEGER NOT NULL, largest_file_bytes INTEGER, PRIMARY KEY (snapshot_id, path_id)
            ) STRICT, WITHOUT ROWID
            """,
            """
            CREATE TABLE file_obs (
              snapshot_id INTEGER NOT NULL REFERENCES snapshot (snapshot_id), folder_path_id INTEGER NOT NULL REFERENCES folder_path (path_id),
              name_id INTEGER NOT NULL REFERENCES name (name_id), seq INTEGER NOT NULL, size_bytes INTEGER NOT NULL, modified_utc INTEGER, created_utc INTEGER,
              accessed_utc INTEGER, attributes INTEGER NOT NULL, PRIMARY KEY (snapshot_id, folder_path_id, name_id)
            ) STRICT, WITHOUT ROWID
            """,
            """
            CREATE TABLE scan_error (
              snapshot_id INTEGER NOT NULL REFERENCES snapshot (snapshot_id), seq INTEGER NOT NULL, rel_path BLOB NOT NULL, error_type INTEGER NOT NULL, message BLOB NOT NULL,
              PRIMARY KEY (snapshot_id, seq)
            ) STRICT, WITHOUT ROWID
            """,
            """
            CREATE TABLE snapshot_extension_total (
              snapshot_id INTEGER NOT NULL REFERENCES snapshot (snapshot_id), extension_key BLOB NOT NULL, files INTEGER NOT NULL, bytes INTEGER NOT NULL,
              PRIMARY KEY (snapshot_id, extension_key)
            ) STRICT, WITHOUT ROWID
            """);
    }
}

using System.Diagnostics;
using StorageInventory.Core;

namespace StorageInventory.Library.Tests;

/// <summary>
/// Scenarios run by a child process (see Program.cs). They exist because writer locks, hot journals and process termination cannot
/// be shown with several objects in one process (G0 R4): the child is this same executable, started with
/// <c>--child &lt;scenario&gt; &lt;args&gt;</c>, and is synchronised with the test through files (a ready file it writes, a stop file
/// it waits for) so that nothing depends on timing.
/// </summary>
internal static class ChildScenarios
{
    internal static int Run(string scenario, string[] args)
    {
        try
        {
            return scenario switch
            {
                "hold" => Hold(args),
                "try-open" => TryOpen(args),
                "race" => Race(args),
                "import-hang" => ImportHang(args),
                "setaside" => SetAside(args),
                _ => Fail("unknown child scenario " + scenario),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 3;
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }

    /// <summary>Writes a file in one step (a temporary name, then a move) so the parent never reads half of it.</summary>
    private static void Publish(string path, string text)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, path, overwrite: true);
    }

    private static void WaitForFile(string path)
    {
        while (!File.Exists(path)) Thread.Sleep(10);
    }

    private static string Describe(LibraryStatus status) => $"{status.State}|{status.Reason}|{(status.RecoveryPending ? "recovery-pending" : "")}";

    /// <summary>hold &lt;dir&gt; &lt;appdata&gt; &lt;ready&gt; &lt;stop&gt; &lt;create|open&gt; [snapshots]: opens, optionally creates the Library and saves some
    /// snapshots, signals ready, and holds the writer lock until the stop file appears (or it is killed).</summary>
    private static int Hold(string[] args)
    {
        var (dir, appData, ready, stop, mode) = (args[0], args[1], args[2], args[3], args[4]);
        var snapshots = args.Length > 5 ? int.Parse(args[5]) : 0;
        var session = new LibrarySession(dir, appData);
        var status = session.RunStartupOpen();
        if (mode == "create" && status.State is LibraryState.NotCreated)
        {
            using var lease = Lease(session, MutationKind.Create);
            status = session.CreateLibrary(lease);
        }
        for (var i = 1; i <= snapshots && status.State == LibraryState.Available; i++)
        {
            LibraryStateTests.ImportOne(session, new SyntheticSnapshot(60, seed: i, label: "h" + i), "hold-" + i, i == 1 ? null : new ImportSourceSpec.Existing(1));
        }
        Publish(ready, Describe(status));
        WaitForFile(stop);
        return 0;
    }

    /// <summary>try-open &lt;dir&gt; &lt;appdata&gt; &lt;out&gt;: a second process's start-up open; writes the state it derived.</summary>
    private static int TryOpen(string[] args)
    {
        var session = new LibrarySession(args[0], args[1]);
        var status = session.RunStartupOpen();
        Publish(args[2], Describe(status));
        return 0;
    }

    /// <summary>Busy-waits for a file with no sleep (a tight, cooperative spin), so that two processes released by the same file leave
    /// their wait within microseconds of each other. Fails the scenario when the file never appears.</summary>
    private static void SpinFor(string path, int timeoutMilliseconds = 30_000)
    {
        var started = Stopwatch.StartNew();
        var spinner = new SpinWait();
        while (!File.Exists(path))
        {
            if (started.ElapsedMilliseconds > timeoutMilliseconds) throw new TimeoutException("timed out waiting for " + path);
            spinner.SpinOnce(sleep1Threshold: -1);
        }
    }

    /// <summary>race &lt;dir&gt; &lt;appdata&gt; &lt;id&gt; &lt;rendezvous&gt; &lt;barrier&gt; &lt;out&gt; &lt;stop&gt;: a process that starts with no Library. It opens (Not created,
    /// no lock), writes its ready file (the parent's handshake), spins on the barrier, then saves for the first time (a Prepare lease:
    /// lock first, state re-derived under it). The rendezvous makes the creation CONTESTED: when its open finds no <c>library.lock</c>
    /// and is about to create it, it announces that and waits (spinning) until its rival has announced the same, so both are at the
    /// <c>CreateNew</c> arbitration together. It reports the status, the lock branches it took, and how many SQLite connections it
    /// ever opened.</summary>
    private static int Race(string[] args)
    {
        var (dir, appData, id, rendezvous, barrier, outFile, stop) = (args[0], args[1], args[2], args[3], args[4], args[5], args[6]);
        var events = new List<string>();
        var hooks = new LibraryStoreHooks
        {
            BeforeLockCreate = () =>
            {
                events.Add("before-create");
                Publish(Path.Combine(rendezvous, "at-create-" + id), "");
                SpinFor(Path.Combine(rendezvous, "at-create-1"));
                SpinFor(Path.Combine(rendezvous, "at-create-2"));
            },
            OnLockBranch = events.Add,
        };
        var session = new LibrarySession(dir, appData, new LibrarySessionOptions { StoreHooks = hooks });
        var open = session.RunStartupOpen();
        Publish(Path.Combine(rendezvous, "ready-" + id), Describe(open));
        SpinFor(barrier);
        LibraryStatus status;
        using (var prepare = Lease(session, MutationKind.Prepare, 1))
        {
            status = session.PrepareForSave(prepare);
        }
        Publish(outFile, string.Join(';', $"open={Describe(open)}", $"status={Describe(status)}", $"events={string.Join(',', events)}",
            $"writers={WriterConnection.WritersOpenedTotal}", $"readers={ReaderConnection.ReadersOpenedTotal}"));
        WaitForFile(stop);
        return 0;
    }

    /// <summary>import-hang &lt;dir&gt; &lt;appdata&gt; &lt;ready&gt; &lt;threshold&gt;: creates a Library holding one snapshot, then starts a second
    /// import that is large enough to spill the page cache, and blocks inside it (after &lt;threshold&gt; file rows) until killed:
    /// the journal is then non-empty and the database file has been written to, so only the hot journal can undo it.</summary>
    private static int ImportHang(string[] args)
    {
        var (dir, appData, ready, threshold) = (args[0], args[1], args[2], long.Parse(args[3]));
        var session = new LibrarySession(dir, appData);
        var status = session.RunStartupOpen();
        if (status.State == LibraryState.NotCreated)
        {
            using var lease = Lease(session, MutationKind.Create);
            status = session.CreateLibrary(lease);
        }
        if (status.State != LibraryState.Available) return Fail("not available: " + Describe(status));
        var first = LibraryStateTests.ImportOne(session, new SyntheticSnapshot(2000, seed: 1), "first");
        var options = new ImportOptions { OnFileRows = count => { if (count >= threshold) { Publish(ready, "importing " + count); Thread.Sleep(Timeout.Infinite); } } };
        var captureId = session.NewCaptureId();
        using var prepare = Lease(session, MutationKind.Prepare, captureId);
        var attempt = session.RecordAttemptStartAsync(prepare, new AttemptStart(new byte[16], null, Utf16.ToBytes(@"D:\Media"), null, "second", DateTime.UtcNow.Ticks)).GetAwaiter().GetResult();
        using var save = prepare.HandOffToObservation().HandOffToSave(out _);
        var big = new SyntheticSnapshot(int.Parse(args.Length > 4 ? args[4] : "400000"), seed: 2, label: "big");
        session.ImportSnapshotAsync(save, attempt, new ImportSourceSpec.Existing(first.SourceId), big.Header("second"), big, options).GetAwaiter().GetResult();
        return Fail("the import finished before it was killed (threshold too high)");
    }

    /// <summary>setaside &lt;dir&gt; &lt;appdata&gt; &lt;out&gt; &lt;crashAfterRenames|-&gt;: takes the SetAside lease WITHOUT opening SQLite (as the
    /// "Set aside" action does for a Library that cannot be opened), sets the member set aside and reports; with a number it kills
    /// itself after that many renames (a crash between renames).</summary>
    private static int SetAside(string[] args)
    {
        var (dir, appData, outFile, crashAfter) = (args[0], args[1], args[2], args[3]);
        var hooks = crashAfter == "-" ? null : new LibraryStoreHooks { AfterRename = n => { if (n >= int.Parse(crashAfter)) Process.GetCurrentProcess().Kill(); } };
        var session = new LibrarySession(dir, appData, new LibrarySessionOptions { StoreHooks = hooks });
        session.Interlock.TakeStartupLease().Dispose();   // skip the open: the Library may be one that cannot be opened
        using var lease = Lease(session, MutationKind.SetAside);
        var result = session.SetAsideAsync(lease).GetAwaiter().GetResult();
        Publish(outFile, $"{Describe(result.Status)}|moved={(result.Quarantine is null ? "none" : string.Join(",", result.Quarantine.Moved))}|stem={result.Quarantine?.Stem}");
        return 0;
    }

    private static MutationLease Lease(LibrarySession session, MutationKind kind, long owner = 0)
    {
        if (!session.Interlock.TryBeginMutation(kind, owner, out var lease, out var refusal)) throw new InvalidOperationException("lease refused: " + refusal);
        return lease;
    }
}

/// <summary>A child process of this executable, with files for synchronisation.</summary>
internal sealed class Child : IDisposable
{
    private readonly Process _process;
    private readonly string _stderrFile;

    private Child(Process process, string stderrFile)
    {
        _process = process;
        _stderrFile = stderrFile;
    }

    internal static Child Start(World world, string scenario, params string[] args)
    {
        var stderr = Path.Combine(world.Root, $"child-{scenario}-{Guid.NewGuid():N}"[..24] + ".err");
        var info = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        // started as "dotnet <dll>" (dotnet run, dotnet test): the child needs the same host and the same assembly
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)) info.ArgumentList.Add(System.Reflection.Assembly.GetEntryAssembly()!.Location);
        info.ArgumentList.Add("--child");
        info.ArgumentList.Add(scenario);
        foreach (var arg in args) info.ArgumentList.Add(arg);
        var process = Process.Start(info)!;
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) File.AppendAllText(stderr, e.Data + Environment.NewLine); };
        process.OutputDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        return new Child(process, stderr);
    }

    internal bool HasExited => _process.HasExited;

    internal int ExitCode => _process.ExitCode;

    internal string Errors => File.Exists(_stderrFile) ? File.ReadAllText(_stderrFile) : "";

    /// <summary>Waits for a file the child writes; fails with the child's error output when it dies first.</summary>
    internal string WaitFor(string file, int milliseconds = 60_000)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.ElapsedMilliseconds < milliseconds)
        {
            if (File.Exists(file))
            {
                try { return File.ReadAllText(file); }
                catch (IOException) { /* being replaced */ }
            }
            if (_process.HasExited && !File.Exists(file)) throw new StorageInventory.Testing.AssertionException($"the child exited with {_process.ExitCode} before writing {Path.GetFileName(file)}: {Errors}");
            Thread.Sleep(10);
        }
        throw new StorageInventory.Testing.AssertionException($"timed out waiting for {Path.GetFileName(file)}: {Errors}");
    }

    internal void Kill()
    {
        if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        _process.WaitForExit();
    }

    internal bool WaitForExit(int milliseconds = 30_000) => _process.WaitForExit(milliseconds);

    public void Dispose()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { /* gone */ }
        _process.Dispose();
    }
}

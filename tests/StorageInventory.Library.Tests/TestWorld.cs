using System.Diagnostics;
using System.Security.Cryptography;
using StorageInventory.Testing;

namespace StorageInventory.Library.Tests;

/// <summary>A scratch app-data root with a Library directory under it, in <c>%TEMP%</c> (LIB-04: the Library is location-agnostic,
/// so tests never touch <c>%LOCALAPPDATA%</c>). In-process sessions keep their writer-lock handle until the process exits, as in
/// production; stale worlds of earlier runs are deleted at the start of the next one.</summary>
internal sealed class World
{
    internal static readonly string RunRoot = InitialiseRunRoot();

    private static string InitialiseRunRoot()
    {
        var parent = Path.Combine(Path.GetTempPath(), "SI-Library-Tests");
        Directory.CreateDirectory(parent);
        foreach (var stale in Directory.EnumerateDirectories(parent))
        {
            // only the folders of runs that are over: a run's folder is named run-<pid>-..., and a live process's is not touched
            // (parallel test processes must not delete each other's worlds)
            var parts = Path.GetFileName(stale).Split('-');
            if (parts.Length > 2 && int.TryParse(parts[1], out var pid) && pid != Environment.ProcessId)
            {
                try { if (!System.Diagnostics.Process.GetProcessById(pid).HasExited) continue; }
                catch (ArgumentException) { /* no such process: the run is over */ }
            }
            try { Directory.Delete(stale, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* held by a still-running process */ }
        }
        var run = Path.Combine(parent, $"run-{Environment.ProcessId}-{Guid.NewGuid():N}"[..28]);
        Directory.CreateDirectory(run);
        return run;
    }

    private World(string root)
    {
        Root = root;
        AppData = Path.Combine(root, "AppData");
        LibraryDirectory = Path.Combine(AppData, "Library");
    }

    internal string Root { get; }

    /// <summary>The stand-in for <c>%LOCALAPPDATA%\StorageInventory</c>; it exists, the Library directory below it does not.</summary>
    internal string AppData { get; }

    /// <summary>The stand-in for <c>%LOCALAPPDATA%\StorageInventory\Library</c>.</summary>
    internal string LibraryDirectory { get; }

    internal static World Create([System.Runtime.CompilerServices.CallerMemberName] string name = "world")
    {
        // a short folder name: the longest test names would pass MAX_PATH under a hosted runner's longer temp folder
        var world = new World(Path.Combine(RunRoot, (name.Length > 40 ? name[..40] : name) + "-" + Guid.NewGuid().ToString("N")[..6]));
        Directory.CreateDirectory(world.AppData);
        return world;
    }

    internal string Member(string name) => Path.Combine(LibraryDirectory, name);
    internal string Main => Member(LibraryNames.MainFile);
    internal string Journal => Member(LibraryNames.JournalFile);
    internal string Lock => Member(LibraryNames.LockFile);

    internal LibrarySession NewSession(LibrarySessionOptions? options = null) => new(LibraryDirectory, AppData, options);

    /// <summary>A session whose start-up open has run: the interlock is Idle (Not created, if nothing exists yet).</summary>
    internal LibrarySession OpenedSession(LibrarySessionOptions? options = null)
    {
        var session = NewSession(options);
        session.RunStartupOpen();
        return session;
    }

    /// <summary>A session with a created, Available Library and an Idle interlock.</summary>
    internal LibrarySession CreatedSession(LibrarySessionOptions? options = null)
    {
        var session = OpenedSession(options);
        using var lease = Lease(session, MutationKind.Create);
        var status = session.CreateLibrary(lease);
        Assert.Equal(LibraryState.Available, status.State, "the Library was created: " + status.Message);
        return session;
    }

    /// <summary>Grants a lease from Idle and fails the test if it is refused.</summary>
    internal static MutationLease Lease(LibrarySession session, MutationKind kind, long owner = 0)
    {
        Assert.True(session.Interlock.TryBeginMutation(kind, owner, out var lease, out var refusal), $"a {kind} lease was refused: {refusal}");
        return lease;
    }

    /// <summary>The names of the files in the Library directory, sorted (names only: sizes and times can lag).</summary>
    internal List<string> Names() => !Directory.Exists(LibraryDirectory) ? [] : [.. Directory.EnumerateFileSystemEntries(LibraryDirectory).Select(p => Path.GetFileName(p)).Order(StringComparer.Ordinal)];

    /// <summary>SHA-256 of every member's CONTENTS, read through a handle that shares everything, keyed by name. The lock file is
    /// held with <c>FileShare.None</c> by its owner and is never written, so it is represented by its length (0) instead.</summary>
    internal Dictionary<string, string> ContentHashes()
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in Names())
        {
            if (name == LibraryNames.LockFile) { hashes[name] = "length=" + new FileInfo(Member(name)).Length; continue; }
            using var stream = new FileStream(Member(name), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            hashes[name] = Convert.ToHexString(SHA256.HashData(stream));
        }
        return hashes;
    }

    internal static long Length(string path) => File.Exists(path) ? new FileInfo(path).Length : -1;

    /// <summary>Waits for a condition with a deadline; true when it held.</summary>
    internal static bool WaitFor(Func<bool> condition, int milliseconds = 15000)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.ElapsedMilliseconds < milliseconds)
        {
            if (condition()) return true;
            Thread.Sleep(20);
        }
        return condition();
    }
}

/// <summary>Test helper: the writer's queries inside its transaction, guarded by the lease on every statement (what the importer's
/// verification uses), without a lease-holding object in the product.</summary>
internal static class WriterQueries
{
    internal static IQueryRunner Of(WriterConnection writer, MutationLease lease) => new DelegateQueryRunner(
        (sql, parameters) => writer.Scalar(lease, sql, parameters),
        (sql, each, parameters) => writer.Rows(lease, sql, each, parameters));
}

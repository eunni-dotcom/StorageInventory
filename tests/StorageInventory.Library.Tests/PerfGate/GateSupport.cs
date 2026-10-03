using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using StorageInventory.Core;

namespace StorageInventory.Library.Tests.PerfGate;

/// <summary>File and process helpers of the gate harness.</summary>
internal static class GateSupport
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetDiskFreeSpaceExW(string directory, out ulong freeToCaller, out ulong total, out ulong totalFree);

    /// <summary>The bytes free to the caller on the volume of <paramref name="path"/>, or -1 when it cannot be read.</summary>
    internal static long FreeBytes(string path)
    {
        try
        {
            var directory = Directory.Exists(path) ? path : Path.GetDirectoryName(path)!;
            return GetDiskFreeSpaceExW(directory, out var free, out _, out _) ? (long)free : -1;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ArgumentException)
        {
            return -1;
        }
    }

    /// <summary>The length of a file through a handle that shares everything (a directory listing can lag).</summary>
    internal static long HandleLength(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return fs.Length;
    }

    internal static long HandleLengthOrZero(string path)
    {
        try { return HandleLength(path); }
        catch (FileNotFoundException) { return 0; }
        catch (DirectoryNotFoundException) { return 0; }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    /// <summary><c>FlushFileBuffers</c> on a file (the OS cache stays warm; the data is on the disk).</summary>
    internal static void FlushToDisk(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        fs.Flush(flushToDisk: true);
    }

    /// <summary>Copies a file, flushes the copy to disk and returns its length and SHA-256 (read back from the copy).</summary>
    internal static (long Length, string Sha256) CopyFlushHash(string from, string to)
    {
        using (var src = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var dst = new FileStream(to, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            src.CopyTo(dst, 1 << 20);
            dst.Flush(flushToDisk: true);
        }
        using var read = new FileStream(to, FileMode.Open, FileAccess.Read, FileShare.Read);
        var length = read.Length;
        return (length, Convert.ToHexString(SHA256.HashData(read)).ToLowerInvariant());
    }

    internal static string Slug(string text) => new(text.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());

    internal static string BenchRoot() => Environment.GetEnvironmentVariable("SI_BENCH_ROOT") ?? Path.GetTempPath();

    internal static void TryDelete(string directory)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }

    internal static MutationLease Lease(LibrarySession session, MutationKind kind, long owner = 0)
    {
        if (!session.Interlock.TryBeginMutation(kind, owner, out var lease, out var refusal)) throw new InvalidOperationException("lease refused: " + refusal);
        return lease;
    }

    internal static string Iso(DateTime utc) => utc.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>The value of "--name" in an argument list, or <paramref name="fallback"/>.</summary>
    internal static string Opt(string[] args, string name, string fallback)
    {
        var i = Array.IndexOf(args, "--" + name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
    }

    internal static string Req(string[] args, string name) =>
        Opt(args, name, "") is { Length: > 0 } v ? v : throw new ArgumentException("missing --" + name);

    internal static bool Flag(string[] args, string name) => Array.IndexOf(args, "--" + name) >= 0;

    /// <summary>Starts this same executable with arguments (as <c>dotnet run</c> hosts it too), capturing its standard output lines.</summary>
    internal static (int ExitCode, List<string> Lines, string Error) RunSelf(IEnumerable<string> args, IReadOnlyDictionary<string, string>? environment = null)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(System.Reflection.Assembly.GetEntryAssembly()!.Location);
        foreach (var a in args) info.ArgumentList.Add(a);
        if (environment is not null) foreach (var (k, v) in environment) info.Environment[k] = v;
        using var process = Process.Start(info)!;
        var stderr = process.StandardError.ReadToEndAsync();
        var lines = new List<string>();
        while (process.StandardOutput.ReadLine() is { } line) lines.Add(line);
        process.WaitForExit();
        return (process.ExitCode, lines, stderr.Result);
    }

    /// <summary>The last output line that is a JSON object.</summary>
    internal static string? LastJson(IEnumerable<string> lines) => lines.LastOrDefault(l => l.StartsWith('{'));

    internal static JsonElement ParseObject(string json) => JsonDocument.Parse(json).RootElement.Clone();
}

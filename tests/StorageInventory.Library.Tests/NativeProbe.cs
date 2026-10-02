using System.Diagnostics;

namespace StorageInventory.Library.Tests;

/// <summary>
/// The Library's native-loading probe (Q-11, Q-22, TEST-R2's loading part). This executable, published single-file with exactly the
/// App's publish settings, opens a Library in a scratch folder (which loads <c>e_sqlite3.dll</c> through the runtime's own probing:
/// no resolver, no <c>AppContext.BaseDirectory</c>) and prints which native module and which assemblies were actually loaded, from
/// where, so that a script can check the extraction directory, the module's hash, and that a DLL or assembly planted beside the
/// exe is NOT loaded. The App itself does not call the Library until C5, so the App's own module list cannot show this yet; the
/// loading mechanism (single-file extraction and probing) is the same.
/// </summary>
internal static class NativeProbe
{
    internal static int Run(string scratch)
    {
        Directory.CreateDirectory(scratch);
        var library = Path.Combine(scratch, "Library");
        var session = new LibrarySession(library, scratch);
        var status = session.RunStartupOpen();
        if (status.State == LibraryState.NotCreated)
        {
            if (!session.Interlock.TryBeginMutation(MutationKind.Create, 0, out var lease, out var refusal)) throw new InvalidOperationException(refusal);
            using (lease) status = session.CreateLibrary(lease);
        }
        Console.WriteLine("state=" + status.State + " " + status.Reason + " " + status.Message);
        if (status.State == LibraryState.Available)
        {
            var version = session.Read(r => $"{r.Scalar("SELECT sqlite_version()")} {r.Scalar("SELECT sqlite_source_id()")}");
            Console.WriteLine("engine=" + version);
        }
        foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
        {
            var name = Path.GetFileName(module.FileName);
            if (name.Contains("sqlite", StringComparison.OrdinalIgnoreCase) || name.Contains("batteries", StringComparison.OrdinalIgnoreCase)) Console.WriteLine("module=" + module.FileName);
        }
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var name = assembly.GetName().Name ?? "";
            if (name.Contains("SQLite", StringComparison.OrdinalIgnoreCase) || name.Contains("batteries", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"assembly={name} location={(assembly.Location.Length == 0 ? "<bundle>" : assembly.Location)}");
            }
        }
        Console.WriteLine("exe=" + Environment.ProcessPath);
        Console.WriteLine("marker=" + (File.Exists(Environment.GetEnvironmentVariable("SI_PLANT_MARKER") ?? "") ? "PLANTED-ASSEMBLY-WAS-LOADED" : "absent"));
        return status.State == LibraryState.Available ? 0 : 1;
    }
}

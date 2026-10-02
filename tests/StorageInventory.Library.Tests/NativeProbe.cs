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
        // The experiment's own question to the runtime (set by the script, never by the product): "can a plain by-name load find the
        // planted assembly?" It tells "ignored because nothing asked" from "ignored because the runtime would not find it".
        if (Environment.GetEnvironmentVariable("SI_PROBE_RESOLVE_DECOY") == "1")
        {
            try
            {
                var found = System.Reflection.Assembly.Load(new System.Reflection.AssemblyName("SQLitePCLRaw.batteries_v2"));
                Console.WriteLine("decoy-resolve=found " + (found.Location.Length == 0 ? "<bundle>" : found.Location));
            }
            catch (Exception e) when (e is FileNotFoundException or FileLoadException or BadImageFormatException)
            {
                Console.WriteLine("decoy-resolve=notfound " + e.GetType().Name);
            }
        }
        // The control: the decoy is a working assembly. Loaded by explicit path (which the product never does) and initialised, it
        // writes the marker, so "the marker is absent" in the other runs means something.
        if (Environment.GetEnvironmentVariable("SI_PROBE_LOAD_DECOY") is { Length: > 0 } decoyPath)
        {
            var decoy = System.Reflection.Assembly.LoadFrom(decoyPath);
            decoy.GetType("SQLitePCL.Batteries_V2", throwOnError: true)!.GetMethod("Init", Type.EmptyTypes)!.Invoke(null, null);
            Console.WriteLine("decoy-loaded=" + decoy.Location);
            Console.WriteLine("marker-after=" + (File.Exists(Environment.GetEnvironmentVariable("SI_PLANT_MARKER") ?? "") ? "PLANTED-ASSEMBLY-WAS-INITIALISED" : "absent"));
        }
        return status.State == LibraryState.Available ? 0 : 1;
    }
}

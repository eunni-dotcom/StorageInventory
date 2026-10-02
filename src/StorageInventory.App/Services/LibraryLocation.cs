using System.IO;

namespace StorageInventory.App.Services;

/// <summary>
/// Where the Inventory Library lives (LIB-01, LIB-04, A-14): <c>%LOCALAPPDATA%\StorageInventory\Library\</c>, resolved here and
/// nowhere else in the product. The Library code itself is location-agnostic (every Library API takes an explicit directory), and
/// v1.1 has no user-selectable location, no second Library and no persisted setting for it (LIB-11, D-34). LocalAppData does not
/// roam; the path rules (<c>LibraryPathRules</c>) block it if it turns out to be a junction, a network location or a sync folder.
/// Nothing in C4 uses it yet: the capture wiring that opens the Library at start-up is C5.
/// </summary>
internal static class LibraryLocation
{
    /// <summary>The app-data root, <c>%LOCALAPPDATA%\StorageInventory\</c>: exclusively StorageInventory's. A scan source or a report
    /// folder inside it is blocked (LIB-06a, LIB-06b).</summary>
    internal static string AppDataRoot { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StorageInventory");

    /// <summary>The Library directory, <c>%LOCALAPPDATA%\StorageInventory\Library\</c>.</summary>
    internal static string LibraryDirectory { get; } = Path.Combine(AppDataRoot, "Library");
}

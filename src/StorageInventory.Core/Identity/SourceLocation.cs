using StorageInventory.Core.Paths;

namespace StorageInventory.Core.Identity;

/// <summary>
/// Where a source's root sits inside its volume or share, derived from the canonical path of the opened object (§7.1,
/// §7.4). Pure string work: no filesystem access, no name resolution, no case folding. The root is kept exactly as the
/// canonical path spells it; only the trailing separator is dropped.
/// </summary>
/// <param name="Kind">Local volume (a drive-letter path) or network (a UNC path).</param>
/// <param name="NetworkRoot">For a network source, the exact canonical <c>\\server\share</c>; otherwise null.</param>
/// <param name="RootInVolume">The root's exact, case-preserving path below the volume or share root: <c>\</c> for a whole
/// volume or share, <c>\Media\Music</c> for a folder; no trailing separator except for <c>\</c>. Compared by UTF-16 code
/// units, never case-insensitively (ID-02).</param>
internal readonly record struct SourceLocation(SourceKind Kind, string? NetworkRoot, string RootInVolume)
{
    /// <summary>
    /// Splits a canonical path (as <c>GetFinalPathNameByHandleW</c> returns it, without the extended prefix):
    /// <c>C:\Media\Music</c> becomes (local, null, <c>\Media\Music</c>) and <c>\\nas\share\Media</c> becomes
    /// (network, <c>\\nas\share</c>, <c>\Media</c>). Server and share spellings are kept exactly as given: different
    /// spellings of one server are different sources, and StorageInventory never resolves a name (INV-05).
    /// Returns false when the path has neither shape.
    /// </summary>
    public static bool TryDerive(string canonicalPath, out SourceLocation location)
    {
        location = default;
        if (string.IsNullOrEmpty(canonicalPath)) return false;

        if (canonicalPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var server = canonicalPath.IndexOf('\\', 2);
            if (server < 0 || server == 2) return false;
            if (canonicalPath[2..server] is "?" or ".") return false;   // \\?\ and \\.\ are the device namespace, not a server
            var shareStart = server + 1;
            var shareEnd = canonicalPath.IndexOf('\\', shareStart);
            var shareLength = (shareEnd < 0 ? canonicalPath.Length : shareEnd) - shareStart;
            if (shareLength <= 0) return false;
            var root = canonicalPath[..(shareStart + shareLength)];
            location = new SourceLocation(SourceKind.Network, root, RootBelow(canonicalPath[root.Length..]));
            return true;
        }

        if (canonicalPath.Length >= 3 && IsDriveLetter(canonicalPath[0]) && canonicalPath[1] == ':' && canonicalPath[2] == '\\')
        {
            location = new SourceLocation(SourceKind.LocalVolume, null, RootBelow(canonicalPath[2..]));
            return true;
        }

        return false;
    }

    /// <summary>
    /// The kind of the opened object: <see cref="SourceKind.Network"/> when its canonical path is a UNC path, which is what
    /// a mapped network drive letter resolves to, and <see cref="SourceKind.LocalVolume"/> otherwise. The drive-letter
    /// syntax of the input is never used as identity. Only when Windows could not report a canonical path (so the source
    /// cannot be saved anyway, ID-13) does the kind fall back to v1's own "UNC path or network drive" test.
    /// </summary>
    internal static SourceKind ClassifyKind(string? canonicalPath, string enumeratedPath, Func<string, bool>? isNetworkPath = null)
    {
        if (canonicalPath is not null) return canonicalPath.StartsWith(@"\\", StringComparison.Ordinal) ? SourceKind.Network : SourceKind.LocalVolume;
        return (isNetworkPath ?? PathPolicy.IsNetworkPath)(enumeratedPath) ? SourceKind.Network : SourceKind.LocalVolume;
    }

    private static bool IsDriveLetter(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    /// <summary>The part of the canonical path below the root (starting at its separator, or empty), without its trailing
    /// separator; the root itself is <c>\</c>.</summary>
    private static string RootBelow(string below)
    {
        var trimmed = below.TrimEnd('\\');
        return trimmed.Length == 0 ? @"\" : trimmed;
    }
}

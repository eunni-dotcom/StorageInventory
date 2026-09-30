namespace StorageInventory.IntegrationTests;

/// <summary>
/// Independent snapshot of a tree, used to prove that code under test changed nothing. Never follows reparse points.
/// The first listing of a freshly created tree can differ from later ones because NTFS lazily updates the folder
/// timestamps cached in parent directories (observed and documented in Phase A), so callers settle it first.
/// </summary>
public static class Snapshot
{
    public static string Take(string root, bool trueValues = false)
    {
        var lines = new List<string>();
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            FileSystemInfo[] items;
            try { items = new DirectoryInfo(dir).GetFileSystemInfos(); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { lines.Add("UNREADABLE|" + dir); continue; }
            foreach (var item in items)
            {
                var isDir = item is DirectoryInfo;
                FileSystemInfo v = item;
                if (trueValues)
                {
                    v = isDir ? new DirectoryInfo(item.FullName) : new FileInfo(item.FullName);
                    v.Refresh();
                }
                var len = isDir ? -1 : ((FileInfo)v).Length;
                lines.Add($"{item.FullName}|{len}|{Ticks(() => v.CreationTimeUtc)}|{Ticks(() => v.LastWriteTimeUtc)}|{v.Attributes}");
                if (isDir && !item.Attributes.HasFlag(FileAttributes.ReparsePoint)) stack.Push(item.FullName);
            }
        }
        lines.Sort(StringComparer.Ordinal);
        return string.Join("\n", lines);
    }

    /// <summary>List the tree once and discard the result (see the class remarks).</summary>
    public static void Settle(string root) => _ = Take(root);

    private static string Ticks(Func<DateTime> read)
    {
        try { return read().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture); }
        catch (ArgumentOutOfRangeException) { return "BADTIME"; }
    }
}

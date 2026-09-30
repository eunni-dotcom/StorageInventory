using System.Diagnostics;
using System.IO;
using StorageInventory.Core;

namespace StorageInventory.App.Services;

/// <summary>Opens a report or the report folder in the program Windows associates with it. Only ever called from an
/// explicit button click; nothing is opened automatically.</summary>
public interface IReportOpener
{
    void Open(string path);
}

/// <summary>
/// The ONLY place the application launches anything. It accepts nothing but this run's own report files and output
/// folder (checked against the scan result, and required to exist), so no path from the scanned tree, and no
/// arbitrary string, can ever be launched.
/// </summary>
public sealed class ReportOpener : IReportOpener
{
    private readonly StorageScanResult _scan;
    private readonly string? _workbook;

    public ReportOpener(StorageScanResult scan, string? workbook)
    {
        _scan = scan;
        _workbook = workbook;
    }

    public static bool IsAllowed(StorageScanResult scan, string? workbook, string path)
    {
        if (!scan.Finished || scan.Reports is not { } r) return false;
        var allowed = new[] { r.FilesCsv, r.FoldersCsv, r.ErrorsCsv, scan.OutputPath, workbook };
        return allowed.Any(a => a is not null && string.Equals(a, path, StringComparison.OrdinalIgnoreCase));
    }

    public void Open(string path)
    {
        if (!IsAllowed(_scan, _workbook, path)) throw new InvalidOperationException($"Refusing to open '{path}': it is not one of this run's reports.");
        if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException("The report no longer exists.", path);
        // ShellExecute of a report file (.csv/.xlsx) or of the report folder (Explorer), at the user's explicit request.
        using var _ = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}

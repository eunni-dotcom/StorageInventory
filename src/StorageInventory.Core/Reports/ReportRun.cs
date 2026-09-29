using System.Globalization;

namespace StorageInventory.Core.Reports;

/// <summary>
/// The per-run identity and the ONLY code in Core that creates, deletes or renames files.
/// <list type="bullet">
/// <item><description>Every file is created with <see cref="FileMode.CreateNew"/> (fails rather than overwrite), only
/// with one of this run's own names, and only directly inside the validated output folder.</description></item>
/// <item><description>A delete or rename must prove all three: this run created the file, it is in the output folder,
/// and it has the expected temporary/partial name. There is no generic or recursive delete.</description></item>
/// </list>
/// </summary>
internal sealed class ReportRun
{
    public const string TemporaryFilesSuffix = ".unsorted.tmp";
    public const string PartialWorkbookSuffix = ".xlsx.partial";

    private readonly List<string> _created = [];
    private readonly HashSet<string> _ownNames;

    private ReportRun(string outputFolder, string runId)
    {
        OutputFolder = outputFolder;
        RunId = runId;
        FilesCsv = Path.Combine(outputFolder, $"Files_{runId}.csv");
        FilesTemporary = Path.Combine(outputFolder, $"Files_{runId}{TemporaryFilesSuffix}");
        FoldersCsv = Path.Combine(outputFolder, $"Folders_{runId}.csv");
        ErrorsCsv = Path.Combine(outputFolder, $"ScanErrors_{runId}.csv");
        Workbook = Path.Combine(outputFolder, $"StorageInventory_{runId}.xlsx");
        WorkbookPartial = Workbook + ".partial";
        _ownNames = new HashSet<string>(
            new[] { FilesCsv, FilesTemporary, FoldersCsv, ErrorsCsv, Workbook, WorkbookPartial }.Select(p => Path.GetFileName(p)!),
            StringComparer.OrdinalIgnoreCase);
    }

    public string OutputFolder { get; }

    /// <summary>Local timestamp plus a random suffix, e.g. 20260926_143012_a1b2c3.</summary>
    public string RunId { get; }

    public string FilesCsv { get; }
    public string FilesTemporary { get; }
    public string FoldersCsv { get; }
    public string ErrorsCsv { get; }
    public string Workbook { get; }
    public string WorkbookPartial { get; }

    /// <summary>File names this run may create. Seeing one inside the scanned tree trips the safety tripwire.</summary>
    public IReadOnlySet<string> OwnFileNames => _ownNames;

    /// <summary>Files created by this run that still exist, in creation order.</summary>
    public IReadOnlyList<string> CreatedFiles => _created;

    /// <summary>Names the run. Throws <see cref="IOException"/> if any of its names already exists (nothing is
    /// created in that case).</summary>
    public static ReportRun Prepare(string validatedOutputFolder, DateTime localNow)
    {
        var runId = localNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N")[..6];
        var run = new ReportRun(validatedOutputFolder, runId);
        foreach (var name in run._ownNames)
        {
            var path = Path.Combine(validatedOutputFolder, name);
            if (File.Exists(path) || Directory.Exists(path))
            {
                throw new IOException($"'{path}' already exists. Nothing was overwritten; please run again.");
            }
        }
        return run;
    }

    /// <summary>The names of an already-finished run, for post-processing (the optional workbook). Nothing is
    /// recorded as created, so this instance can only delete or rename files it creates itself.</summary>
    public static ReportRun ForFinishedRun(string outputFolder, string runId)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(runId, @"^\d{8}_\d{6}_[0-9a-f]{6}$"))
            throw new ArgumentException($"'{runId}' is not a StorageInventory run ID.", nameof(runId));
        return new ReportRun(outputFolder, runId);
    }

    /// <summary>Creates the output folder if it does not exist. Returns true if it was created.</summary>
    public bool EnsureOutputFolder()
    {
        if (Directory.Exists(OutputFolder)) return false;
        Directory.CreateDirectory(OutputFolder);
        return true;
    }

    /// <summary>The only way Core creates a file.</summary>
    public FileStream CreateNew(string path)
    {
        if (!IsOwnPathInOutputFolder(path)) throw new InvalidOperationException($"Internal safety check failed: refusing to create '{path}'.");
        // Defence in depth: the folder was validated before the run, but must not have been swapped for a link since.
        if (File.GetAttributes(OutputFolder).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException($"The report folder '{OutputFolder}' has become a link since it was checked; nothing was written.");
        }
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, bufferSize: 1 << 20);
        _created.Add(path);
        return stream;
    }

    /// <summary>The only delete in Core: this run's own temporary Files file, in the output folder.</summary>
    public void DeleteOwnTemporaryFile(string path)
    {
        var provenOwn = _created.Contains(path, StringComparer.Ordinal);
        var expectedName = path.EndsWith(TemporaryFilesSuffix, StringComparison.OrdinalIgnoreCase) && string.Equals(path, FilesTemporary, StringComparison.Ordinal);
        if (!(provenOwn && expectedName && IsOwnPathInOutputFolder(path)))
        {
            throw new InvalidOperationException($"Internal safety check failed: refusing to delete '{path}'.");
        }
        File.Delete(path);
        _created.Remove(path);
    }

    /// <summary>The only rename in Core: this run's finished '.xlsx.partial' becomes its final name. File.Move with
    /// overwrite: false never replaces an existing file; it throws instead.</summary>
    public void PublishOwnWorkbook()
    {
        var provenOwn = _created.Contains(WorkbookPartial, StringComparer.Ordinal);
        if (!(provenOwn && IsOwnPathInOutputFolder(WorkbookPartial) && IsOwnPathInOutputFolder(Workbook)))
        {
            throw new InvalidOperationException($"Internal safety check failed: refusing to rename '{WorkbookPartial}'.");
        }
        File.Move(WorkbookPartial, Workbook, overwrite: false);
        _created.Remove(WorkbookPartial);
        _created.Add(Workbook);
    }

    // Both sides trimmed, so a drive-root output folder ("E:\") compares correctly.
    private bool IsOwnPathInOutputFolder(string path) =>
        string.Equals(Path.GetDirectoryName(path)?.TrimEnd('\\'), OutputFolder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
        && _ownNames.Contains(Path.GetFileName(path));
}

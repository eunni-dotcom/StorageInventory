namespace StorageInventory.Core.Scanning;

/// <summary>
/// Mutable per-folder state during a scan: one instance per folder, never one per file. Direct values are filled in
/// while walking; totals are filled in afterwards by the aggregation pass.
/// </summary>
internal sealed class FolderState
{
    /// <summary>Index of the parent in the folder table; -1 for the root. Always smaller than this folder's index,
    /// because a folder is only discovered while its parent is being listed.</summary>
    public int ParentIndex;
    public int Depth;
    public required string Name;

    /// <summary>Relative to the scan root; "." for the root.</summary>
    public required string RelativePath;

    public DateTime? CreatedUtc;
    public DateTime? ModifiedUtc;
    public FileAttributes Attributes;

    public long DirectSizeBytes;
    public long DirectFileCount;
    public long DirectSubfolderCount;
    public long TotalSizeBytes;
    public long TotalFileCount;
    public long TotalSubfolderCount;
    public long LargestFileBytes = -1;
    public string LargestFileRelativePath = "";

    public FolderScanStatus Status = FolderScanStatus.Ok;
    public ScanErrorType? StatusReason;
    public bool SubtreeComplete = true;

    /// <summary>Full path, kept only while the folder waits to be listed (released afterwards to save memory).</summary>
    public string? PendingFullPath;
}

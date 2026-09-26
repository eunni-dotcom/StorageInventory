namespace StorageInventory.Core;

/// <summary>
/// A progress snapshot. During <see cref="ScanPhase.Enumerating"/> the total amount of work is unknown, so
/// <see cref="PhaseItemsTotal"/> is 0 and callers must not show a percentage. Later phases (sorting, writing)
/// have a known total.
/// </summary>
public readonly record struct StorageScanProgress(
    ScanPhase Phase,
    long FilesDiscovered,
    long FoldersDiscovered,
    long BytesDiscovered,
    long ScanErrors,
    long ReparsePointsSkipped,
    long FileReparsePoints,
    string CurrentRelativePath,
    TimeSpan Elapsed,
    long PhaseItemsDone = 0,
    long PhaseItemsTotal = 0)
{
    /// <summary>True only when a genuine total is known for the current phase.</summary>
    public bool HasKnownTotal => PhaseItemsTotal > 0;
}

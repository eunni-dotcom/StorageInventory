namespace StorageInventory.Core;

/// <summary>What to scan and where to put the reports.</summary>
public sealed record StorageScanOptions
{
    /// <summary>Folder or drive root to inventory. Validated again by the scanner; callers are never trusted.</summary>
    public required string RootPath { get; init; }

    /// <summary>Folder that receives the reports. Must be outside <see cref="RootPath"/>. Created if missing.</summary>
    public required string OutputPath { get; init; }

    /// <summary>Write the Files report largest-first (needs a temporary file and an in-memory sort index) instead of
    /// in discovery order.</summary>
    public bool SortFiles { get; init; } = true;

    /// <summary>Reparse-point handling. Only <see cref="ReparsePointPolicy.NeverFollow"/> exists.</summary>
    public ReparsePointPolicy ReparsePoints { get; init; } = ReparsePointPolicy.NeverFollow;

    /// <summary>Minimum time between progress reports.</summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(250);
}

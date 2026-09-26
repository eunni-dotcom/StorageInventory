namespace StorageInventory.Core;

/// <summary>
/// One file, as read from the directory listing. Produced and handed on one at a time; the scanner never keeps a
/// collection of these (the Files report is streamed).
/// </summary>
/// <remarks>
/// Raw observation values only: timestamps are UTC (null when the stored value is not a valid date), sizes are bytes.
/// Presentation (local time, KB/MB/GB, file-type category, the Excel formula guard) belongs to the report layer, so
/// that a future snapshot format is never coupled to report columns.
/// </remarks>
public readonly record struct FileInventoryRecord(
    string FileName,
    string Extension,
    string RelativePath,
    string RelativeDirectory,
    string FullPath,
    long SizeBytes,
    DateTime? CreatedUtc,
    DateTime? ModifiedUtc,
    DateTime? LastAccessUtc,
    FileAttributes Attributes);

/// <summary>A row of the ScanErrors report.</summary>
public sealed record ScanErrorRecord(string Path, ScanErrorType Type, string Message);

/// <summary>Final totals for one folder (after recursive aggregation).</summary>
public sealed record FolderInventoryRecord
{
    public required string Name { get; init; }

    /// <summary>Relative to the scan root; "." for the root itself.</summary>
    public required string RelativePath { get; init; }

    /// <summary>Relative path of the parent; empty for the root.</summary>
    public required string ParentRelativePath { get; init; }

    public required string FullPath { get; init; }
    public required int Depth { get; init; }

    public required long DirectSizeBytes { get; init; }
    public required long TotalSizeBytes { get; init; }
    public required long DirectFileCount { get; init; }
    public required long TotalFileCount { get; init; }
    public required long DirectSubfolderCount { get; init; }
    public required long TotalSubfolderCount { get; init; }

    /// <summary>Largest file anywhere in this subtree, or null if the subtree has no files.</summary>
    public long? LargestFileBytes { get; init; }
    public string LargestFileRelativePath { get; init; } = "";

    /// <summary>Share of the root's total size, 0-100 (0 when the root total is 0).</summary>
    public required double PercentOfRoot { get; init; }

    /// <summary>Share of the parent's total size, 0-100; null for the root.</summary>
    public double? PercentOfParent { get; init; }

    /// <summary>UTC; null when the stored value is not a valid date.</summary>
    public DateTime? CreatedUtc { get; init; }
    /// <summary>UTC; null when the stored value is not a valid date.</summary>
    public DateTime? ModifiedUtc { get; init; }
    public required FileAttributes Attributes { get; init; }

    /// <summary>What happened to this folder itself.</summary>
    public required FolderScanStatus Status { get; init; }

    /// <summary>For <see cref="FolderScanStatus.Unreadable"/> / <see cref="FolderScanStatus.Partial"/>: why.</summary>
    public ScanErrorType? StatusReason { get; init; }

    /// <summary>False if this folder OR anything beneath it could not be fully read; totals are then lower bounds.
    /// Skipped reparse points never make a subtree incomplete.</summary>
    public required bool SubtreeComplete { get; init; }

    /// <summary>The ScanStatus column text, identical to the PowerShell reference:
    /// OK, &lt;ErrorType&gt;, Partial:&lt;ErrorType&gt;, ReparsePointSkipped.</summary>
    public string StatusText => Status switch
    {
        FolderScanStatus.Ok => "OK",
        FolderScanStatus.ReparsePointSkipped => "ReparsePointSkipped",
        FolderScanStatus.Unreadable => (StatusReason ?? ScanErrorType.UnexpectedError).ToString(),
        FolderScanStatus.Partial => "Partial:" + (StatusReason ?? ScanErrorType.UnexpectedError),
        _ => Status.ToString(),
    };
}

/// <summary>The authoritative CSV reports of one run.</summary>
public sealed record ReportSet(string FilesCsv, string FoldersCsv, string ErrorsCsv, bool FilesSortedLargestFirst);

/// <summary>Counters for a scan (final, or as far as the scan got).</summary>
public sealed record ScanTotals
{
    public long Files { get; init; }
    public long Folders { get; init; }
    public long Bytes { get; init; }

    /// <summary>ScanErrors rows that are real errors (excludes informational reparse-point rows).</summary>
    public long ScanErrors { get; init; }

    public long ReparsePointsSkipped { get; init; }
    public long FileReparsePoints { get; init; }

    /// <summary>Folders that could not be listed, or whose entries could not all be read.</summary>
    public long LocallyIncompleteFolders { get; init; }

    /// <summary>Folders read fine themselves but containing something unreadable.</summary>
    public long AffectedAncestorFolders { get; init; }
}

/// <summary>Time spent in each phase.</summary>
public sealed record PhaseTimings
{
    public TimeSpan Validation { get; init; }
    public TimeSpan Enumeration { get; init; }
    public TimeSpan Aggregation { get; init; }
    public TimeSpan FoldersReport { get; init; }
    public TimeSpan Sorting { get; init; }
    public TimeSpan FilesReport { get; init; }
    public TimeSpan Total { get; init; }
}

/// <summary>Why a scan failed, in words suitable for showing to a person.</summary>
public sealed record ScanFailure(ScanFailureKind Kind, string Message);

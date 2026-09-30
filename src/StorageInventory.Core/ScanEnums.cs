namespace StorageInventory.Core;

/// <summary>
/// How a scan ended. These are deliberately distinct: finishing a scan is NOT the same as every relevant
/// file-system entry having been readable.
/// </summary>
public enum ScanCompletionState
{
    /// <summary>The scan finished and every folder and entry beneath the root was read. Totals are exact.</summary>
    Complete,

    /// <summary>The scan finished and all reports were written, but some folders or entries could not be read
    /// (for example access denied). Totals are lower bounds.</summary>
    Incomplete,

    /// <summary>The user cancelled. Any report files created are incomplete and are listed as such.</summary>
    Cancelled,

    /// <summary>The scan could not start or could not finish (invalid paths, report write failure, safety tripwire,
    /// internal consistency failure). Any report files created are incomplete and are listed as such.</summary>
    Failed,
}

/// <summary>Classification of a row in the ScanErrors report. Names match the PowerShell reference.</summary>
public enum ScanErrorType
{
    AccessDenied,
    NotFound,
    PathTooLong,
    IOError,
    InvalidPath,
    InvalidTimestamp,
    UnexpectedError,

    /// <summary>Informational, not an error: a folder reparse point (junction, symbolic link, mount point, cloud
    /// placeholder...) was recorded but deliberately not entered.</summary>
    ReparsePointSkipped,

    /// <summary>Informational, not an error: a file reparse point was counted with the size the directory listing
    /// reports (for a symbolic link: the link itself); its target was not followed.</summary>
    ReparsePointFile,
}

public static class ScanErrorTypeExtensions
{
    /// <summary>True for rows that describe behaviour by design rather than a failure to read something.</summary>
    public static bool IsInformational(this ScanErrorType type) =>
        type is ScanErrorType.ReparsePointSkipped or ScanErrorType.ReparsePointFile;
}

/// <summary>How reparse points are treated during traversal.</summary>
public enum ReparsePointPolicy
{
    /// <summary>The only supported policy: folder reparse points are listed but never entered, and file reparse
    /// points are counted with their listed size without reading their targets. There is intentionally no "follow"
    /// option: following links can loop, double-count, leave the drive or reach the network.</summary>
    NeverFollow,
}

/// <summary>What the scanner is currently doing.</summary>
public enum ScanPhase
{
    Validating,
    Enumerating,
    Aggregating,
    WritingFoldersReport,
    SortingFiles,
    WritingFilesReport,
    Finished,
}

/// <summary>What happened to a folder itself (not its descendants; see <see cref="FolderInventoryRecord.SubtreeComplete"/>).</summary>
public enum FolderScanStatus
{
    /// <summary>The folder was listed completely.</summary>
    Ok,

    /// <summary>The folder could not be listed at all.</summary>
    Unreadable,

    /// <summary>The folder was listed, but some entries in it could not be read.</summary>
    Partial,

    /// <summary>The folder is a reparse point and was not entered, by design.</summary>
    ReparsePointSkipped,
}

/// <summary>Why a scan failed.</summary>
public enum ScanFailureKind
{
    /// <summary>The source or output path was blocked by validation. Nothing was created.</summary>
    InvalidPaths,

    /// <summary>A report file could not be created or written (disk full, access denied, name collision...).</summary>
    OutputError,

    /// <summary>The scan found this run's own report file inside the scanned tree: the output folder is reachable
    /// from the source through an alias. The scan was stopped to avoid writing into the scanned tree.</summary>
    OutputInsideScannedTree,

    /// <summary>The scanner's own accounting did not add up. Reports are withheld as untrustworthy.</summary>
    InternalConsistency,

    /// <summary>Anything else.</summary>
    Unexpected,
}

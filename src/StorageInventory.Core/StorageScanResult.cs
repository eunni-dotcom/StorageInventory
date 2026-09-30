namespace StorageInventory.Core;

/// <summary>
/// The outcome of a scan. Instances can only be created through the factory methods, which enforce the invariants
/// of each <see cref="ScanCompletionState"/>:
/// <list type="bullet">
/// <item><description>Complete / Incomplete: all three reports exist, no incomplete artifacts, no failure.</description></item>
/// <item><description>Complete requires a fully readable root; Incomplete requires that it was not.</description></item>
/// <item><description>Cancelled / Failed: no reports are presented as valid; created files are listed as incomplete.</description></item>
/// </list>
/// </summary>
public sealed class StorageScanResult
{
    private StorageScanResult(
        ScanCompletionState state, string runId, string rootPath, string outputPath, ScanTotals totals,
        IReadOnlyDictionary<ScanErrorType, long> errorCounts, FolderInventoryRecord? root,
        IReadOnlyList<FolderInventoryRecord> largestTopLevelFolders, ReportSet? reports,
        IReadOnlyList<string> incompleteArtifacts, ScanFailure? failure, PhaseTimings timings)
    {
        State = state;
        RunId = runId;
        RootPath = rootPath;
        OutputPath = outputPath;
        Totals = totals;
        ErrorCounts = errorCounts;
        Root = root;
        LargestTopLevelFolders = largestTopLevelFolders;
        Reports = reports;
        IncompleteArtifacts = incompleteArtifacts;
        Failure = failure;
        Timings = timings;
    }

    public ScanCompletionState State { get; }

    /// <summary>True only when the scan finished AND every folder and entry was readable. Totals are exact.</summary>
    public bool IsComplete => State == ScanCompletionState.Complete;

    /// <summary>True when the scan ran to the end and its reports are valid (Complete or Incomplete).
    /// A finished scan is not necessarily complete: check <see cref="IsComplete"/>.</summary>
    public bool Finished => State is ScanCompletionState.Complete or ScanCompletionState.Incomplete;

    /// <summary>Timestamp plus random suffix identifying every file this run created, e.g. 20260926_143012_a1b2c3.
    /// Empty if the run never got as far as naming its reports.</summary>
    public string RunId { get; }

    public string RootPath { get; }
    public string OutputPath { get; }
    public ScanTotals Totals { get; }
    public IReadOnlyDictionary<ScanErrorType, long> ErrorCounts { get; }

    /// <summary>The root folder's final record (null unless <see cref="Finished"/>).</summary>
    public FolderInventoryRecord? Root { get; }

    /// <summary>Largest folders directly beneath the root, largest first (empty unless <see cref="Finished"/>).</summary>
    public IReadOnlyList<FolderInventoryRecord> LargestTopLevelFolders { get; }

    /// <summary>The authoritative CSV reports; null unless <see cref="Finished"/>.</summary>
    public ReportSet? Reports { get; }

    /// <summary>Files this run created that are NOT valid reports (cancelled or failed runs). Never deleted
    /// automatically; shown to the user as incomplete and safe to delete.</summary>
    public IReadOnlyList<string> IncompleteArtifacts { get; }

    public ScanFailure? Failure { get; }
    public PhaseTimings Timings { get; }

    internal static StorageScanResult ForFinishedScan(
        string runId, string rootPath, string outputPath, ScanTotals totals,
        IReadOnlyDictionary<ScanErrorType, long> errorCounts, FolderInventoryRecord root,
        IReadOnlyList<FolderInventoryRecord> largestTopLevelFolders, ReportSet reports, PhaseTimings timings)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(reports);
        if (string.IsNullOrEmpty(runId)) throw new ArgumentException("A finished scan must have a run ID.", nameof(runId));
        foreach (var path in new[] { reports.FilesCsv, reports.FoldersCsv, reports.ErrorsCsv })
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("A finished scan must have all three reports.", nameof(reports));
        }
        if (root.RelativePath != ".") throw new ArgumentException("The root record must have RelativePath '.'.", nameof(root));

        // The root's completeness decides between Complete and Incomplete - never the caller.
        var state = root.SubtreeComplete ? ScanCompletionState.Complete : ScanCompletionState.Incomplete;
        return new StorageScanResult(state, runId, rootPath, outputPath, totals, errorCounts, root,
            largestTopLevelFolders, reports, [], null, timings);
    }

    internal static StorageScanResult ForCancelled(
        string runId, string rootPath, string outputPath, ScanTotals totalsSoFar,
        IReadOnlyDictionary<ScanErrorType, long> errorCounts, IReadOnlyList<string> incompleteArtifacts, PhaseTimings timings)
        => new(ScanCompletionState.Cancelled, runId, rootPath, outputPath, totalsSoFar, errorCounts, null, [], null,
            incompleteArtifacts, null, timings);

    internal static StorageScanResult ForFailed(
        ScanFailure failure, string runId, string rootPath, string outputPath, ScanTotals totalsSoFar,
        IReadOnlyDictionary<ScanErrorType, long> errorCounts, IReadOnlyList<string> incompleteArtifacts, PhaseTimings timings)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new(ScanCompletionState.Failed, runId, rootPath, outputPath, totalsSoFar, errorCounts, null, [], null,
            incompleteArtifacts, failure, timings);
    }
}

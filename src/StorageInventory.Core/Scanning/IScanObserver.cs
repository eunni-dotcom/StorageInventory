namespace StorageInventory.Core.Scanning;

/// <summary>
/// Receives the observations of one scan (SINK-02). It replaces v1's <c>IScanSink</c>, whose two methods it keeps
/// unchanged, and adds a lifecycle. Internal until the 1.2 integration design (SINK-09, D-46).
/// </summary>
/// <remarks>
/// <para>Calls arrive on the scanning thread, in this order (SINK-03):</para>
/// <list type="number">
/// <item><description>If the scan fails before its run is named (invalid paths, or a report folder that cannot be
/// prepared), no observer is called. Otherwise <see cref="OnScanStarted"/> comes first and <see cref="OnScanEnded"/>
/// last, exactly once each.</description></item>
/// <item><description><see cref="OnFile"/> and <see cref="OnError"/> happen only during enumeration. All files of one
/// folder form one contiguous run with respect to other folders' files (errors may interleave). Runs follow the
/// listing order (depth-first pop order), not folder index order.</description></item>
/// <item><description>The n-th <see cref="OnFile"/> call (0-based) has v1 sequence n, the tie-breaker of the sorted
/// Files report.</description></item>
/// <item><description><see cref="OnFolderFinalised"/> is called once per folder, in ascending index order, only if
/// enumeration and aggregation (with its self-check) completed: after the last file and error, before the Folders
/// report is written. Its parent index is always smaller; the root has index 0 and parent -1.</description></item>
/// <item><description>The calls and arguments of <see cref="OnFile"/> and <see cref="OnError"/> are exactly v1's, so
/// report bytes are unchanged.</description></item>
/// </list>
/// <para>There is deliberately no discovery callback (SINK-08): while enumeration runs, a folder's state is provisional
/// (it can still become Partial, Unreadable or ReparsePointSkipped), so the first and only emission of a folder is its
/// finalised record. No observer can treat a folder as final before it is.</para>
/// <para>An observer must not retain every file record. How its exceptions are handled depends on how it is attached:
/// see <see cref="ObserverFanOut"/>.</para>
/// </remarks>
internal interface IScanObserver
{
    /// <summary>Once, first: the run's identity and root.</summary>
    void OnScanStarted(in ScanStartInfo start);

    /// <param name="folderIndex">Index of the containing folder: a forward reference that the folder's finalised
    /// record resolves.</param>
    void OnFile(in FileInventoryRecord file, int folderIndex);

    void OnError(ScanErrorRecord error);

    /// <summary>A folder's final record, after aggregation and its self-check: totals, status, status reason and
    /// subtree completeness are final.</summary>
    void OnFolderFinalised(int index, int parentIndex, FolderInventoryRecord folder);

    /// <summary>Exactly once, last, on every path after <see cref="OnScanStarted"/>.</summary>
    void OnScanEnded(in ScanEndInfo end);
}

/// <summary>What every observer learns when a named run starts.</summary>
/// <param name="RunId">The run's ID, as in <see cref="StorageScanResult.RunId"/>.</param>
/// <param name="RootFullPath">The root the scan enumerates: the validated, normalised path as entered.</param>
/// <param name="CanonicalRoot">Where Windows says that root really is (aliases resolved), read at scan start; null when
/// Windows cannot report it. Information only: C1 decides nothing from it.</param>
/// <param name="OwnFileNames">The run's own file names (the tripwire set).</param>
internal readonly record struct ScanStartInfo(string RunId, string RootFullPath, string? CanonicalRoot, IReadOnlySet<string> OwnFileNames);

/// <summary>How a scan ended, from the observers' point of view.</summary>
internal enum ScanEndOutcome
{
    /// <summary>The scan finished and v1's reports were written: the result is Complete or Incomplete.</summary>
    Finished,

    /// <summary>The scan was cancelled; nothing of it is valid.</summary>
    Cancelled,

    /// <summary>The scan failed; nothing of it is valid.</summary>
    Failed,
}

/// <summary>The outcome (and, only when Finished, the final totals) of a scan.</summary>
internal readonly record struct ScanEndInfo
{
    private ScanEndInfo(ScanEndOutcome outcome, ScanTotals? totals)
    {
        Outcome = outcome;
        Totals = totals;
    }

    public ScanEndOutcome Outcome { get; }

    /// <summary>The scan result's final totals; non-null exactly when <see cref="Outcome"/> is Finished.</summary>
    public ScanTotals? Totals { get; }

    public static ScanEndInfo Finished(ScanTotals totals) => new(ScanEndOutcome.Finished, totals ?? throw new ArgumentNullException(nameof(totals)));

    public static ScanEndInfo Cancelled() => new(ScanEndOutcome.Cancelled, null);

    public static ScanEndInfo Failed() => new(ScanEndOutcome.Failed, null);
}

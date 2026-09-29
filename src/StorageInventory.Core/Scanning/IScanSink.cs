namespace StorageInventory.Core.Scanning;

/// <summary>
/// Receives observations from the traversal as they happen. The CSV report pipeline is one sink; a future snapshot
/// writer or integration provider can be another, without changing traversal. Internal for v1: it becomes public only
/// when a second real consumer exists.
/// </summary>
/// <remarks>Calls arrive on the scanning thread, in traversal order. A sink must not retain every file record.
/// Exceptions thrown by a sink are NOT treated as scan errors: they abort the scan (for example, a report write
/// failure must never be silently ignored).</remarks>
internal interface IScanSink
{
    /// <param name="folderIndex">Index of the containing folder in the folder table.</param>
    void OnFile(in FileInventoryRecord file, int folderIndex);

    void OnError(ScanErrorRecord error);
}

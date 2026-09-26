namespace StorageInventory.Core;

/// <summary>
/// The single entry point a client (the WPF app, tests) uses to run an inventory.
/// </summary>
/// <remarks>
/// Implementations must re-validate <see cref="StorageScanOptions"/> themselves (callers are never trusted), must
/// only ever read metadata of the scanned tree, and must report every outcome through the returned
/// <see cref="StorageScanResult"/>: the returned task does not fault for scan, output or cancellation problems.
/// It only throws for programming errors such as a null argument.
/// </remarks>
public interface IStorageInventoryScanner
{
    /// <param name="options">What to scan and where to write the reports.</param>
    /// <param name="progress">Receives throttled snapshots. With <see cref="Progress{T}"/> they arrive on the caller's
    /// synchronisation context (for example the UI thread).</param>
    /// <param name="cancellationToken">Stops the scan at the next safe point. All report files are closed, and the
    /// result's state is <see cref="ScanCompletionState.Cancelled"/> with the created files listed as incomplete.</param>
    Task<StorageScanResult> ScanAsync(
        StorageScanOptions options,
        IProgress<StorageScanProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

namespace StorageInventory.Library;

/// <summary>T-DELETE (LIB-12, SEC-07): exactly one published snapshot, in one transaction. These are the only <c>DELETE</c>
/// statements in the product (A-05), and each is constrained by <c>snapshot_id</c>. The attempt row stays; unreferenced
/// <c>name</c> and <c>folder_path</c> rows stay until a later Compact.</summary>
internal static class DeleteSql
{
    /// <summary>Must change exactly one row: only a Published snapshot can be deleted.</summary>
    internal const string MarkDeleting = "UPDATE snapshot SET state = 3 WHERE snapshot_id = $snapshot_id AND state = 2";

    internal const string DeleteFileObs = "DELETE FROM file_obs WHERE snapshot_id = $snapshot_id";
    internal const string DeleteFolderObs = "DELETE FROM folder_obs WHERE snapshot_id = $snapshot_id";
    internal const string DeleteScanErrors = "DELETE FROM scan_error WHERE snapshot_id = $snapshot_id";
    internal const string DeleteExtensionTotals = "DELETE FROM snapshot_extension_total WHERE snapshot_id = $snapshot_id";

    /// <summary>Must change exactly one row.</summary>
    internal const string DeleteSnapshotRow = "DELETE FROM snapshot WHERE snapshot_id = $snapshot_id";
}

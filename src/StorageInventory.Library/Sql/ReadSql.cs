namespace StorageInventory.Library;

/// <summary>The constant SQL of the read-only catalogue queries C4 needs to prove the store (C6 builds the History services on
/// top). Every query is bounded or an aggregate over one snapshot's primary-key range (A-24).</summary>
internal static class ReadSql
{
    /// <summary>The published snapshots, oldest first (capture order is the snapshot id, SCH-11). Bounded by LIMIT.</summary>
    internal const string ListSnapshots = "SELECT snapshot_id, source_id, state, files, folders, bytes FROM snapshot ORDER BY snapshot_id LIMIT $limit";

    internal const string CountFileRows = "SELECT count(*) FROM file_obs WHERE snapshot_id = $snapshot_id";

    internal const string CountFolderRows = "SELECT count(*) FROM folder_obs WHERE snapshot_id = $snapshot_id";

    internal const string CountScanErrorRows = "SELECT count(*) FROM scan_error WHERE snapshot_id = $snapshot_id";

    internal const string CountSnapshotRows = "SELECT count(*) FROM snapshot WHERE snapshot_id = $snapshot_id";

    internal const string SelectSnapshotState = "SELECT state FROM snapshot WHERE snapshot_id = $snapshot_id";

    /// <summary>The attempt, by id. Bounded: one primary-key row.</summary>
    internal const string SelectAttempt = "SELECT outcome, failure_kind, message, source_id FROM scan_attempt WHERE attempt_id = $attempt_id";

    /// <summary>A snapshot's sealed totals and completeness (the verification of a committed snapshot checks the data against them).</summary>
    internal const string SelectSnapshotSealed = "SELECT source_id, files, folders, bytes, scan_errors, completeness FROM snapshot WHERE snapshot_id = $snapshot_id";

    internal const string SelectLibraryInfo = "SELECT library_id, next_snapshot_id, last_opened_app_version FROM library_info WHERE singleton = 1";

    /// <summary>The largest files of a snapshot: a bounded top-N sort (A-24: <c>USE TEMP B-TREE</c> is allowed with a LIMIT).</summary>
    internal const string TopFilesBySize = "SELECT folder_path_id, name_id, size_bytes FROM file_obs WHERE snapshot_id = $snapshot_id ORDER BY size_bytes DESC, seq LIMIT $limit";

    internal const string SelectNameBytes = "SELECT utf16 FROM name WHERE name_id = $name_id";

    internal const string SelectAttemptCountByOutcome = "SELECT count(*) FROM scan_attempt WHERE outcome = $outcome";
}

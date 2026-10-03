namespace StorageInventory.Library;

/// <summary>The constant SQL of the attempt rows (T0, T-OUTCOME) and of T-IMPORT (IMP-02 to IMP-06): dictionary interning, the
/// observation rows, the in-transaction verification of §9.5 (IMP-05) and the final statements. Parameters are always bound;
/// no identifier is ever built from data (A-05, SEC-04). The aggregate verification queries return the NUMBER OF VIOLATIONS
/// unless stated, so 0 is success; their query plans are inspected by TEST-A1 (A-24).</summary>
internal static class ImportSql
{
    // ---- T0 and T-OUTCOME ----
    internal const string InsertAttempt = """
        INSERT INTO scan_attempt (session_token, capture_token, source_id, root_path_as_entered, report_folder, run_id, started_utc, outcome)
        VALUES ($session_token, $capture_token, $source_id, $root_path, $report_folder, $run_id, $started_utc, 1)
        """;

    internal const string RecordAttemptOutcome = """
        UPDATE scan_attempt SET outcome = $outcome, failure_kind = $failure_kind, message = $message, ended_utc = $ended_utc
        WHERE attempt_id = $attempt_id AND session_token = $session_token AND outcome = 1
        """;

    // ---- T-IMPORT: catalogue rows ----
    internal const string SelectNextSnapshotId = "SELECT next_snapshot_id FROM library_info WHERE singleton = 1";

    internal const string SourceExists = "SELECT count(*) FROM source WHERE source_id = $source_id";

    internal const string VolumeExists = "SELECT count(*) FROM volume WHERE volume_id = $volume_id";

    internal const string InsertVolume = """
        INSERT INTO volume (fs_type, serial64, serial32, confidence, display_name, last_label, last_capacity_bytes, first_seen_utc, last_seen_utc)
        VALUES ($fs_type, $serial64, $serial32, $confidence, $display_name, $last_label, $last_capacity_bytes, $now, $now)
        """;

    internal const string InsertSource = """
        INSERT INTO source (kind, volume_id, network_root, network_root_key, root_in_volume, identity_confidence, identity_basis, display_name, created_utc)
        VALUES ($kind, $volume_id, $network_root, $network_root_key, $root_in_volume, $confidence, $basis, $display_name, $now)
        """;

    internal const string InsertSnapshot = """
        INSERT INTO snapshot (
          snapshot_id, source_id, attempt_id, state, completeness, run_id, started_utc, finished_utc, published_utc,
          root_path_as_entered, canonical_root, mount_point, volume_label, capacity_bytes, free_bytes, root_dir_file_id,
          identity_confidence, identity_basis, identity_note, capture_kind, fs_name, library_inside_source, library_rel_path,
          app_version, scanner_contract, spool_format, schema_version, sqlite_version,
          files, folders, bytes, scan_errors, reparse_points_skipped, file_reparse_points, locally_incomplete_folders, affected_ancestor_folders)
        VALUES (
          $snapshot_id, $source_id, $attempt_id, 1, $completeness, $run_id, $started_utc, $finished_utc, NULL,
          $root_path, $canonical_root, $mount_point, $volume_label, $capacity_bytes, $free_bytes, $root_dir_file_id,
          $confidence, $basis, $identity_note, $capture_kind, $fs_name, $library_inside_source, $library_rel_path,
          $app_version, $scanner_contract, $spool_format, $schema_version, $sqlite_version,
          $files, $folders, $bytes, $scan_errors, $reparse_points_skipped, $file_reparse_points, $locally_incomplete_folders, $affected_ancestor_folders)
        """;

    // ---- T-IMPORT: dictionaries (names and folder paths are both per source, SCH-04, D-52) ----
    /// <summary>Every dictionary statement is scoped to the import's source: a name seen by two sources is two rows, one for each.</summary>
    internal const string SelectNameId = "SELECT name_id FROM name WHERE source_id = $source_id AND utf16 = $utf16";

    /// <summary>No <c>RETURNING</c>: the engine's ephemeral result table costs about 8 microseconds a statement (TEST-P1's micro
    /// benchmark), so the new id is read with <c>sqlite3_last_insert_rowid</c> instead (<c>WriterStatement.ExecuteInsert</c>).</summary>
    internal const string InsertName = "INSERT INTO name (source_id, utf16) VALUES ($source_id, $utf16)";

    /// <summary>Insert-first interning for a run of names that are mostly new: one statement instead of a lookup and an insert. A
    /// conflict changes no row, and the importer then looks the existing name up.</summary>
    internal const string InsertNameIfAbsent = "INSERT INTO name (source_id, utf16) VALUES ($source_id, $utf16) ON CONFLICT (source_id, utf16) DO NOTHING";

    internal const string SelectFolderPathId =
        "SELECT path_id FROM folder_path WHERE source_id = $source_id AND parent_path_id IS $parent_path_id AND name_id = $name_id";

    internal const string InsertFolderPath =
        "INSERT INTO folder_path (source_id, parent_path_id, name_id, depth) VALUES ($source_id, $parent_path_id, $name_id, $depth)";

    // ---- T-IMPORT: observation rows ----
    internal const string InsertFolderObs = """
        INSERT INTO folder_obs (snapshot_id, path_id, discovery_index, status, status_reason, subtree_complete, attributes, created_utc, modified_utc,
          direct_bytes, total_bytes, direct_files, total_files, direct_subfolders, total_subfolders, largest_file_bytes)
        VALUES ($snapshot_id, $path_id, $discovery_index, $status, $status_reason, $subtree_complete, $attributes, $created_utc, $modified_utc,
          $direct_bytes, $total_bytes, $direct_files, $total_files, $direct_subfolders, $total_subfolders, $largest_file_bytes)
        """;

    internal const string InsertFileObs = """
        INSERT INTO file_obs (snapshot_id, folder_path_id, name_id, seq, size_bytes, modified_utc, created_utc, accessed_utc, attributes)
        VALUES ($snapshot_id, $folder_path_id, $name_id, $seq, $size_bytes, $modified_utc, $created_utc, $accessed_utc, $attributes)
        """;

    /// <summary>Eight file rows in one statement, with positional parameters (9 per row, in the column order of
    /// <see cref="InsertFileObs"/>): the per-statement cost of the engine and its binding layer is paid once per eight rows.</summary>
    internal const string InsertFileObsBatch8 = """
        INSERT INTO file_obs (snapshot_id, folder_path_id, name_id, seq, size_bytes, modified_utc, created_utc, accessed_utc, attributes)
        VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?), (?, ?, ?, ?, ?, ?, ?, ?, ?), (?, ?, ?, ?, ?, ?, ?, ?, ?), (?, ?, ?, ?, ?, ?, ?, ?, ?),
               (?, ?, ?, ?, ?, ?, ?, ?, ?), (?, ?, ?, ?, ?, ?, ?, ?, ?), (?, ?, ?, ?, ?, ?, ?, ?, ?), (?, ?, ?, ?, ?, ?, ?, ?, ?)
        """;

    internal const string InsertScanError =
        "INSERT INTO scan_error (snapshot_id, seq, rel_path, error_type, message) VALUES ($snapshot_id, $seq, $rel_path, $error_type, $message)";

    internal const string InsertExtensionTotal =
        "INSERT INTO snapshot_extension_total (snapshot_id, extension_key, files, bytes) VALUES ($snapshot_id, $extension_key, $files, $bytes)";

    // ---- IMP-05: verification inside the transaction, before the final statements ----
    /// <summary>(d) The attempt exists, belongs to this session and capture, carries the snapshot's <c>run_id</c> (invariant 13: a
    /// NULL or different run id never matches) and is still InProgress. Must be 1.</summary>
    internal const string VerifyAttempt =
        "SELECT count(*) FROM scan_attempt WHERE attempt_id = $attempt_id AND session_token = $session_token AND capture_token = $capture_token AND run_id = $run_id AND outcome = 1";

    /// <summary>Invariant 1: the snapshot has exactly one folder row for the source's root path, with discovery_index 0.
    /// Returns (count, minimum discovery_index of those rows); must be (1, 0).</summary>
    internal const string VerifyRoot = """
        SELECT count(*), coalesce(min(o.discovery_index), -1)
        FROM folder_obs o JOIN folder_path p ON p.path_id = o.path_id
        WHERE o.snapshot_id = $snapshot_id AND p.parent_path_id IS NULL
        """;

    /// <summary>Invariant 2 (a): every folder row refers to a folder_path row of this source.</summary>
    internal const string VerifyFolderPathsOfSource = """
        SELECT count(*) FROM folder_obs o LEFT JOIN folder_path p ON p.path_id = o.path_id
        WHERE o.snapshot_id = $snapshot_id AND (p.path_id IS NULL OR p.source_id <> $source_id)
        """;

    /// <summary>Invariants 2 (b) and 5, first half: the files of each folder, as (folder_path_id, count, bytes) in primary-key order
    /// (the grouping follows the index, so no temporary B-tree). The verifier merges this stream with <see cref="StreamFolderDirects"/>:
    /// a folder with files but no folder row is an orphan (2b), and each folder's direct counts must equal what is stored (5).</summary>
    internal const string StreamFileFolderTotals =
        "SELECT folder_path_id, count(*), sum(size_bytes) FROM file_obs WHERE snapshot_id = $snapshot_id GROUP BY folder_path_id";

    /// <summary>Invariant 5, second half: each folder's recorded direct counts, in primary-key order (<c>path_id</c> ascending).</summary>
    internal const string StreamFolderDirects =
        "SELECT path_id, direct_files, direct_bytes, direct_subfolders FROM folder_obs WHERE snapshot_id = $snapshot_id";

    /// <summary>Invariant 5, third part: the parent of every non-root folder row, from which the verifier counts each folder's
    /// child folders.</summary>
    internal const string StreamFolderParents = """
        SELECT p.parent_path_id FROM folder_obs o JOIN folder_path p ON p.path_id = o.path_id
        WHERE o.snapshot_id = $snapshot_id AND p.parent_path_id IS NOT NULL
        """;

    /// <summary>Invariant 3 (parent closure): every non-root folder row has a parent row in this snapshot that was listed
    /// (status Ok or Partial) and was discovered earlier.</summary>
    internal const string VerifyParentClosure = """
        SELECT count(*) FROM folder_obs o
        JOIN folder_path p ON p.path_id = o.path_id
        LEFT JOIN folder_obs po ON po.snapshot_id = o.snapshot_id AND po.path_id = p.parent_path_id
        WHERE o.snapshot_id = $snapshot_id AND p.parent_path_id IS NOT NULL
          AND (po.path_id IS NULL OR po.status NOT IN (0, 2) OR po.discovery_index >= o.discovery_index)
        """;

    /// <summary>Invariant 4 (name closure, per source: REV-M06, D-52): every file name exists in the dictionary WITH the snapshot's own
    /// <c>source_id</c>. A name id that exists only for another source does not count.</summary>
    internal const string VerifyFileNames = """
        SELECT count(*) FROM file_obs f LEFT JOIN name n ON n.name_id = f.name_id AND n.source_id = $source_id
        WHERE f.snapshot_id = $snapshot_id AND n.name_id IS NULL
        """;

    /// <summary>Invariant 4 (name closure, per source): every folder's name exists in the dictionary with the snapshot's own source.</summary>
    internal const string VerifyFolderNames = """
        SELECT count(*) FROM folder_obs o JOIN folder_path p ON p.path_id = o.path_id LEFT JOIN name n ON n.name_id = p.name_id AND n.source_id = $source_id
        WHERE o.snapshot_id = $snapshot_id AND n.name_id IS NULL
        """;

    /// <summary>Invariant 6: what the data actually holds. Returns (files, bytes, folders).</summary>
    internal const string MeasureSnapshot = """
        SELECT (SELECT count(*) FROM file_obs WHERE snapshot_id = $snapshot_id),
               (SELECT coalesce(sum(size_bytes), 0) FROM file_obs WHERE snapshot_id = $snapshot_id),
               (SELECT count(*) FROM folder_obs WHERE snapshot_id = $snapshot_id)
        """;

    /// <summary>Invariants 6 and 7: the root's totals and completeness. Returns (total_files, total_bytes, total_subfolders,
    /// subtree_complete) of the root row, or no row.</summary>
    internal const string SelectRootTotals = """
        SELECT o.total_files, o.total_bytes, o.total_subfolders, o.subtree_complete
        FROM folder_obs o JOIN folder_path p ON p.path_id = o.path_id
        WHERE o.snapshot_id = $snapshot_id AND p.parent_path_id IS NULL
        """;

    /// <summary>Invariant 8: a skipped or unreadable folder has no files or child folders.</summary>
    internal const string VerifyNoChildrenOfUnlisted = """
        SELECT count(*) FROM folder_obs o
        WHERE o.snapshot_id = $snapshot_id AND o.status IN (1, 3) AND (
             o.direct_files <> 0 OR o.direct_subfolders <> 0
          OR EXISTS (SELECT 1 FROM file_obs f WHERE f.snapshot_id = o.snapshot_id AND f.folder_path_id = o.path_id)
          OR EXISTS (SELECT 1 FROM folder_path cp JOIN folder_obs c ON c.snapshot_id = o.snapshot_id AND c.path_id = cp.path_id
                     WHERE cp.source_id = $source_id AND cp.parent_path_id = o.path_id))
        """;

    /// <summary>Invariant 9 (a): an Unreadable or Partial folder is not subtree-complete.</summary>
    internal const string VerifyIncompleteFolders = """
        SELECT count(*) FROM folder_obs o WHERE o.snapshot_id = $snapshot_id AND o.status IN (1, 2) AND o.subtree_complete <> 0
        """;

    /// <summary>Invariant 9 (b): an ancestor of an incomplete folder is incomplete too.</summary>
    internal const string VerifyIncompleteAncestors = """
        SELECT count(*) FROM folder_obs c
        JOIN folder_path cp ON cp.path_id = c.path_id
        JOIN folder_obs po ON po.snapshot_id = c.snapshot_id AND po.path_id = cp.parent_path_id
        WHERE c.snapshot_id = $snapshot_id AND c.subtree_complete = 0 AND po.subtree_complete <> 0
        """;

    /// <summary>Invariant 10: scan_errors counts the rows whose type is not informational (codes 8 and 9).</summary>
    internal const string CountRealScanErrors = "SELECT count(*) FROM scan_error WHERE snapshot_id = $snapshot_id AND error_type NOT IN (8, 9)";

    /// <summary>Invariant 11 (a): the sequence of every file row, streamed in primary-key order (no sort). The importer checks
    /// that each value 0 .. files-1 appears exactly once with a bitmap, so no temporary B-tree is needed.</summary>
    internal const string StreamFileSequences = "SELECT seq FROM file_obs WHERE snapshot_id = $snapshot_id";

    /// <summary>Invariant 11 (b): the discovery index of every folder row, streamed likewise.</summary>
    internal const string StreamDiscoveryIndexes = "SELECT discovery_index FROM folder_obs WHERE snapshot_id = $snapshot_id";

    /// <summary>Invariant 12: the extension totals sum to the files and bytes. Returns (files, bytes).</summary>
    internal const string SumExtensionTotals =
        "SELECT coalesce(sum(files), 0), coalesce(sum(bytes), 0) FROM snapshot_extension_total WHERE snapshot_id = $snapshot_id";

    // ---- IMP-06: the final statements, each required to change exactly one row ----
    internal const string PublishSnapshot = "UPDATE snapshot SET state = 2, published_utc = $published_utc WHERE snapshot_id = $snapshot_id AND state = 1";

    /// <summary>Invariant 13 is established here: the attempt becomes Published only if it carries the snapshot's own <c>run_id</c>.</summary>
    internal const string PublishAttempt = """
        UPDATE scan_attempt SET outcome = 2, source_id = $source_id, ended_utc = $ended_utc
        WHERE attempt_id = $attempt_id AND session_token = $session_token AND capture_token = $capture_token AND run_id = $run_id AND outcome = 1
        """;

    internal const string AdvanceSnapshotId = "UPDATE library_info SET next_snapshot_id = next_snapshot_id + 1 WHERE next_snapshot_id = $next_snapshot_id";
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using StorageInventory.Core;
using StorageInventory.Core.Identity;
using StorageInventory.Core.Scanning;

namespace StorageInventory.Library;

/// <summary>
/// T-IMPORT (§10.3, IMP-02 to IMP-06) over an abstract row stream: ONE <c>BEGIN IMMEDIATE</c> transaction that resolves or creates
/// the volume and source, inserts the snapshot (<c>state = 1</c>), interns names and folder paths, inserts the observation rows in
/// key order, verifies the §9.5 invariants and the sealed totals, and only then publishes (<c>state = 2</c>) and commits. Nothing of
/// the snapshot is visible to any connection before the <c>COMMIT</c> (CONC-04), a failure rolls everything back, and no earlier
/// snapshot is ever written (it only inserts rows of its own snapshot id and new dictionary rows). The lease is checked when the
/// writer opens, at <c>BEGIN IMMEDIATE</c>, every 16,384 rows, and immediately before <c>COMMIT</c> (OBS-15).
/// </summary>
internal static class SnapshotImporter
{
    /// <summary>Rows between lease checks (OBS-15) and progress callbacks.</summary>
    internal const int CheckInterval = 16_384;

    /// <summary>IMP-04: the per-capture name cache holds at most this many entries.</summary>
    internal const int NameCacheCapacity = 262_144;

    internal static ImportResult Run(WriterConnection writer, MutationLease lease, byte[] sessionToken, AttemptRef attempt, ImportSourceSpec sourceSpec,
        ImportSnapshotHeader header, ISnapshotRowSource rows, ImportOptions? options, string? journalPath, CancellationToken cancellation)
    {
        var stopwatch = Stopwatch.StartNew();
        long peakJournal = 0;
        void SampleJournal()
        {
            if (journalPath is null) return;
            var info = new FileInfo(journalPath);
            if (info.Exists && info.Length > peakJournal) peakJournal = info.Length;
        }

        try
        {
            writer.Begin(lease);
            var result = Execute(writer, lease, sessionToken, attempt, sourceSpec, header, rows, options, SampleJournal, cancellation, stopwatch);
            writer.Commit(lease);
            SampleJournal();
            return result with { Elapsed = stopwatch.Elapsed, PeakJournalBytes = peakJournal };
        }
        catch (Exception ex)
        {
            RollBackQuietly(writer, lease);
            throw Translate(ex, lease);
        }
    }

    private static void RollBackQuietly(WriterConnection writer, MutationLease lease)
    {
        try { writer.Rollback(lease); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { /* the connection is closed next; SQLite rolls back an open transaction */ }
    }

    /// <summary>The capture failure kind of a SQLite error met during an import (§9.4): a constraint failure is an invariant
    /// violation, a full disk is LibraryFull, and so on.</summary>
    internal static CaptureFailureKind ClassifyFailure(SqliteException sqlite) => sqlite.SqliteErrorCode == 19 ? CaptureFailureKind.InvariantViolation : LibraryDatabase.Classify(sqlite) switch
    {
        DatabaseFailure.DiskFull => CaptureFailureKind.LibraryFull,
        DatabaseFailure.Busy => CaptureFailureKind.LibraryBusy,
        DatabaseFailure.Corrupt or DatabaseFailure.IoError or DatabaseFailure.CannotOpen => CaptureFailureKind.LibraryIoError,
        _ => CaptureFailureKind.LibraryUnavailable,
    };

    /// <summary>Maps a failure to the classified exception T-OUTCOME records. Lease violations, cancellation and class C failures
    /// pass through unchanged (they have their own handling).</summary>
    private static Exception Translate(Exception ex, MutationLease lease)
    {
        switch (ex)
        {
            case LeaseViolationException or OperationCanceledException or ImportException:
                return ex;
            case SqliteException sqlite:
                return new ImportException(ClassifyFailure(sqlite), sqlite.Message, sqlite);
            default:
                if (LibraryInterlock.IsClassC(ex)) lease.ClassCFailure(ex);
                return ex;
        }
    }

    private static ImportResult Execute(WriterConnection writer, MutationLease lease, byte[] sessionToken, AttemptRef attempt, ImportSourceSpec sourceSpec,
        ImportSnapshotHeader header, ISnapshotRowSource rows, ImportOptions? options, Action sampleJournal, CancellationToken cancellation, Stopwatch stopwatch)
    {
        // (d) The attempt exists, is this session's and this capture's, and is InProgress (checked early to avoid wasted work, and
        // again by the final statement, which changes exactly one row).
        using (var verify = writer.Prepare(lease, ImportSql.VerifyAttempt, "$attempt_id", "$session_token", "$capture_token"))
        {
            verify.Set(0, attempt.AttemptId).Set(1, sessionToken).Set(2, attempt.CaptureToken);
            if (Convert.ToInt64(verify.ExecuteScalar(lease)) != 1) throw new ImportException(CaptureFailureKind.InvariantViolation, "The attempt row is not InProgress for this session and capture.");
        }

        var now = DateTime.UtcNow.Ticks;
        var sourceId = ResolveSource(writer, lease, sourceSpec, now);

        long snapshotId;
        using (var next = writer.Prepare(lease, ImportSql.SelectNextSnapshotId))
        {
            snapshotId = Convert.ToInt64(next.ExecuteScalar(lease));
        }
        InsertSnapshotRow(writer, lease, snapshotId, sourceId, attempt, header);

        using var names = new NameCache(writer, lease);
        var counts = new Counters();

        // IMP-03 step 3: folders in ascending discovery index, parents first, interning folder paths.
        var folderCount = rows.FolderCount;
        var pathIds = new long[folderCount];
        var depths = new int[folderCount];
        InsertFolders(writer, lease, rows, snapshotId, sourceId, names, pathIds, depths, counts, options, cancellation);

        // IMP-03 step 4: files, visiting folders in path-id order and inserting each run in name-id order.
        var extensions = new Dictionary<string, (long Files, long Bytes)>(StringComparer.Ordinal);
        InsertFiles(writer, lease, rows, snapshotId, names, pathIds, extensions, counts, options, sampleJournal, cancellation);

        // IMP-03 step 5: error records. Step 6: the extension totals.
        InsertErrors(writer, lease, rows, snapshotId, counts, cancellation);
        InsertExtensionTotals(writer, lease, snapshotId, extensions);
        options?.AfterRows?.Invoke();

        // IMP-05: verification inside the transaction, before the final statements.
        VerifyCounts(header, counts);
        var failure = SnapshotVerifier.Verify(new WriterQueryRunner(writer, lease), snapshotId, sourceId, header.Files, header.Bytes, header.Folders, header.ScanErrors,
            header.Completion == ScanCompletionState.Complete);
        if (failure is not null) throw new ImportException(CaptureFailureKind.InvariantViolation, "The snapshot failed its in-transaction verification: " + failure);
        writer.CheckCurrent(lease);

        // IMP-06: the final statements, each required to change exactly one row.
        var published = DateTime.UtcNow.Ticks;
        using (var publish = writer.Prepare(lease, ImportSql.PublishSnapshot, "$published_utc", "$snapshot_id"))
        {
            ExpectOneRow(publish.Set(0, published).Set(1, snapshotId).ExecuteNonQuery(lease), "publish the snapshot");
        }
        using (var outcome = writer.Prepare(lease, ImportSql.PublishAttempt, "$source_id", "$ended_utc", "$attempt_id", "$session_token", "$capture_token"))
        {
            ExpectOneRow(outcome.Set(0, sourceId).Set(1, published).Set(2, attempt.AttemptId).Set(3, sessionToken).Set(4, attempt.CaptureToken).ExecuteNonQuery(lease), "record the attempt outcome");
        }
        using (var advance = writer.Prepare(lease, ImportSql.AdvanceSnapshotId, "$next_snapshot_id"))
        {
            ExpectOneRow(advance.Set(0, snapshotId).ExecuteNonQuery(lease), "advance the snapshot sequence");
        }

        return new ImportResult(snapshotId, sourceId, counts.Files, counts.Folders, counts.Errors, names.NewNames, stopwatch.Elapsed, 0);
    }

    private static void ExpectOneRow(int changed, string what)
    {
        if (changed != 1) throw new ImportException(CaptureFailureKind.InvariantViolation, $"Expected to {what} in exactly one row, but {changed} rows changed.");
    }

    private static long ResolveSource(WriterConnection writer, MutationLease lease, ImportSourceSpec spec, long now)
    {
        if (spec is ImportSourceSpec.Existing existing)
        {
            using var exists = writer.Prepare(lease, ImportSql.SourceExists, "$source_id");
            if (Convert.ToInt64(exists.Set(0, existing.SourceId).ExecuteScalar(lease)) != 1)
            {
                throw new ImportException(CaptureFailureKind.SourceChanged, "The chosen source no longer exists.");
            }
            return existing.SourceId;
        }

        var created = (ImportSourceSpec.NewSource)spec;
        long? volumeId = null;
        if (created.Kind == SourceKind.LocalVolume)
        {
            if (created.ExistingVolumeId is { } existingVolume)
            {
                using var exists = writer.Prepare(lease, ImportSql.VolumeExists, "$volume_id");
                if (Convert.ToInt64(exists.Set(0, existingVolume).ExecuteScalar(lease)) != 1)
                {
                    throw new ImportException(CaptureFailureKind.SourceChanged, "The chosen volume no longer exists.");
                }
                volumeId = existingVolume;
            }
            else
            {
                var volume = created.NewVolume ?? throw new ImportException(CaptureFailureKind.InvariantViolation, "A new local source needs a volume.");
                using var insert = writer.Prepare(lease, ImportSql.InsertVolume, "$fs_type", "$serial64", "$serial32", "$confidence", "$display_name", "$last_label", "$last_capacity_bytes", "$now");
                volumeId = Convert.ToInt64(insert.Set(0, volume.FsType).Set(1, volume.Serial64).Set(2, volume.Serial32).Set(3, StableCodes.ToCode(volume.Confidence))
                    .Set(4, volume.DisplayName).Set(5, volume.LastLabel).Set(6, volume.CapacityBytes).Set(7, now).ExecuteScalar(lease));
            }
        }

        using var source = writer.Prepare(lease, ImportSql.InsertSource, "$kind", "$volume_id", "$network_root", "$network_root_key", "$root_in_volume", "$confidence", "$basis", "$display_name", "$now");
        return Convert.ToInt64(source.Set(0, StableCodes.ToCode(created.Kind)).Set(1, volumeId).Set(2, created.NetworkRoot).Set(3, created.NetworkRootKey).Set(4, created.RootInVolume)
            .Set(5, StableCodes.ToCode(created.Confidence)).Set(6, StableCodes.ToCode(created.Basis)).Set(7, created.DisplayName).Set(8, now).ExecuteScalar(lease));
    }

    private static void InsertSnapshotRow(WriterConnection writer, MutationLease lease, long snapshotId, long sourceId, AttemptRef attempt, ImportSnapshotHeader h)
    {
        using var insert = writer.Prepare(lease, ImportSql.InsertSnapshot,
            "$snapshot_id", "$source_id", "$attempt_id", "$completeness", "$run_id", "$started_utc", "$finished_utc",
            "$root_path", "$canonical_root", "$mount_point", "$volume_label", "$capacity_bytes", "$free_bytes", "$root_dir_file_id",
            "$confidence", "$basis", "$identity_note", "$capture_kind", "$fs_name", "$library_inside_source", "$library_rel_path",
            "$app_version", "$scanner_contract", "$spool_format", "$schema_version", "$sqlite_version",
            "$files", "$folders", "$bytes", "$scan_errors", "$reparse_points_skipped", "$file_reparse_points", "$locally_incomplete_folders", "$affected_ancestor_folders");
        insert.Set(0, snapshotId).Set(1, sourceId).Set(2, attempt.AttemptId).Set(3, StableCodes.ToStoredCompletion(h.Completion)).Set(4, h.RunId)
            .Set(5, h.StartedUtcTicks).Set(6, h.FinishedUtcTicks).Set(7, h.RootPathAsEntered).Set(8, h.CanonicalRoot).Set(9, h.MountPoint).Set(10, h.VolumeLabel)
            .Set(11, h.CapacityBytes).Set(12, h.FreeBytes).Set(13, h.RootDirFileId).Set(14, StableCodes.ToCode(h.Confidence)).Set(15, StableCodes.ToCode(h.Basis))
            .Set(16, h.IdentityNote).Set(17, StableCodes.ToCode(h.CaptureKind)).Set(18, h.FsName).Set(19, StableCodes.ToCode(h.LibraryInsideSource)).Set(20, h.LibraryRelPath)
            .Set(21, LibraryNames.AppVersion).Set(22, StableCodes.ScannerContractV1).Set(23, StableCodes.SpoolFormat1).Set(24, LibraryNames.SchemaVersion)
            .Set(25, LibraryNames.PinnedSqliteVersion)
            .Set(26, h.Files).Set(27, h.Folders).Set(28, h.Bytes).Set(29, h.ScanErrors).Set(30, h.ReparsePointsSkipped).Set(31, h.FileReparsePoints)
            .Set(32, h.LocallyIncompleteFolders).Set(33, h.AffectedAncestorFolders);
        insert.ExecuteNonQuery(lease);
    }

    private static void InsertFolders(WriterConnection writer, MutationLease lease, ISnapshotRowSource rows, long snapshotId, long sourceId, NameCache names,
        long[] pathIds, int[] depths, Counters counts, ImportOptions? options, CancellationToken cancellation)
    {
        using var select = writer.Prepare(lease, ImportSql.SelectFolderPathId, "$source_id", "$parent_path_id", "$name_id");
        using var insertPath = writer.Prepare(lease, ImportSql.InsertFolderPath, "$source_id", "$parent_path_id", "$name_id", "$depth");
        using var insertObs = writer.Prepare(lease, ImportSql.InsertFolderObs, "$snapshot_id", "$path_id", "$discovery_index", "$status", "$status_reason", "$subtree_complete",
            "$attributes", "$created_utc", "$modified_utc", "$direct_bytes", "$total_bytes", "$direct_files", "$total_files", "$direct_subfolders", "$total_subfolders", "$largest_file_bytes");
        var expected = 0;
        foreach (var folder in rows.Folders())
        {
            if (folder.Index != expected || expected >= pathIds.Length) throw new ImportException(CaptureFailureKind.InvariantViolation, "The folder section is out of order.");
            if ((expected == 0) != (folder.ParentIndex < 0) || folder.ParentIndex >= folder.Index) throw new ImportException(CaptureFailureKind.InvariantViolation, "A folder's parent does not precede it.");
            var nameId = names.Intern(folder.Name);
            long? parent = folder.ParentIndex < 0 ? null : pathIds[folder.ParentIndex];
            var depth = folder.ParentIndex < 0 ? 0 : depths[folder.ParentIndex] + 1;

            var existing = select.Set(0, sourceId).Set(1, parent).Set(2, nameId).ExecuteScalar(lease);
            long pathId;
            if (existing is not null) pathId = Convert.ToInt64(existing);
            else pathId = Convert.ToInt64(insertPath.Set(0, sourceId).Set(1, parent).Set(2, nameId).Set(3, depth).ExecuteScalar(lease));
            pathIds[expected] = pathId;
            depths[expected] = depth;

            insertObs.Set(0, snapshotId).Set(1, pathId).Set(2, folder.Index).Set(3, StableCodes.ToCode(folder.Status))
                .Set(4, folder.StatusReason is { } reason ? StableCodes.ToCode(reason) : null).Set(5, folder.SubtreeComplete ? 1 : 0).Set(6, folder.Attributes)
                .Set(7, folder.CreatedTicks).Set(8, folder.ModifiedTicks).Set(9, folder.DirectBytes).Set(10, folder.TotalBytes).Set(11, folder.DirectFiles)
                .Set(12, folder.TotalFiles).Set(13, folder.DirectSubfolders).Set(14, folder.TotalSubfolders).Set(15, folder.LargestFileBytes)
                .ExecuteNonQuery(lease);
            counts.Folders++;
            expected++;
            if (expected % CheckInterval == 0)
            {
                writer.CheckCurrent(lease);
                cancellation.ThrowIfCancellationRequested();
            }
        }
        if (expected != pathIds.Length) throw new ImportException(CaptureFailureKind.InvariantViolation, "The folder section ended early.");
    }

    private static void InsertFiles(WriterConnection writer, MutationLease lease, ISnapshotRowSource rows, long snapshotId, NameCache names, long[] pathIds,
        Dictionary<string, (long Files, long Bytes)> extensions, Counters counts, ImportOptions? options, Action sampleJournal, CancellationToken cancellation)
    {
        var order = new int[pathIds.Length];
        for (var i = 0; i < order.Length; i++) order[i] = i;
        var keys = (long[])pathIds.Clone();
        Array.Sort(keys, order);

        using var insert = writer.Prepare(lease, ImportSql.InsertFileObs, "$snapshot_id", "$folder_path_id", "$name_id", "$seq", "$size_bytes", "$modified_utc", "$created_utc", "$accessed_utc", "$attributes");
        var nextCheck = CheckInterval;
        foreach (var folderIndex in order)
        {
            var files = rows.FilesOfFolder(folderIndex);
            if (files.Count == 0) continue;
            var pathId = pathIds[folderIndex];
            var ids = new long[files.Count];
            var sorted = new int[files.Count];
            for (var i = 0; i < files.Count; i++)
            {
                ids[i] = names.Intern(files[i].Name);
                sorted[i] = i;
            }
            Array.Sort(ids, sorted);
            for (var k = 0; k < sorted.Length; k++)
            {
                var file = files[sorted[k]];
                insert.Set(0, snapshotId).Set(1, pathId).Set(2, ids[k]).Set(3, file.Seq).Set(4, file.Size).Set(5, file.ModifiedTicks).Set(6, file.CreatedTicks)
                    .Set(7, file.AccessedTicks).Set(8, file.Attributes).ExecuteNonQuery(lease);
                var key = ExtensionKey(file.Name);
                extensions[key] = extensions.TryGetValue(key, out var total) ? (total.Files + 1, total.Bytes + file.Size) : (1, file.Size);
                counts.Files++;
                if (counts.Files >= nextCheck)
                {
                    nextCheck += CheckInterval;
                    writer.CheckCurrent(lease);
                    cancellation.ThrowIfCancellationRequested();
                    sampleJournal();
                    options?.OnFileRows?.Invoke(counts.Files);
                }
            }
        }
    }

    /// <summary>The extension key of a file name (<c>ScanEngine.ExtensionOf</c>, lower-invariant), as the string the name's exact
    /// UTF-16 code units spell (an unpaired surrogate is kept as it is, never replaced).</summary>
    private static string ExtensionKey(byte[] name) => ScanEngine.ExtensionOf(Utf16.ToString(name)).ToLowerInvariant();

    private static void InsertErrors(WriterConnection writer, MutationLease lease, ISnapshotRowSource rows, long snapshotId, Counters counts, CancellationToken cancellation)
    {
        using var insert = writer.Prepare(lease, ImportSql.InsertScanError, "$snapshot_id", "$seq", "$rel_path", "$error_type", "$message");
        foreach (var error in rows.Errors())
        {
            insert.Set(0, snapshotId).Set(1, error.Seq).Set(2, error.RelativePath).Set(3, StableCodes.ToCode(error.Type)).Set(4, error.Message).ExecuteNonQuery(lease);
            counts.Errors++;
            if (!error.Type.IsInformational()) counts.RealErrors++;
            if (counts.Errors % CheckInterval == 0)
            {
                writer.CheckCurrent(lease);
                cancellation.ThrowIfCancellationRequested();
            }
        }
    }

    private static void InsertExtensionTotals(WriterConnection writer, MutationLease lease, long snapshotId, Dictionary<string, (long Files, long Bytes)> extensions)
    {
        using var insert = writer.Prepare(lease, ImportSql.InsertExtensionTotal, "$snapshot_id", "$extension_key", "$files", "$bytes");
        foreach (var (key, total) in extensions.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            insert.Set(0, snapshotId).Set(1, Utf16.ToBytes(key)).Set(2, total.Files).Set(3, total.Bytes).ExecuteNonQuery(lease);
        }
    }

    /// <summary>IMP-05 (b): the numbers of rows inserted equal the sealed totals.</summary>
    private static void VerifyCounts(ImportSnapshotHeader header, Counters counts)
    {
        if (counts.Files != header.Files) throw new ImportException(CaptureFailureKind.InvariantViolation, $"{counts.Files} file rows were inserted but the sealed total is {header.Files}.");
        if (counts.Folders != header.Folders) throw new ImportException(CaptureFailureKind.InvariantViolation, $"{counts.Folders} folder rows were inserted but the sealed total is {header.Folders}.");
        if (counts.RealErrors != header.ScanErrors) throw new ImportException(CaptureFailureKind.InvariantViolation, $"{counts.RealErrors} real error rows were inserted but the sealed total is {header.ScanErrors}.");
    }

    private sealed class Counters
    {
        internal long Files;
        internal long Folders;
        internal long Errors;
        internal long RealErrors;
    }

    /// <summary>IMP-04: a name cache per capture (created when T-IMPORT begins, discarded when it ends, committed or rolled back, so a
    /// rolled-back <c>name_id</c> can never be reused), bounded at <see cref="NameCacheCapacity"/> entries and backed by lookups
    /// inside the transaction. Eviction is by two generations: when the newer one is full it becomes the older one and the older
    /// one is dropped, so recently used names stay cached.</summary>
    private sealed class NameCache : IDisposable
    {
        private readonly WriterStatement _select;
        private readonly WriterStatement _insert;
        private readonly MutationLease _lease;
        private Dictionary<string, long> _young = new(StringComparer.Ordinal);
        private Dictionary<string, long> _old = new(StringComparer.Ordinal);

        internal NameCache(WriterConnection writer, MutationLease lease)
        {
            _lease = lease;
            _select = writer.Prepare(lease, ImportSql.SelectNameId, "$utf16");
            _insert = writer.Prepare(lease, ImportSql.InsertName, "$utf16");
        }

        internal long NewNames { get; private set; }

        internal long Intern(byte[] name)
        {
            var key = Utf16.ToString(name);
            if (_young.TryGetValue(key, out var id)) return id;
            if (_old.TryGetValue(key, out id))
            {
                Remember(key, id);
                return id;
            }
            var found = _select.Set(0, name).ExecuteScalar(_lease);
            if (found is not null) id = Convert.ToInt64(found);
            else
            {
                id = Convert.ToInt64(_insert.Set(0, name).ExecuteScalar(_lease));
                NewNames++;
            }
            Remember(key, id);
            return id;
        }

        private void Remember(string key, long id)
        {
            if (_young.Count >= NameCacheCapacity / 2)
            {
                _old = _young;
                _young = new Dictionary<string, long>(StringComparer.Ordinal);
            }
            _young[key] = id;
        }

        public void Dispose()
        {
            _select.Dispose();
            _insert.Dispose();
        }
    }
}

/// <summary>Exact UTF-16LE conversion between .NET strings and the stored bytes (NAME-01): nothing is validated, trimmed or
/// replaced, so an unpaired surrogate survives a round trip.</summary>
internal static class Utf16
{
    internal static string ToString(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length % 2 != 0) throw new LibraryDataException("A stored name has an odd number of bytes.");
        return new string(MemoryMarshal.Cast<byte, char>(bytes));
    }

    internal static byte[] ToBytes(string text) => MemoryMarshal.AsBytes(text.AsSpan()).ToArray();
}

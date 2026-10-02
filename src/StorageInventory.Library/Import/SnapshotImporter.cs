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
            var commitStart = Stopwatch.GetTimestamp();
            writer.Commit(lease);
            var commit = Stopwatch.GetElapsedTime(commitStart);
            SampleJournal();
            return result with { Elapsed = stopwatch.Elapsed, PeakJournalBytes = peakJournal, Phases = result.Phases! with { Commit = commit } };
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
        var phase = Stopwatch.GetTimestamp();
        InsertFolders(writer, lease, rows, snapshotId, sourceId, names, pathIds, depths, counts, options, cancellation);
        var foldersTime = Stopwatch.GetElapsedTime(phase);

        // IMP-03 step 4: files, visiting folders in path-id order and inserting each run in name-id order.
        phase = Stopwatch.GetTimestamp();
        var extensions = new ExtensionTotals();
        InsertFiles(writer, lease, rows, snapshotId, names, pathIds, extensions, counts, options, sampleJournal, cancellation);
        var filesTime = Stopwatch.GetElapsedTime(phase);

        // IMP-03 step 5: error records. Step 6: the extension totals.
        phase = Stopwatch.GetTimestamp();
        InsertErrors(writer, lease, rows, snapshotId, counts, cancellation);
        InsertExtensionTotals(writer, lease, snapshotId, extensions);
        var errorsTime = Stopwatch.GetElapsedTime(phase);
        options?.AfterRows?.Invoke();

        // IMP-05: verification inside the transaction, before the final statements. Cancelling here rolls everything back (CAN-01d).
        cancellation.ThrowIfCancellationRequested();
        VerifyCounts(header, counts);
        phase = Stopwatch.GetTimestamp();
        var failure = SnapshotVerifier.Verify(new WriterQueryRunner(writer, lease), snapshotId, sourceId, header.Files, header.Bytes, header.Folders, header.ScanErrors,
            header.Completion == ScanCompletionState.Complete);
        var verificationTime = Stopwatch.GetElapsedTime(phase);
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

        return new ImportResult(snapshotId, sourceId, counts.Files, counts.Folders, counts.Errors, names.NewNames, stopwatch.Elapsed, 0,
            new ImportPhases(foldersTime, filesTime, errorsTime, verificationTime, TimeSpan.Zero));
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
                volumeId = insert.Set(0, volume.FsType).Set(1, volume.Serial64).Set(2, volume.Serial32).Set(3, StableCodes.ToCode(volume.Confidence))
                    .Set(4, volume.DisplayName).Set(5, volume.LastLabel).Set(6, volume.CapacityBytes).Set(7, now).ExecuteInsert(lease);
            }
        }

        using var source = writer.Prepare(lease, ImportSql.InsertSource, "$kind", "$volume_id", "$network_root", "$network_root_key", "$root_in_volume", "$confidence", "$basis", "$display_name", "$now");
        return source.Set(0, StableCodes.ToCode(created.Kind)).Set(1, volumeId).Set(2, created.NetworkRoot).Set(3, created.NetworkRootKey).Set(4, created.RootInVolume)
            .Set(5, StableCodes.ToCode(created.Confidence)).Set(6, StableCodes.ToCode(created.Basis)).Set(7, created.DisplayName).Set(8, now).ExecuteInsert(lease);
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
            else pathId = insertPath.Set(0, sourceId).Set(1, parent).Set(2, nameId).Set(3, depth).ExecuteInsert(lease);
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
        ExtensionTotals extensions, Counters counts, ImportOptions? options, Action sampleJournal, CancellationToken cancellation)
    {
        var order = new int[pathIds.Length];
        for (var i = 0; i < order.Length; i++) order[i] = i;
        var keys = (long[])pathIds.Clone();
        Array.Sort(keys, order);

        using var single = writer.Prepare(lease, ImportSql.InsertFileObs, "$snapshot_id", "$folder_path_id", "$name_id", "$seq", "$size_bytes", "$modified_utc", "$created_utc", "$accessed_utc", "$attributes");
        using var batch = writer.Prepare(lease, ImportSql.InsertFileObsBatch8);
        const int BatchRows = 8;
        var pending = new (long Folder, long Name, ImportFile File)[BatchRows];
        var pendingCount = 0;
        var nextCheck = CheckInterval;

        void Flush()
        {
            if (pendingCount == BatchRows)
            {
                for (var r = 0; r < BatchRows; r++)
                {
                    var o = r * 9;
                    var (folder, name, file) = pending[r];
                    batch.Set(o, snapshotId).Set(o + 1, folder).Set(o + 2, name).Set(o + 3, file.Seq).Set(o + 4, file.Size).Set(o + 5, file.ModifiedTicks)
                        .Set(o + 6, file.CreatedTicks).Set(o + 7, file.AccessedTicks).Set(o + 8, file.Attributes);
                }
                batch.ExecuteNonQuery(lease);
            }
            else
            {
                for (var r = 0; r < pendingCount; r++)
                {
                    var (folder, name, file) = pending[r];
                    single.Set(0, snapshotId).Set(1, folder).Set(2, name).Set(3, file.Seq).Set(4, file.Size).Set(5, file.ModifiedTicks).Set(6, file.CreatedTicks)
                        .Set(7, file.AccessedTicks).Set(8, file.Attributes).ExecuteNonQuery(lease);
                }
            }
            pendingCount = 0;
        }

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
                pending[pendingCount++] = (pathId, ids[k], file);
                extensions.Add(file.Name, file.Size);
                counts.Files++;
                if (pendingCount == BatchRows) Flush();
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
        Flush();
    }

    /// <summary>The per-extension file and byte totals of one snapshot (<c>snapshot_extension_total</c>). The key is
    /// <c>ScanEngine.ExtensionOf(name)</c> lower-invariant, as v1's own rule. Spellings are cached by their exact code units, so
    /// the common case (an extension seen before) allocates nothing; two spellings that lower to the same key share one total.</summary>
    private sealed class ExtensionTotals
    {
        private sealed class Total
        {
            internal long Files;
            internal long Bytes;
        }

        private readonly Dictionary<string, Total> _byKey = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Total> _bySpelling = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Total>.AlternateLookup<ReadOnlySpan<char>> _lookup;

        internal ExtensionTotals() => _lookup = _bySpelling.GetAlternateLookup<ReadOnlySpan<char>>();

        internal void Add(byte[] name, long size)
        {
            var chars = MemoryMarshal.Cast<byte, char>(name);
            var dot = chars.LastIndexOf('.');
            // the same rule as ScanEngine.ExtensionOf: from the last '.', unless the name ends with '.' (or has none)
            var extension = dot >= 0 && dot < chars.Length - 1 ? chars[dot..] : [];
            if (!_lookup.TryGetValue(extension, out var total))
            {
                var spelling = extension.ToString();
                var key = ScanEngine.ExtensionOf(Utf16.ToString(name)).ToLowerInvariant();
                if (!_byKey.TryGetValue(key, out total)) _byKey[key] = total = new Total();
                _bySpelling[spelling] = total;
            }
            total.Files++;
            total.Bytes += size;
        }

        internal IEnumerable<(string Key, long Files, long Bytes)> Ordered() =>
            _byKey.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => (e.Key, e.Value.Files, e.Value.Bytes));
    }

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

    private static void InsertExtensionTotals(WriterConnection writer, MutationLease lease, long snapshotId, ExtensionTotals extensions)
    {
        using var insert = writer.Prepare(lease, ImportSql.InsertExtensionTotal, "$snapshot_id", "$extension_key", "$files", "$bytes");
        foreach (var (key, files, bytes) in extensions.Ordered())
        {
            insert.Set(0, snapshotId).Set(1, Utf16.ToBytes(key)).Set(2, files).Set(3, bytes).ExecuteNonQuery(lease);
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
        private readonly WriterStatement _insertIfAbsent;
        private readonly MutationLease _lease;
        private double _newRate = 0.25;   // a moving share of the cache misses that turned out to be NEW names
        private Dictionary<byte[], long> _young = new(ByteArrayComparer.Instance);
        private Dictionary<byte[], long> _old = new(ByteArrayComparer.Instance);

        internal NameCache(WriterConnection writer, MutationLease lease)
        {
            _lease = lease;
            _select = writer.Prepare(lease, ImportSql.SelectNameId, "$utf16");
            _insert = writer.Prepare(lease, ImportSql.InsertName, "$utf16");
            _insertIfAbsent = writer.Prepare(lease, ImportSql.InsertNameIfAbsent, "$utf16");
        }

        internal long NewNames { get; private set; }

        internal long Intern(byte[] name)
        {
            if (_young.TryGetValue(name, out var id)) return id;
            if (_old.TryGetValue(name, out id))
            {
                Remember(name, id);
                return id;
            }
            // A name that is not cached is either already in the dictionary (a later snapshot of a source: look it up) or new (the first
            // snapshot of a source: insert it). The moving share of new names among the misses chooses the cheaper first statement:
            // look-up-first costs a probe and, for a new name, an insert; insert-first costs one insert, and a probe more for an old name.
            bool isNew;
            if (_newRate > 0.5)
            {
                isNew = _insertIfAbsent.Set(0, name).ExecuteInsertIfAbsent(_lease, out id);
                if (!isNew) id = LookUp(name);
            }
            else
            {
                var found = _select.Set(0, name).ExecuteScalar(_lease);
                isNew = found is null;
                id = isNew ? _insert.Set(0, name).ExecuteInsert(_lease) : (long)found!;
            }
            if (isNew) NewNames++;
            _newRate = _newRate * 0.995 + (isNew ? 0.005 : 0);
            Remember(name, id);
            return id;
        }

        private long LookUp(byte[] name) =>
            (long?)_select.Set(0, name).ExecuteScalar(_lease) ?? throw new ImportException(CaptureFailureKind.InvariantViolation, "A name that conflicted on insert could not be found.");

        private void Remember(byte[] name, long id)
        {
            if (_young.Count >= NameCacheCapacity / 2)
            {
                _old = _young;
                _young = new Dictionary<byte[], long>(ByteArrayComparer.Instance);
            }
            _young[name] = id;
        }

        public void Dispose()
        {
            _select.Dispose();
            _insert.Dispose();
            _insertIfAbsent.Dispose();
        }
    }
}

/// <summary>Content equality and hashing for exact name bytes (the cache's key), so that no string is made per lookup.</summary>
internal sealed class ByteArrayComparer : IEqualityComparer<byte[]>
{
    internal static readonly ByteArrayComparer Instance = new();

    public bool Equals(byte[]? x, byte[]? y) => x is not null && y is not null && x.AsSpan().SequenceEqual(y);

    public int GetHashCode(byte[] value)
    {
        var hash = new HashCode();
        hash.AddBytes(value);
        return hash.ToHashCode();
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

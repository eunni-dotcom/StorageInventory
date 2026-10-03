using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using StorageInventory.Core;
using StorageInventory.Core.Identity;
using StorageInventory.Core.Scanning;

namespace StorageInventory.Library;

/// <summary>
/// T-IMPORT (§10.3, IMP-02 to IMP-06, IMP-11) over an abstract row stream: ONE <c>BEGIN IMMEDIATE</c> transaction that resolves or
/// creates the volume and source, inserts the snapshot (<c>state = 1</c>), interns names and folder paths IN THE SOURCE'S OWN
/// DICTIONARY (SCH-04, D-52), inserts the observation rows in key order, verifies the §9.5 invariants and the sealed totals, and only
/// then publishes (<c>state = 2</c>) and commits. Nothing of the snapshot is visible to any connection before the <c>COMMIT</c>
/// (CONC-04), a failure rolls everything back, and no earlier snapshot is ever written (it only inserts rows of its own snapshot id
/// and new dictionary rows of its own source).
/// <para><b>Checks.</b> At <c>BEGIN</c>, after every <see cref="GuardInterval"/> inserted rows of ANY table (the dictionary rows of
/// <c>name</c> and <c>folder_path</c> included, wherever in the import they are inserted: a folder boundary resets nothing) and in a
/// final check immediately before <c>COMMIT</c> (after the verification and the final statements, with nothing in between), the import
/// checks that its lease is current (OBS-15), looks at the save token (CAN-01d: a cancelled token rolls everything back) and consults the
/// space guard (IMP-11). <b>Time bound.</b> Work that inserts nothing, or little (a name looked up in the dictionary, a slow row), is
/// counted too: after every <see cref="ClockPoll"/> units of work the clock is read, and when <see cref="MaxLookGap"/> has passed since the
/// last look at the token the lease and the token are looked at (CAN-03: no two looks more than 0.5 s apart); the space guard keeps its
/// own row cadence, since growth is driven by rows. Inside the verification the token is observed between queries, every 65,536 rows of
/// a streamed query and by the engine's progress callback inside a statement; after the verification it is checked once more. The
/// final check is the last look at the token: a cancellation after it is not observed and the save publishes (CAN-01e).</para>
/// <para>Every method takes the lease it works under (A-25 (a)): there is no lease-holding object.</para>
/// </summary>
internal static class SnapshotImporter
{
    /// <summary>File rows between <see cref="ImportOptions.OnFileRows"/> callbacks.</summary>
    internal const int CheckInterval = 16_384;

    /// <summary>IMP-11 / CAN-01d: inserted rows (of any table) between checks (lease, save token, space guard).</summary>
    internal const int GuardInterval = 4_096;

    /// <summary>CAN-03: units of work (a row inserted, a name looked up) between two readings of the clock. A reading costs tens of
    /// nanoseconds against microseconds per unit, so the bound below costs nothing measurable, and a unit that costs a few milliseconds
    /// (a stalled disk) still keeps a look within about <see cref="ClockPoll"/> units of <see cref="MaxLookGap"/>.</summary>
    internal const int ClockPoll = 64;

    /// <summary>CAN-03: the longest the import goes without looking at the save token (a fifth of the 0.5 s budget of CAN-01d, which
    /// is what remains for the rollback's own detection and the units between two clock readings).</summary>
    internal static readonly TimeSpan MaxLookGap = TimeSpan.FromMilliseconds(100);

    /// <summary>IMP-04: the per-capture, per-source name cache holds at most this many entries.</summary>
    internal const int NameCacheCapacity = 262_144;

    /// <summary>The main file's and the journal's lengths through handles (never through a directory listing, which can lag).</summary>
    internal delegate (long Main, long Journal) FileLengths();

    /// <summary>What one import carries from check to check. It holds no lease.</summary>
    private sealed class ImportState(CancellationToken cancellation, Func<long>? clock)
    {
        internal TokenChecker Checker { get; } = new(cancellation, clock);
        internal long Rows;
        internal long NextGuard = GuardInterval;
        internal long Work;
        internal int SpaceChecks;
        internal long PeakJournal;
        internal long FinalPending = -1;
        internal long FinalMain = -1;
        internal long NextFileCallback = CheckInterval;
    }

    internal static ImportResult Run(WriterConnection writer, MutationLease lease, byte[] sessionToken, AttemptRef attempt, ImportSourceSpec sourceSpec,
        ImportSnapshotHeader header, ISnapshotRowSource rows, ImportOptions? options, FileLengths? lengths, CancellationToken cancellation)
    {
        var stopwatch = Stopwatch.StartNew();
        var state = new ImportState(cancellation, options?.Clock);
        try
        {
            writer.Begin(lease);
            GuardPoint(writer, lease, state, options, lengths, SpaceCheckKind.Begin);
            var result = Execute(writer, lease, sessionToken, attempt, sourceSpec, header, rows, options, lengths, state, cancellation, stopwatch);
            state.Checker.Close();
            var commitStart = Stopwatch.GetTimestamp();
            writer.Commit(lease);
            var commit = Stopwatch.GetElapsedTime(commitStart);
            var commitGrowth = -1L;
            if (lengths is not null && state.FinalMain >= 0)
            {
                try { commitGrowth = Math.Max(0, lengths().Main - state.FinalMain); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a measurement only: the commit has happened */ }
            }
            var checks = new ImportChecks(state.Checker.Observations, state.Checker.MaxGap, state.SpaceChecks, state.FinalPending, commitGrowth);
            return result with { Elapsed = stopwatch.Elapsed, PeakJournalBytes = state.PeakJournal, Phases = result.Phases! with { Commit = commit }, Checks = checks };
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

    /// <summary>An input of the space guard that could not be read (the engine's page count or page size, a file length): the import
    /// stops as <c>LibraryFull</c>, class A (IMP-11: an unknown free space is never treated as enough). A lease violation, a
    /// cancellation and a class C failure are not unreadable inputs and pass through.</summary>
    private static bool IsUnreadable(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or SqliteException or FormatException or InvalidCastException or OverflowException or ArgumentException
        || (ex is InvalidOperationException and not LeaseViolationException);

    /// <summary>One check of the primitive (IMP-11, CAN-01d, OBS-15): the lease is current, the save token is looked at (a cancelled
    /// token throws and the caller rolls back), and the space guard is consulted with exact inputs. <paramref name="kind"/> is Final
    /// only for the check immediately before <c>COMMIT</c>.</summary>
    private static void GuardPoint(WriterConnection writer, MutationLease lease, ImportState state, ImportOptions? options, FileLengths? lengths, SpaceCheckKind kind)
    {
        writer.CheckCurrent(lease);
        state.Checker.Check();

        var guard = options?.SpaceGuard;
        var record = options?.OnSpaceCheck;
        if (guard is null && record is null && lengths is null) return;

        SpaceCheck check;
        try
        {
            if (lengths is null) throw new IOException("The lengths of the Library files are not available.");
            var pageCount = ReadEngineInput(writer, lease, options, kind, EngineInput.PageCount);
            var pageSize = ReadEngineInput(writer, lease, options, kind, EngineInput.PageSize);
            var (main, journal) = lengths();
            check = new SpaceCheck(kind, PendingGrowth(pageCount, pageSize, main), pageCount, pageSize, main, journal, state.Rows);
        }
        catch (Exception ex) when (IsUnreadable(ex))
        {
            if (guard is null && record is null) return;   // nobody consults the numbers: an unreadable length is not a failure
            throw new ImportException(CaptureFailureKind.LibraryFull, $"An input of the space check at {kind} could not be read, so the free space cannot be judged.", ex);
        }

        state.SpaceChecks++;
        if (check.JournalLength > state.PeakJournal) state.PeakJournal = check.JournalLength;
        if (kind == SpaceCheckKind.Final)
        {
            state.FinalPending = check.PendingGrowth;
            state.FinalMain = check.MainFileLength;
            state.PeakJournal = check.JournalLength;   // a TRUNCATE journal is at its largest immediately before COMMIT: the reported peak
        }
        record?.Invoke(check);
        if (guard is null) return;

        bool permit;
        try
        {
            permit = guard.Permit(check);
        }
        catch (Exception ex) when (!LibraryInterlock.IsClassC(ex) && ex is not LeaseViolationException && !(ex is OperationCanceledException && state.Checker.Token.IsCancellationRequested))
        {
            throw new ImportException(CaptureFailureKind.LibraryFull, $"The space guard failed at {kind}: {ex.Message}", ex);
        }
        if (!permit) throw new ImportException(CaptureFailureKind.LibraryFull, $"The space guard stopped the import at {kind}: the drive holding the Library became nearly full while saving.");
    }

    /// <summary>One engine-side input of a check, read from the writer inside the transaction (IMP-11: at EVERY check, never cached);
    /// the test seam sees the value and may replace it or fail.</summary>
    private static long ReadEngineInput(WriterConnection writer, MutationLease lease, ImportOptions? options, SpaceCheckKind kind, EngineInput input)
    {
        var value = input == EngineInput.PageCount ? writer.PageCount(lease) : writer.PageSize(lease);
        return options?.OnEngineInput is { } seam ? seam(input, kind, value) : value;
    }

    /// <summary>IMP-11's pending main-file growth <c>Λ</c> = max(0, <c>page_count</c> × page size − the main file's length): the pages
    /// of the transaction's image the file does not hold yet. All arithmetic is in 64 bits and checked; an input that is out of range
    /// (a negative page count or length, a page size of 0 or less, a product that overflows) is an input that cannot be read, so the
    /// check fails closed and the import rolls back as <c>LibraryFull</c>.</summary>
    internal static long PendingGrowth(long pageCount, long pageSize, long mainFileLength)
    {
        if (pageCount < 0 || pageSize <= 0 || mainFileLength < 0) throw new FormatException("An input of the space check is out of range.");
        return Math.Max(0L, checked(pageCount * pageSize) - mainFileLength);
    }

    /// <summary>Accounts for work done since the last call: <paramref name="inserted"/> rows inserted into ANY table (file, folder,
    /// folder-path, name, error and extension-total rows) and <paramref name="worked"/> further units that inserted nothing (a name
    /// found in the cache or the dictionary). Two independent bounds apply, and neither is ever reset by a folder boundary because both
    /// live in <see cref="ImportState"/>: a full check (lease, save token, space guard) after every <see cref="GuardInterval"/> inserted
    /// rows (IMP-11 (3), CAN-01d), and a look at the lease and the token whenever <see cref="MaxLookGap"/> has passed since the last
    /// look, tested after every <see cref="ClockPoll"/> units of work (CAN-03).</summary>
    private static void Advance(WriterConnection writer, MutationLease lease, ImportState state, ImportOptions? options, FileLengths? lengths, int inserted, int worked = 0)
    {
        state.Rows += inserted;
        if (state.Rows >= state.NextGuard)
        {
            while (state.NextGuard <= state.Rows) state.NextGuard += GuardInterval;
            state.Work = 0;
            GuardPoint(writer, lease, state, options, lengths, SpaceCheckKind.Rows);
            options?.Probe?.Invoke(ImportPoint.AfterRowCheck);
            return;
        }
        state.Work += inserted + worked;
        if (state.Work < ClockPoll) return;
        state.Work = 0;
        if (state.Checker.SinceLast < MaxLookGap) return;
        writer.CheckCurrent(lease);
        state.Checker.Check();
    }

    /// <summary>Interns one name and accounts for it: a name new to the source's dictionary is an inserted row of <c>name</c>, and a
    /// look-up that inserts nothing is still a unit of work. This is the loop that a folder of 100,000 names or more makes long.</summary>
    private static long Intern(WriterConnection writer, MutationLease lease, NameCache names, byte[] name, ImportState state, ImportOptions? options, FileLengths? lengths)
    {
        var before = names.NewNames;
        var id = names.Intern(lease, name);
        Advance(writer, lease, state, options, lengths, inserted: (int)(names.NewNames - before), worked: 1);
        return id;
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
        ImportSnapshotHeader header, ISnapshotRowSource rows, ImportOptions? options, FileLengths? lengths, ImportState state, CancellationToken cancellation, Stopwatch stopwatch)
    {
        // (d) The attempt exists, is this session's and this capture's, carries this snapshot's run id (invariant 13) and is InProgress
        // (checked early to avoid wasted work, and again by the final statement, which changes exactly one row).
        using (var verify = writer.Prepare(lease, ImportSql.VerifyAttempt, "$attempt_id", "$session_token", "$capture_token", "$run_id"))
        {
            verify.Set(0, attempt.AttemptId).Set(1, sessionToken).Set(2, attempt.CaptureToken).Set(3, header.RunId);
            if (Convert.ToInt64(verify.ExecuteScalar(lease)) != 1) throw new ImportException(CaptureFailureKind.InvariantViolation, "The attempt row is not InProgress for this session, capture and run.");
        }

        var now = DateTime.UtcNow.Ticks;
        var sourceId = ResolveSource(writer, lease, sourceSpec, now);

        long snapshotId;
        using (var next = writer.Prepare(lease, ImportSql.SelectNextSnapshotId))
        {
            snapshotId = Convert.ToInt64(next.ExecuteScalar(lease));
        }
        InsertSnapshotRow(writer, lease, snapshotId, sourceId, attempt, header);

        // IMP-04: the name cache belongs to this capture AND to this source: it is created here, after the source is known, and
        // discarded when the import ends, so no name id of another source or of an earlier (rolled-back) import can ever be reused.
        using var names = new NameCache(writer, lease, sourceId);
        var counts = new Counters();

        // IMP-03 step 3: folders in ascending discovery index, parents first, interning folder paths.
        var folderCount = rows.FolderCount;
        var pathIds = new long[folderCount];
        var depths = new int[folderCount];
        var phase = Stopwatch.GetTimestamp();
        InsertFolders(writer, lease, rows, snapshotId, sourceId, names, pathIds, depths, counts, options, lengths, state);
        var foldersTime = Stopwatch.GetElapsedTime(phase);

        // IMP-03 step 4: files, visiting folders in path-id order and inserting each run in name-id order.
        phase = Stopwatch.GetTimestamp();
        var extensions = new ExtensionTotals();
        InsertFiles(writer, lease, rows, snapshotId, names, pathIds, extensions, counts, options, lengths, state);
        var filesTime = Stopwatch.GetElapsedTime(phase);

        // IMP-03 step 5: error records. Step 6: the extension totals.
        phase = Stopwatch.GetTimestamp();
        InsertErrors(writer, lease, rows, snapshotId, counts, options, lengths, state);
        InsertExtensionTotals(writer, lease, snapshotId, extensions, options, lengths, state);
        var errorsTime = Stopwatch.GetElapsedTime(phase);
        options?.AfterRows?.Invoke();

        // IMP-05: verification inside the transaction, before the final statements. A cancellation anywhere in it rolls everything
        // back (CAN-01d): the token is looked at between queries, every 65,536 rows of a streamed query and, inside a statement, by
        // the engine's progress callback, which exists only for the duration of the verification and only on this connection.
        state.Checker.Check();
        VerifyCounts(header, counts);
        phase = Stopwatch.GetTimestamp();
        string? failure;
        using (writer.BeginCancellationScope(lease, cancellation, state.Checker))
        {
            var runner = new DelegateQueryRunner(
                (sql, parameters) => writer.Scalar(lease, sql, parameters),
                (sql, each, parameters) => writer.Rows(lease, sql, each, parameters));
            failure = SnapshotVerifier.Verify(runner, snapshotId, sourceId, header.Files, header.Bytes, header.Folders, header.ScanErrors,
                header.Completion == ScanCompletionState.Complete,
                new VerificationHooks { Checkpoint = state.Checker.Check, Stage = options?.Probe });
        }
        var verificationTime = Stopwatch.GetElapsedTime(phase);
        if (failure is not null) throw new ImportException(CaptureFailureKind.InvariantViolation, "The snapshot failed its in-transaction verification: " + failure);

        // the token check that follows the verification (CAN-01d), then the lease check
        state.Checker.Check();
        writer.CheckCurrent(lease);

        // IMP-06: the final statements, each required to change exactly one row. The fourth cancel point lies between them (§15.4).
        var published = DateTime.UtcNow.Ticks;
        using (var publish = writer.Prepare(lease, ImportSql.PublishSnapshot, "$published_utc", "$snapshot_id"))
        {
            ExpectOneRow(publish.Set(0, published).Set(1, snapshotId).ExecuteNonQuery(lease), "publish the snapshot");
        }
        options?.Probe?.Invoke(ImportPoint.FinalStatements);
        using (var outcome = writer.Prepare(lease, ImportSql.PublishAttempt, "$source_id", "$ended_utc", "$attempt_id", "$session_token", "$capture_token", "$run_id"))
        {
            ExpectOneRow(outcome.Set(0, sourceId).Set(1, published).Set(2, attempt.AttemptId).Set(3, sessionToken).Set(4, attempt.CaptureToken).Set(5, header.RunId).ExecuteNonQuery(lease), "record the attempt outcome");
        }
        using (var advance = writer.Prepare(lease, ImportSql.AdvanceSnapshotId, "$next_snapshot_id"))
        {
            ExpectOneRow(advance.Set(0, snapshotId).ExecuteNonQuery(lease), "advance the snapshot sequence");
        }

        // IMP-11's final check, immediately before COMMIT (nothing runs between it and the commit): lease, save token, space guard.
        // It is the last look at the token; a cancellation after it is not observed and the save publishes (CAN-01e).
        GuardPoint(writer, lease, state, options, lengths, SpaceCheckKind.Final);

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
        long[] pathIds, int[] depths, Counters counts, ImportOptions? options, FileLengths? lengths, ImportState state)
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
            var nameId = Intern(writer, lease, names, folder.Name, state, options, lengths);
            long? parent = folder.ParentIndex < 0 ? null : pathIds[folder.ParentIndex];
            var depth = folder.ParentIndex < 0 ? 0 : depths[folder.ParentIndex] + 1;

            var existing = select.Set(0, sourceId).Set(1, parent).Set(2, nameId).ExecuteScalar(lease);
            long pathId;
            var pathRows = 0;
            if (existing is not null) pathId = Convert.ToInt64(existing);
            else
            {
                pathId = insertPath.Set(0, sourceId).Set(1, parent).Set(2, nameId).Set(3, depth).ExecuteInsert(lease);
                pathRows = 1;
            }
            pathIds[expected] = pathId;
            depths[expected] = depth;

            insertObs.Set(0, snapshotId).Set(1, pathId).Set(2, folder.Index).Set(3, StableCodes.ToCode(folder.Status))
                .Set(4, folder.StatusReason is { } reason ? StableCodes.ToCode(reason) : null).Set(5, folder.SubtreeComplete ? 1 : 0).Set(6, folder.Attributes)
                .Set(7, folder.CreatedTicks).Set(8, folder.ModifiedTicks).Set(9, folder.DirectBytes).Set(10, folder.TotalBytes).Set(11, folder.DirectFiles)
                .Set(12, folder.TotalFiles).Set(13, folder.DirectSubfolders).Set(14, folder.TotalSubfolders).Set(15, folder.LargestFileBytes)
                .ExecuteNonQuery(lease);
            counts.Folders++;
            expected++;
            Advance(writer, lease, state, options, lengths, inserted: 1 + pathRows);
        }
        if (expected != pathIds.Length) throw new ImportException(CaptureFailureKind.InvariantViolation, "The folder section ended early.");
    }

    private static void InsertFiles(WriterConnection writer, MutationLease lease, ISnapshotRowSource rows, long snapshotId, NameCache names, long[] pathIds,
        ExtensionTotals extensions, Counters counts, ImportOptions? options, FileLengths? lengths, ImportState state)
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
                Advance(writer, lease, state, options, lengths, inserted: BatchRows);
            }
            else
            {
                for (var r = 0; r < pendingCount; r++)
                {
                    var (folder, name, file) = pending[r];
                    single.Set(0, snapshotId).Set(1, folder).Set(2, name).Set(3, file.Seq).Set(4, file.Size).Set(5, file.ModifiedTicks).Set(6, file.CreatedTicks)
                        .Set(7, file.AccessedTicks).Set(8, file.Attributes).ExecuteNonQuery(lease);
                    Advance(writer, lease, state, options, lengths, inserted: 1);
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
                ids[i] = Intern(writer, lease, names, files[i].Name, state, options, lengths);
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
                if (counts.Files >= state.NextFileCallback)
                {
                    state.NextFileCallback += CheckInterval;
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

    private static void InsertErrors(WriterConnection writer, MutationLease lease, ISnapshotRowSource rows, long snapshotId, Counters counts, ImportOptions? options,
        FileLengths? lengths, ImportState state)
    {
        using var insert = writer.Prepare(lease, ImportSql.InsertScanError, "$snapshot_id", "$seq", "$rel_path", "$error_type", "$message");
        foreach (var error in rows.Errors())
        {
            insert.Set(0, snapshotId).Set(1, error.Seq).Set(2, error.RelativePath).Set(3, StableCodes.ToCode(error.Type)).Set(4, error.Message).ExecuteNonQuery(lease);
            counts.Errors++;
            if (!error.Type.IsInformational()) counts.RealErrors++;
            Advance(writer, lease, state, options, lengths, inserted: 1);
        }
    }

    private static void InsertExtensionTotals(WriterConnection writer, MutationLease lease, long snapshotId, ExtensionTotals extensions, ImportOptions? options,
        FileLengths? lengths, ImportState state)
    {
        using var insert = writer.Prepare(lease, ImportSql.InsertExtensionTotal, "$snapshot_id", "$extension_key", "$files", "$bytes");
        foreach (var (key, files, bytes) in extensions.Ordered())
        {
            insert.Set(0, snapshotId).Set(1, Utf16.ToBytes(key)).Set(2, files).Set(3, bytes).ExecuteNonQuery(lease);
            Advance(writer, lease, state, options, lengths, inserted: 1);
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

    /// <summary>IMP-04: a name cache per capture AND per source (D-52). It is created when T-IMPORT has resolved its source and is
    /// discarded when the import ends, committed or rolled back, so a rolled-back <c>name_id</c> can never be reused and an id of
    /// another source can never be handed out: every statement it runs is scoped to <see cref="SourceId"/>, and the dictionary rows
    /// it creates belong to that source alone. Bounded at <see cref="NameCacheCapacity"/> entries and backed by lookups inside the
    /// transaction. Eviction is by two generations: when the newer one is full it becomes the older one and the older one is
    /// dropped, so recently used names stay cached. Every call takes the lease it works under (A-25 (a)).</summary>
    private sealed class NameCache : IDisposable
    {
        private readonly long _sourceId;
        private readonly WriterStatement _select;
        private readonly WriterStatement _insert;
        private readonly WriterStatement _insertIfAbsent;
        private double _newRate = 0.25;   // a moving share of the cache misses that turned out to be NEW names
        private Dictionary<byte[], long> _young = new(ByteArrayComparer.Instance);
        private Dictionary<byte[], long> _old = new(ByteArrayComparer.Instance);

        internal NameCache(WriterConnection writer, MutationLease lease, long sourceId)
        {
            _sourceId = sourceId;
            _select = writer.Prepare(lease, ImportSql.SelectNameId, "$source_id", "$utf16");
            _insert = writer.Prepare(lease, ImportSql.InsertName, "$source_id", "$utf16");
            _insertIfAbsent = writer.Prepare(lease, ImportSql.InsertNameIfAbsent, "$source_id", "$utf16");
        }

        /// <summary>The one source this cache serves.</summary>
        internal long SourceId => _sourceId;

        internal long NewNames { get; private set; }

        internal long Intern(MutationLease lease, byte[] name)
        {
            if (_young.TryGetValue(name, out var id)) return id;
            if (_old.TryGetValue(name, out id))
            {
                Remember(name, id);
                return id;
            }
            // A name that is not cached is either already in the source's dictionary (a later snapshot of the source: look it up) or
            // new to it (the first snapshot of a source: insert it). The moving share of new names among the misses chooses the
            // cheaper first statement: look-up-first costs a probe and, for a new name, an insert; insert-first costs one insert, and
            // a probe more for an old name.
            bool isNew;
            if (_newRate > 0.5)
            {
                isNew = _insertIfAbsent.Set(0, _sourceId).Set(1, name).ExecuteInsertIfAbsent(lease, out id);
                if (!isNew) id = LookUp(lease, name);
            }
            else
            {
                var found = _select.Set(0, _sourceId).Set(1, name).ExecuteScalar(lease);
                isNew = found is null;
                id = isNew ? _insert.Set(0, _sourceId).Set(1, name).ExecuteInsert(lease) : (long)found!;
            }
            if (isNew) NewNames++;
            _newRate = _newRate * 0.995 + (isNew ? 0.005 : 0);
            Remember(name, id);
            return id;
        }

        private long LookUp(MutationLease lease, byte[] name) =>
            (long?)_select.Set(0, _sourceId).Set(1, name).ExecuteScalar(lease) ?? throw new ImportException(CaptureFailureKind.InvariantViolation, "A name that conflicted on insert could not be found.");

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

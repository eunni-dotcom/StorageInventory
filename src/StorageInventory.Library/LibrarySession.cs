using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using StorageInventory.Core;
using StorageInventory.History.Library;

namespace StorageInventory.Library;

/// <summary>Options for a session. Production passes none; tests use them to reach real engine faults.</summary>
internal sealed class LibrarySessionOptions
{
    internal LibraryFaultInjection? Faults { get; init; }
    internal LibraryStoreHooks? StoreHooks { get; init; }
}

/// <summary>A read was requested while the Library is not readable (any state but Available, or Faulted). Class A: a state, not a crash.</summary>
internal sealed class LibraryUnavailableException(string message) : InvalidOperationException(message);

/// <summary>The result of a set-aside (LIB-13).</summary>
/// <param name="Quarantine">What the renames did; null when the lock could not be taken (nothing was renamed).</param>
/// <param name="Status">The state derived afterwards.</param>
internal sealed record SetAsideResult(QuarantineResult? Quarantine, LibraryStatus Status);

/// <summary>The row of a T0 insert (§10.3). Names and paths are exact UTF-16 bytes (SCH-10).</summary>
internal sealed record AttemptStart(byte[] CaptureToken, long? SourceId, byte[] RootPathAsEntered, byte[]? ReportFolder, string? RunId, long StartedUtcTicks);

/// <summary>
/// One process's view of the Inventory Library: the writer lock, the Library activity interlock (constructed already in
/// Mutating(Open) holding the start-up lease, OBS-14), the in-process reader/writer gate (CONC-06), the derived state (§6.8)
/// and the operations that need a lease: open and recovery (LIB-08), creation (LIB-07), set-aside (LIB-13), the attempt rows,
/// the one-transaction import (T-IMPORT) and snapshot deletion (T-DELETE). Every mutating operation takes a lease and checks it is
/// current BEFORE any I/O (OBS-15); every Library API takes an explicit directory (LIB-04).
/// <para><b>Nothing here wires the scanner:</b> capture, the spool and the UI are C5.</para>
/// </summary>
internal sealed class LibrarySession
{
    private enum OpenMode
    {
        /// <summary>LIB-08: never creates anything except the 0-byte lock file in an existing directory.</summary>
        Open,

        /// <summary>LIB-07 inside a Prepare lease (the first save): re-derives under the lock and opens a Library found there.</summary>
        ImplicitCreate,

        /// <summary>LIB-07 inside a Create lease (the explicit "Create a new, empty Library").</summary>
        ExplicitCreate,
    }

    private readonly object _statusLock = new();
    private readonly LibraryFaultInjection? _faults;
    private LibraryStatus? _derived;
    private bool _everHadLibrary;
    private long _captureCounter;

    internal LibrarySession(string libraryDirectory, string appDataRoot, LibrarySessionOptions? options = null)
    {
        _faults = options?.Faults;
        Interlock = new LibraryInterlock();
        Store = new LibraryStore(libraryDirectory, appDataRoot, Interlock, options?.StoreHooks);
        Interlock.StateChanged += OnInterlockChanged;
    }

    internal LibraryInterlock Interlock { get; }

    internal LibraryStore Store { get; }

    internal ReaderWriterGate Gate { get; } = new();

    /// <summary>16 random bytes naming this session; written into every attempt it inserts (CONC-08).</summary>
    internal byte[] SessionToken { get; } = RandomNumberGenerator.GetBytes(16);

    /// <summary>Raised (outside any lock) when the derived state changes.</summary>
    internal event Action<LibraryStatus>? StatusChanged;

    /// <summary>The Library state (§6.8). While the start-up open is running it is Unavailable ("opening"); once the interlock
    /// is Faulted it is Unavailable, restart required, whatever it was before (OBS-13).</summary>
    internal LibraryStatus Status
    {
        get
        {
            var directory = Store.Directory;
            if (Interlock.IsFaulted)
            {
                return new LibraryStatus(LibraryState.Unavailable, LibraryReason.RestartRequired, LibraryInterlock.FaultedReason, false, true, directory);
            }
            lock (_statusLock)
            {
                return _derived ?? LibraryStatus.Of(LibraryState.Unavailable, "opening", LibraryInterlock.OpeningReason, directory);
            }
        }
    }

    /// <summary>A fresh id for a capture or run (leases that belong to a capture carry it as their owner).</summary>
    internal long NewCaptureId() => Interlocked.Increment(ref _captureCounter);

    private void OnInterlockChanged(InterlockSnapshot state)
    {
        if (state.Kind == InterlockStateKind.Faulted)
        {
            Gate.Close();   // best-effort release of every reader (OBS-13); no further reads are served
            StatusChanged?.Invoke(Status);
        }
    }

    // ---- opening ----

    /// <summary>The start-up open (OBS-14, LIB-08): runs under the start-up Open lease the interlock was constructed holding, and
    /// only when it ends cleanly does the interlock reach Idle for the first time, whatever state was derived.</summary>
    internal LibraryStatus RunStartupOpen()
    {
        var lease = Interlock.TakeStartupLease();
        try
        {
            return Derive(lease, OpenMode.Open);
        }
        finally
        {
            lease.Dispose();
        }
    }

    /// <summary>"Retry" (LIB-08): an Open lease granted from Idle. Refused, with the reason, while anything else runs.</summary>
    internal bool TryRetryOpen(out LibraryStatus status, out string refusal)
    {
        if (!Interlock.TryBeginMutation(MutationKind.Open, 0, out var lease, out refusal))
        {
            status = Status;
            return false;
        }
        try
        {
            status = Derive(lease, OpenMode.Open);
            return true;
        }
        finally
        {
            lease.Dispose();
        }
    }

    /// <summary>LIB-08 under a lease the caller holds (an Open lease).</summary>
    internal LibraryStatus Open(MutationLease lease) => Derive(lease, OpenMode.Open);

    /// <summary>The explicit "Create a new, empty Library" (LIB-07 with a Create lease): lock first, then the state is re-derived
    /// under the lock; an existing Library is opened and never replaced.</summary>
    internal LibraryStatus CreateLibrary(MutationLease lease) => Derive(lease, OpenMode.ExplicitCreate);

    /// <summary>P1's creation (LIB-07 with a Prepare lease): the first save. Re-derives under the lock; a Library that appeared
    /// meanwhile is opened, a Missing one is never recreated silently, leftovers are refused.</summary>
    internal LibraryStatus PrepareForSave(MutationLease lease) => Derive(lease, OpenMode.ImplicitCreate);

    private LibraryStatus Derive(MutationLease lease, OpenMode mode)
    {
        MutationKind[] allowed = mode switch
        {
            OpenMode.Open => [MutationKind.Open],
            OpenMode.ImplicitCreate => [MutationKind.Prepare],
            _ => [MutationKind.Create],
        };
        Interlock.Require(lease, mode == OpenMode.Open ? "open the Library" : "create or open the Library", allowed);

        LibraryStatus result;
        try
        {
            result = DeriveCore(lease, mode);
        }
        catch (Exception ex) when (LibraryInterlock.IsClassC(ex))
        {
            lease.ClassCFailure(ex);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UnexpectedEngineException or EngineConfigurationException)
        {
            result = Fail(ex is UnexpectedEngineException ? LibraryReason.UnexpectedEngine : ex is UnauthorizedAccessException ? LibraryReason.AccessDenied : LibraryReason.IoError, ex.Message);
        }
        Publish(result);
        return result;
    }

    private LibraryStatus DeriveCore(MutationLease lease, OpenMode mode)
    {
        // LIB-08 step 1 / LIB-07 step 2: path validation.
        var path = Store.ValidatePath();
        if (path.Blocked) return Fail(path.FirstBlocking!.Code == "LibraryThroughLink" ? LibraryReason.ReparsePoint : LibraryReason.PathBlocked, path.FirstBlocking.Message + (path.FirstBlocking.Details is { } d ? " (" + d + ")" : ""));

        // LIB-08 step 2: if the directory does not exist the open ends here; it creates no directory and holds no lock.
        if (!Store.DirectoryExists())
        {
            if (mode == OpenMode.Open) return State(LibraryState.NotCreated, LibraryReason.DirectoryMissing, "There is no Library yet.");
            var created = Store.EnsureLibraryFolder(lease);
            if (created.Blocked) return Fail(LibraryReason.PathBlocked, created.FirstBlocking!.Message);
        }

        // The writer lock FIRST (CONC-01): before any member is inspected, created or renamed.
        var locked = Store.AcquireWriterLock(lease);
        if (locked.Outcome == LockOutcome.InUse) return State(LibraryState.InUse, LibraryReason.LockedByAnotherProcess, "The Library is open in another StorageInventory window.");
        if (locked.Outcome == LockOutcome.Unavailable) return Fail(LibraryReason.IoError, locked.Detail ?? "The Library lock could not be taken.");

        // LIB-08 step 3 / LIB-07 step 4: re-derive the members UNDER THE LOCK; never trust an earlier observation.
        var members = Store.InspectMembersUnderLock();
        if (members.Wal.Exists || members.Shm.Exists)
        {
            return State(LibraryState.NotALibrary, LibraryReason.WalOrShmPresent, "The Library files have been modified by another program (a -wal or -shm file is present).");
        }
        var mainUsable = members.Main.Exists && members.Main.Length > 0;
        if (!mainUsable && members.Journal.NonEmpty)
        {
            return State(LibraryState.LeftoverFiles, LibraryReason.JournalWithoutMain, "Files from an interrupted Library remain here.");
        }
        if (mainUsable) return OpenExisting(lease);

        // Main file absent or 0 bytes, and no non-empty journal.
        var existedBefore = Store.LockPreExisted || _everHadLibrary;
        switch (mode)
        {
            case OpenMode.Open:
                return members.Main.Exists || !existedBefore
                    ? State(LibraryState.NotCreated, LibraryReason.None, "There is no Library yet.")
                    : State(LibraryState.Missing, LibraryReason.MainFileMissingLockExisted, "The Library was not found.");
            case OpenMode.ImplicitCreate when !members.Main.Exists && existedBefore:
                return State(LibraryState.Missing, LibraryReason.MainFileMissingLockExisted, "The Library was not found; it is never recreated silently.");
            default:
                return CreateDatabase(lease, members);
        }
    }

    /// <summary>LIB-07 steps 5 to 7: the 0-byte main file (CreateNew, never an overwrite), then T-CREATE in one transaction.</summary>
    private LibraryStatus CreateDatabase(MutationLease lease, MemberSet members)
    {
        if (!members.Main.Exists && !Store.CreateEmptyDatabase(lease))
        {
            return Fail(LibraryReason.IoError, "The database file appeared while the Library was being created; nothing was overwritten. Try again.");
        }
        var allowed = new[] { MutationKind.Create, MutationKind.Prepare };
        WriterConnection writer;
        try
        {
            writer = LibraryDatabase.OpenWriter(lease, Store.MainPath, "T-CREATE", allowed, importCache: false, _faults);
        }
        catch (SqliteException ex)
        {
            return FromSqliteFailure(ex);
        }
        using (writer)
        {
            try
            {
                writer.Begin(lease);
                Execute(writer, lease, SchemaSql.SetApplicationId);
                Execute(writer, lease, SchemaSql.SetUserVersion);
                foreach (var ddl in SchemaSql.CreateAll) Execute(writer, lease, ddl);
                using (var info = writer.Prepare(lease, SchemaSql.InsertLibraryInfo, "$library_id", "$created_utc", "$app_version"))
                {
                    info.Set(0, Guid.NewGuid().ToString("D")).Set(1, DateTime.UtcNow.Ticks).Set(2, LibraryNames.AppVersion).ExecuteNonQuery(lease);
                }
                writer.Commit(lease);
            }
            catch (SqliteException ex)
            {
                RollBackQuietly(writer, lease);
                return FromSqliteFailure(ex);   // the main file stays 0 bytes or schema-less; SQLite rolled its journal back (LIB-07 step 7)
            }
            catch (Exception)
            {
                RollBackQuietly(writer, lease);
                throw;
            }
        }
        _everHadLibrary = true;
        return State(LibraryState.Available, LibraryReason.None, "The Library was created.");
    }

    private static void Execute(WriterConnection writer, MutationLease lease, string sql)
    {
        using var statement = writer.Prepare(lease, sql);
        statement.ExecuteNonQuery(lease);
    }

    private static void RollBackQuietly(WriterConnection writer, MutationLease lease)
    {
        try { writer.Rollback(lease); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { /* the connection is closed next */ }
    }

    /// <summary>LIB-08 steps 4 to 9 for a main file that is present and not empty.</summary>
    private LibraryStatus OpenExisting(MutationLease lease)
    {
        // Step 4: the header pre-check, WITHOUT SQLite. SQLite never opens a file that fails it (SEC-32).
        var header = Store.ReadHeader();
        switch (header.Outcome)
        {
            case HeaderOutcome.Absent:
                return Fail(LibraryReason.IoError, "The database file disappeared while it was being opened.");
            case HeaderOutcome.TooShort:
                return State(LibraryState.NotALibrary, LibraryReason.TooShort, "The file is too short to be a Library.");
            case HeaderOutcome.ForeignFile:
                return State(LibraryState.NotALibrary, LibraryReason.ForeignFile, "The file is not a StorageInventory Library.");
            case HeaderOutcome.WalFormat:
                return State(LibraryState.NotALibrary, LibraryReason.WalHeader, "The file is in write-ahead-log format, which a Library never is.");
            case HeaderOutcome.WrongApplicationId:
                return State(LibraryState.NotALibrary, LibraryReason.WrongApplicationId, "The file is not a StorageInventory Library.");
            case HeaderOutcome.UnsupportedUserVersion:
                return State(LibraryState.NotALibrary, LibraryReason.UnsupportedUserVersion, "The file has an unsupported schema version.");
            case HeaderOutcome.NewerSchema:
                return State(LibraryState.Incompatible, LibraryReason.NewerSchema, $"This Library was created by a newer StorageInventory (schema {header.UserVersion}). Update the app.");
        }

        // Step 5: the writer connection, every pragma, the engine assertion. A hot journal is rolled back by SQLite here.
        var allowed = new[] { MutationKind.Open, MutationKind.Create, MutationKind.Prepare };
        WriterConnection writer;
        try
        {
            writer = LibraryDatabase.OpenWriter(lease, Store.MainPath, "open the Library", allowed, importCache: false, _faults);
        }
        catch (SqliteException ex)
        {
            return FromSqliteFailure(ex);
        }

        bool recoveryPending;
        using (writer)
        {
            try
            {
                var q = new WriterQueryRunner(writer, lease);
                // Step 6: application id, schema version and the schema fingerprint, re-read through SQLite.
                if (Convert.ToInt64(q.Scalar(OpenSql.GetApplicationId)) != LibraryNames.ApplicationId)
                {
                    return State(LibraryState.NotALibrary, LibraryReason.WrongApplicationId, "The file is not a StorageInventory Library.");
                }
                var userVersion = Convert.ToInt64(q.Scalar(OpenSql.GetUserVersion));
                if (userVersion > LibraryNames.SchemaVersion) return State(LibraryState.Incompatible, LibraryReason.NewerSchema, $"This Library was created by a newer StorageInventory (schema {userVersion}).");
                if (userVersion != LibraryNames.SchemaVersion) return State(LibraryState.NotALibrary, LibraryReason.UnsupportedUserVersion, "The file has an unsupported schema version.");
                var rows = new List<SchemaRow>();
                q.Rows(OpenSql.SelectSchemaRows, r => rows.Add(new SchemaRow(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3))));
                var differences = LibrarySchema.Differences(rows);
                if (differences.Count > 0) return State(LibraryState.NotALibrary, LibraryReason.SchemaFingerprint, "The Library does not have the expected structure: " + string.Join("; ", differences.Take(3)));

                // Step 7: the committed-state check. A committed snapshot not Published proves SQLite's atomic commit was defeated.
                if (Convert.ToInt64(q.Scalar(OpenSql.CountUnpublishedSnapshots)) != 0)
                {
                    return State(LibraryState.Damaged, LibraryReason.NotPublishedSnapshot, "The Library holds a snapshot that was never completed.");
                }

                // Step 8: T-RECOVER, best effort, only under the lock (CONC-08).
                recoveryPending = !TryRecover(writer, lease);
            }
            catch (SqliteException ex)
            {
                return FromSqliteFailure(ex);
            }
        }

        _everHadLibrary = true;
        var status = new LibraryStatus(LibraryState.Available, LibraryReason.None, recoveryPending ? "The Library is available; recovery is pending." : "The Library is available.", recoveryPending, false, Store.Directory);
        return status;
    }

    /// <summary>T-RECOVER (§10.3, CONC-08): attempts left InProgress by ANOTHER session become Interrupted, and the last-opened app
    /// version is recorded. Never touches an attempt of this session. Returns false when it could not complete.</summary>
    private bool TryRecover(WriterConnection writer, MutationLease lease)
    {
        try
        {
            writer.Begin(lease);
            using (var recover = writer.Prepare(lease, OpenSql.RecoverInterruptedAttempts, "$ended_utc", "$session_token"))
            {
                recover.Set(0, DateTime.UtcNow.Ticks).Set(1, SessionToken).ExecuteNonQuery(lease);
            }
            using (var touch = writer.Prepare(lease, OpenSql.TouchLastOpened, "$app_version"))
            {
                touch.Set(0, LibraryNames.AppVersion).ExecuteNonQuery(lease);
            }
            writer.Commit(lease);
            return true;
        }
        catch (SqliteException)
        {
            RollBackQuietly(writer, lease);
            return false;
        }
    }

    /// <summary>Maps a SQLite error met while opening or creating to a state. A corruption error is confirmed by
    /// <c>quick_check</c> before the Library is called Damaged (§6.7); quick_check is never run at every open.</summary>
    private LibraryStatus FromSqliteFailure(SqliteException ex) => LibraryDatabase.Classify(ex) switch
    {
        DatabaseFailure.Corrupt => ConfirmDamaged(ex),
        DatabaseFailure.Busy => Fail(LibraryReason.Busy, "The Library is busy: " + ex.Message),
        DatabaseFailure.DiskFull => Fail(LibraryReason.DiskFull, "The disk holding the Library is full."),
        DatabaseFailure.ReadOnlyRollback => Fail(LibraryReason.IoError, "The Library needs recovery and could not be opened read-write."),
        _ => Fail(LibraryReason.IoError, ex.Message),
    };

    private LibraryStatus ConfirmDamaged(SqliteException original)
    {
        try
        {
            using var reader = LibraryDatabase.OpenReader(Store.MainPath);
            var check = reader.QuickCheck();
            return check == "ok"
                ? Fail(LibraryReason.IoError, "SQLite reported corruption but quick_check passed: " + original.Message)
                : State(LibraryState.Damaged, LibraryReason.Corrupt, "The Library is damaged (quick_check: " + check + ").");
        }
        catch (SqliteException second) when (LibraryDatabase.Classify(second) == DatabaseFailure.Corrupt)
        {
            return State(LibraryState.Damaged, LibraryReason.Corrupt, "The Library is damaged: " + second.Message);
        }
        catch (SqliteException second)
        {
            return Fail(LibraryReason.IoError, second.Message);
        }
    }

    // ---- set aside (LIB-13) ----

    /// <summary>The only rename (LIB-13), on the user's explicit action: the writer lock is held (a process that does not hold it
    /// can never set aside: In use, nothing renamed); every Library connection of this process is closed through the gate; the
    /// member set is renamed, main file first, with <c>overwrite: false</c>; the lock stays held so a new Library can be created
    /// without a gap. Nothing is deleted.</summary>
    internal async Task<SetAsideResult> SetAsideAsync(MutationLease lease, CancellationToken cancellation = default)
    {
        Interlock.Require(lease, "set the Library aside", MutationKind.SetAside);
        try
        {
            var locked = Store.AcquireWriterLock(lease);
            if (locked.Outcome == LockOutcome.InUse) return new SetAsideResult(null, Publish(State(LibraryState.InUse, LibraryReason.LockedByAnotherProcess, "The Library is open in another StorageInventory window.")));
            if (locked.Outcome == LockOutcome.Unavailable) return new SetAsideResult(null, Publish(Fail(LibraryReason.IoError, locked.Detail ?? "The Library lock could not be taken.")));

            // Close every connection of this process: the gate takes the writer side, cancelling in-flight reads; each reader
            // disposes its connection when it stops. The writer exists only inside a lease, and this lease holds none.
            using var exclusive = await Gate.AcquireWriterAsync(cancellation).ConfigureAwait(false);
            var quarantine = Store.QuarantineSet(lease);

            var members = Store.InspectMembersUnderLock();
            LibraryStatus status;
            if (members.Main.Exists) status = Status.State == LibraryState.Unavailable ? Fail(LibraryReason.IoError, quarantine.Failure ?? "The set-aside did not move the database.") : Status;
            else if (members.Journal.NonEmpty) status = State(LibraryState.LeftoverFiles, LibraryReason.JournalWithoutMain, "Files from an interrupted Library remain here.");
            else if (members.Wal.Exists || members.Shm.Exists) status = State(LibraryState.NotALibrary, LibraryReason.WalOrShmPresent, "Side files remain.");
            else status = State(LibraryState.Missing, LibraryReason.MainFileMissingLockExisted, "The Library was set aside. A new, empty Library can be created.");
            _everHadLibrary = true;
            return new SetAsideResult(quarantine, Publish(status));
        }
        catch (Exception ex) when (LibraryInterlock.IsClassC(ex))
        {
            lease.ClassCFailure(ex);
            throw;
        }
    }

    // ---- attempts (T0, T-OUTCOME) ----

    /// <summary>T0 (P1, Prepare lease): inserts the InProgress attempt of a capture in its own <c>BEGIN IMMEDIATE</c> transaction
    /// and closes the writer. Returns its id.</summary>
    internal async Task<AttemptRef> RecordAttemptStartAsync(MutationLease lease, AttemptStart start, CancellationToken cancellation = default)
    {
        Interlock.Require(lease, "T0", MutationKind.Prepare);
        RequireWritable();
        using var exclusive = await Gate.AcquireWriterAsync(cancellation).ConfigureAwait(false);
        using var writer = LibraryDatabase.OpenWriter(lease, Store.MainPath, "T0", [MutationKind.Prepare], importCache: false, _faults);
        try
        {
            writer.Begin(lease);
            long id;
            using (var insert = writer.Prepare(lease, ImportSql.InsertAttempt, "$session_token", "$capture_token", "$source_id", "$root_path", "$report_folder", "$run_id", "$started_utc"))
            {
                id = Convert.ToInt64(insert.Set(0, SessionToken).Set(1, start.CaptureToken).Set(2, start.SourceId).Set(3, start.RootPathAsEntered)
                    .Set(4, start.ReportFolder).Set(5, start.RunId).Set(6, start.StartedUtcTicks).ExecuteScalar(lease));
            }
            writer.Commit(lease);
            return new AttemptRef(id, start.CaptureToken);
        }
        catch
        {
            RollBackQuietly(writer, lease);
            throw;
        }
    }

    /// <summary>T-OUTCOME (Save lease), best effort: records how an attempt ended, only while it is this session's and InProgress.
    /// Returns whether exactly one row changed.</summary>
    internal async Task<bool> RecordAttemptOutcomeAsync(MutationLease lease, AttemptRef attempt, AttemptOutcome outcome, CaptureFailureKind? failure, string? message, CancellationToken cancellation = default)
    {
        Interlock.Require(lease, "T-OUTCOME", MutationKind.Save);
        if (outcome == AttemptOutcome.InProgress) throw new ArgumentException("An outcome is never InProgress.", nameof(outcome));
        RequireWritable();
        using var exclusive = await Gate.AcquireWriterAsync(cancellation).ConfigureAwait(false);
        using var writer = LibraryDatabase.OpenWriter(lease, Store.MainPath, "T-OUTCOME", [MutationKind.Save], importCache: false, _faults);
        try
        {
            writer.Begin(lease);
            int changed;
            using (var update = writer.Prepare(lease, ImportSql.RecordAttemptOutcome, "$outcome", "$failure_kind", "$message", "$ended_utc", "$attempt_id", "$session_token"))
            {
                changed = update.Set(0, StableCodes.ToCode(outcome)).Set(1, failure is { } f ? StableCodes.ToCode(f) : null).Set(2, message).Set(3, DateTime.UtcNow.Ticks)
                    .Set(4, attempt.AttemptId).Set(5, SessionToken).ExecuteNonQuery(lease);
            }
            writer.Commit(lease);
            return changed == 1;
        }
        catch
        {
            RollBackQuietly(writer, lease);
            throw;
        }
    }

    // ---- T-IMPORT ----

    /// <summary>The one-transaction snapshot import over an abstract row stream (Save lease). Takes the in-process gate as a
    /// writer before <c>BEGIN IMMEDIATE</c> (IMP-02), imports, verifies and publishes in one transaction, and closes the writer. On
    /// any failure the transaction is rolled back and nothing of the snapshot remains; the failure is an
    /// <see cref="ImportException"/> carrying its <see cref="CaptureFailureKind"/>, or an <see cref="OperationCanceledException"/>.</summary>
    internal async Task<ImportResult> ImportSnapshotAsync(MutationLease lease, AttemptRef attempt, ImportSourceSpec source, ImportSnapshotHeader header,
        ISnapshotRowSource rows, ImportOptions? options = null, CancellationToken cancellation = default)
    {
        Interlock.Require(lease, "T-IMPORT", MutationKind.Save);
        RequireWritable();
        using var exclusive = await Gate.AcquireWriterAsync(cancellation).ConfigureAwait(false);
        WriterConnection writer;
        try
        {
            writer = LibraryDatabase.OpenWriter(lease, Store.MainPath, "T-IMPORT", [MutationKind.Save], importCache: true, _faults);
        }
        catch (SqliteException ex)
        {
            throw new ImportException(SnapshotImporter.ClassifyFailure(ex), ex.Message, ex);
        }
        using (writer)
        {
            return SnapshotImporter.Run(writer, lease, SessionToken, attempt, source, header, rows, options, Store.JournalPath, cancellation);
        }
    }

    // ---- T-DELETE ----

    /// <summary>T-DELETE (Delete lease): one <c>BEGIN IMMEDIATE</c> transaction that marks exactly one Published snapshot
    /// <c>Deleting</c>, deletes that snapshot's observation, error and extension rows and then its row, and commits. Other snapshots
    /// are never touched and the attempt row stays. Cancelling, or any failure, rolls everything back.</summary>
    internal async Task DeleteSnapshotAsync(MutationLease lease, long snapshotId, CancellationToken cancellation = default)
    {
        Interlock.Require(lease, "T-DELETE", MutationKind.Delete);
        RequireWritable();
        using var exclusive = await Gate.AcquireWriterAsync(cancellation).ConfigureAwait(false);
        using var writer = LibraryDatabase.OpenWriter(lease, Store.MainPath, "T-DELETE", [MutationKind.Delete], importCache: false, _faults);
        try
        {
            writer.Begin(lease);
            Delete(writer, lease, DeleteSql.MarkDeleting, snapshotId, expectOne: true, "mark the snapshot as deleting (it is not a published snapshot)", cancellation);
            Delete(writer, lease, DeleteSql.DeleteFileObs, snapshotId, expectOne: false, "", cancellation);
            Delete(writer, lease, DeleteSql.DeleteFolderObs, snapshotId, expectOne: false, "", cancellation);
            Delete(writer, lease, DeleteSql.DeleteScanErrors, snapshotId, expectOne: false, "", cancellation);
            Delete(writer, lease, DeleteSql.DeleteExtensionTotals, snapshotId, expectOne: false, "", cancellation);
            Delete(writer, lease, DeleteSql.DeleteSnapshotRow, snapshotId, expectOne: true, "delete the snapshot row", cancellation);
            writer.Commit(lease);
        }
        catch (SqliteException ex)
        {
            RollBackQuietly(writer, lease);
            throw new DeleteException(ex.Message, ex);
        }
        catch
        {
            RollBackQuietly(writer, lease);
            throw;
        }
    }

    private static void Delete(WriterConnection writer, MutationLease lease, string sql, long snapshotId, bool expectOne, string what, CancellationToken cancellation)
    {
        using var statement = writer.Prepare(lease, sql, "$snapshot_id");
        var changed = statement.Set(0, snapshotId).ExecuteNonQuery(lease, cancellation);
        if (expectOne && changed != 1) throw new DeleteException($"Could not {what}: {changed} rows changed.");
    }

    private void RequireWritable()
    {
        var status = Status;
        if (!status.CanSave) throw new LibraryUnavailableException($"The Library cannot be changed: {status.State} ({status.Reason}).");
    }

    // ---- reading ----

    /// <summary>Runs a read on a separate <c>Mode=ReadOnly</c> connection (CONC-05): takes the gate as a reader (waiting while a
    /// writer holds it and being cancelled when one asks), opens and closes the connection around the read, never writes. Refused
    /// when the Library is not readable and in Faulted (OBS-13). A corruption error is confirmed with <c>quick_check</c> and turns
    /// the state Damaged (§6.7).</summary>
    internal async Task<T> ReadAsync<T>(Func<ReaderConnection, T> read, CancellationToken cancellation = default)
    {
        if (Interlock.IsFaulted) throw new LibraryUnavailableException(LibraryInterlock.FaultedReason);
        var status = Status;
        if (!status.CanRead) throw new LibraryUnavailableException($"The Library cannot be read: {status.State} ({status.Reason}).");
        using var ticket = await Gate.AcquireReaderAsync(cancellation).ConfigureAwait(false);
        if (Interlock.IsFaulted) throw new LibraryUnavailableException(LibraryInterlock.FaultedReason);
        ReaderConnection? reader = null;
        try
        {
            reader = LibraryDatabase.OpenReader(Store.MainPath);
            reader.Token = ticket.Token;
            return read(reader);
        }
        catch (SqliteException ex) when (LibraryDatabase.Classify(ex) == DatabaseFailure.Corrupt)
        {
            var confirmed = reader?.QuickCheck();
            if (confirmed is not null && confirmed != "ok") Publish(State(LibraryState.Damaged, LibraryReason.Corrupt, "The Library is damaged (quick_check: " + confirmed + ")."));
            throw;
        }
        finally
        {
            reader?.Dispose();
        }
    }

    /// <summary>The synchronous form of <see cref="ReadAsync{T}"/>.</summary>
    internal T Read<T>(Func<ReaderConnection, T> read) => ReadAsync(read).GetAwaiter().GetResult();

    // ---- states ----

    private LibraryStatus State(LibraryState state, string reason, string message) => LibraryStatus.Of(state, reason, message, Store.Directory);

    private LibraryStatus Fail(string reason, string message) => LibraryStatus.Of(LibraryState.Unavailable, reason, message, Store.Directory);

    private LibraryStatus Publish(LibraryStatus status)
    {
        lock (_statusLock) _derived = status;
        StatusChanged?.Invoke(Status);
        return status;
    }

    /// <summary>Ends this process's hold on the Library the way process exit does, for tests that cannot exit: closes the gate and
    /// the writer lock handle. Production never calls it (the lock is held until the process exits, CONC-01); an audit checks that
    /// no shipped code does.</summary>
    internal void TestOnlyShutdown()
    {
        Gate.Close();
        Store.TestOnlyReleaseWriterLock();
    }
}

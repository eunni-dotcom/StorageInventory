using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace StorageInventory.Library;

/// <summary>How a SQLite failure is classified for the Library states and the capture outcomes (§6.8, §10.4).</summary>
internal enum DatabaseFailure
{
    Other = 0,
    Busy = 1,
    DiskFull = 2,
    IoError = 3,
    Corrupt = 4,
    ReadOnlyRollback = 5,
    CannotOpen = 6,
    ReadOnly = 7,
}

/// <summary>The engine reported something other than the pinned build (BLD-15): the Library is Unavailable and nothing is written.</summary>
internal sealed class UnexpectedEngineException(string message) : Exception(message);

/// <summary>A setting that was configured did not read back as configured (A-22): the connection is refused.</summary>
internal sealed class EngineConfigurationException(string message) : Exception(message);

/// <summary>
/// Test-only switches. Production code never constructs one. They exist so the fault tests can exercise real engine behaviour
/// (a database that fills up, a lease that goes stale between BEGIN and COMMIT) without any other code path to the Library.
/// </summary>
internal sealed class LibraryFaultInjection
{
    /// <summary>Makes the writer refuse to grow the database past 2,048 pages (8 MiB at the 4 KiB page size) with the engine's own
    /// limit, so the engine itself fails with <c>SQLITE_FULL</c>. The only fixed-size value; there is no way to pass a number to SQL.</summary>
    internal bool LimitDatabaseToTwoThousandPages { get; init; }

    /// <summary>The same with a 20,000-page limit (80 MiB), for the engine benchmark's rollback measurement.</summary>
    internal bool LimitDatabaseToTwentyThousandPages { get; init; }

    /// <summary>Pretends the pinned engine is something else, so that the "unexpected SQLite engine" state can be forced
    /// deterministically (the real engine is the one shipped and cannot be swapped in a test).</summary>
    internal (string Version, string SourceId)? ExpectedEngine { get; init; }

    /// <summary>Called right after <c>BEGIN IMMEDIATE</c> succeeded.</summary>
    internal Action? AfterBegin { get; init; }

    /// <summary>Called immediately before the lease check that guards <c>COMMIT</c>.</summary>
    internal Action? BeforeCommit { get; init; }

    /// <summary>Called after each statement of T-DELETE with the statement's name (<c>mark</c>, <c>file_obs</c>, <c>folder_obs</c>,
     /// <c>scan_error</c>, <c>extension_total</c>, <c>snapshot_row</c>): a place to fail or cancel part-way, so that the rollback of a
     /// partial deletion is tested where it matters (C4-M12).</summary>
    internal Action<string>? AfterDeleteStep { get; init; }

    /// <summary>Called when the writer connection is released, inside the step whose failure ends the lease in Faulted (OBS-13): a
    /// hook that throws shows that a writer that cannot be closed faults the interlock (TEST-W2).</summary>
    internal Action? OnWriterRelease { get; init; }
}

/// <summary>
/// The SQLite adapter (D-33, A-06): the only place a <c>SqliteConnection</c> is constructed. It sets the provider explicitly,
/// exactly once, before the first connection (A-10); opens every connection with an explicit <c>Mode</c> (never a create
/// mode: the main file is created by <see cref="LibraryStore"/> with <c>FileMode.CreateNew</c> first, SEC-06) and
/// <c>Pooling=False</c>; asserts the exact engine version and source id; sets every §5.8 pragma and reads it back; and hands out
/// writers that exist only inside a mutation lease (OBS-12) and read-only readers that can never perform recovery (CONC-05).
/// </summary>
internal static class LibraryDatabase
{
    private static readonly object ProviderLock = new();
    private static bool _providerSet;

    /// <summary>Sets <c>SQLite3Provider_e_sqlite3</c> exactly once for the process, before any <c>SqliteConnection</c> exists.
    /// No <c>Batteries</c> initialiser, no native resolver (A-10, D-33).</summary>
    internal static void EnsureProvider()
    {
        lock (ProviderLock)
        {
            if (_providerSet) return;
            raw.SetProvider(new SQLite3Provider_e_sqlite3());
            _providerSet = true;
        }
    }

    // ---- writers: only inside a mutation lease ----

    /// <summary>Opens the one writer connection (<c>Mode=ReadWrite</c>), asserts the engine, sets and verifies the §5.8 pragmas.
    /// The lease must be current and one of <paramref name="allowed"/> (OBS-15); the connection is registered as a resource of the
    /// lease, which can end cleanly only after it is closed (OBS-13).</summary>
    internal static WriterConnection OpenWriter(MutationLease lease, LibraryInterlock interlock, string mainFile, string operation, MutationKind[] allowed, bool importCache, LibraryFaultInjection? faults)
    {
        // OBS-15, before any I/O: the interlock rejects a default lease, a lease of another session, a stale, disposed or handed-off
        // lease and one of the wrong kind, and enters Faulted. A default lease names no interlock of its own, which is why the
        // interlock is a parameter: it is the one that must fault.
        interlock.Require(lease, operation + ": open the writer", allowed);
        EnsureProvider();

        var builder = new SqliteConnectionStringBuilder { DataSource = mainFile, Mode = SqliteOpenMode.ReadWrite, Pooling = false };
        var connection = new SqliteConnection(builder.ConnectionString);
        lease.ResourceOpened();
        var writer = new WriterConnection(connection, interlock, lease, operation, allowed, faults);
        try
        {
            connection.Open();
            writer.AssertEngine(lease);
            writer.Configure(lease, importCache);
            return writer;
        }
        catch
        {
            writer.Dispose();
            throw;
        }
    }

    // ---- readers: separate Mode=ReadOnly connections ----

    /// <summary>Opens a read-only connection. It never recovers a hot journal (it fails with <c>SQLITE_READONLY_ROLLBACK</c>) and
    /// writes nothing. Needs no lease (OBS-03, A-25 part d).</summary>
    internal static ReaderConnection OpenReader(string mainFile, LibraryFaultInjection? faults = null)
    {
        EnsureProvider();
        var builder = new SqliteConnectionStringBuilder { DataSource = mainFile, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        var connection = new SqliteConnection(builder.ConnectionString);
        try
        {
            connection.Open();
            var reader = new ReaderConnection(connection);
            reader.AssertEngine(faults);
            reader.Configure();
            return reader;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    // ---- shared ----

    /// <summary>The same assertion without a connection (<c>sqlite3_libversion</c> and <c>sqlite3_sourceid</c>), so that it can run
    /// before the first file of a new Library is created: an unexpected engine must leave nothing written (BLD-15, §5.6).</summary>
    internal static void AssertEngineLoaded(LibraryFaultInjection? faults = null)
    {
        EnsureProvider();
        var (expectedVersion, expectedSourceId) = faults?.ExpectedEngine ?? (LibraryNames.PinnedSqliteVersion, LibraryNames.PinnedSqliteSourceId);
        var version = raw.sqlite3_libversion().utf8_to_string();
        var sourceId = raw.sqlite3_sourceid().utf8_to_string();
        if (version != expectedVersion || sourceId != expectedSourceId)
        {
            throw new UnexpectedEngineException($"Unexpected SQLite engine: {version} ({sourceId}); expected {expectedVersion} ({expectedSourceId}).");
        }
    }

    /// <summary>Maps a SQLite error to the failure classes the Library states and outcomes use.</summary>
    internal static DatabaseFailure Classify(SqliteException ex) => ex.SqliteExtendedErrorCode == 776 ? DatabaseFailure.ReadOnlyRollback : ex.SqliteErrorCode switch
    {
        5 or 6 => DatabaseFailure.Busy,
        13 => DatabaseFailure.DiskFull,
        10 => DatabaseFailure.IoError,
        11 or 26 => DatabaseFailure.Corrupt,
        14 => DatabaseFailure.CannotOpen,
        8 => DatabaseFailure.ReadOnly,
        _ => DatabaseFailure.Other,
    };
}

/// <summary>
/// The one writer connection. It exists only inside a mutation lease and is closed before the lease ends (OBS-12, CONC-03).
/// Every primitive takes the lease and checks it is current and of an allowed kind BEFORE doing any I/O (OBS-15): when the
/// connection is opened, at <c>BEGIN IMMEDIATE</c>, for every statement execution, and immediately before <c>COMMIT</c>.
/// A failed check enters Faulted and throws; it is never ignored.
/// </summary>
internal sealed class WriterConnection : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly LibraryInterlock _interlock;
    private readonly MutationLease _openedBy;
    private readonly string _operation;
    private readonly MutationKind[] _allowed;
    private readonly LibraryFaultInjection? _faults;
    private bool _inTransaction;
    private bool _disposed;
    private CancellationToken _scopeToken;

    internal WriterConnection(SqliteConnection connection, LibraryInterlock interlock, MutationLease openedBy, string operation, MutationKind[] allowed, LibraryFaultInjection? faults)
    {
        _connection = connection;
        _interlock = interlock;
        _openedBy = openedBy;
        _operation = operation;
        _allowed = allowed;
        _faults = faults;
        Interlocked.Increment(ref _openWriters);
        Interlocked.Increment(ref _writersOpenedTotal);
    }

    internal bool InTransaction => _inTransaction;

    private static int _openWriters;
    private static long _writersOpenedTotal;

    /// <summary>How many writer connections this process has EVER opened. A process that was refused (In use) must show 0: it never
    /// opened a connection to a Library it does not own (CONC-02, TEST-K1).</summary>
    internal static long WritersOpenedTotal => Interlocked.Read(ref _writersOpenedTotal);

    /// <summary>How many writer connections are open in this process right now. The writer exists only inside a mutation lease
    /// (OBS-12), so this is 0 during every observation window: TEST-W1 asserts it.</summary>
    internal static int OpenWriterCount => Volatile.Read(ref _openWriters);

    /// <summary>Sets every §5.8 pragma from its constant and asserts it by reading it back (A-22). The first statement that
    /// touches the database file is <c>journal_mode</c>: if a hot journal exists, SQLite rolls it back there, in its default DELETE
    /// mode, and deletes the journal ([E-8] 5). That can only happen inside a mutation lease, because this connection exists only
    /// inside one.</summary>
    internal void Configure(MutationLease lease, bool importCache)
    {
        Guard(lease, "configure the connection");
        SetAndReadBack(_connection, OpenSql.SetJournalMode, OpenSql.GetJournalMode, "truncate", "journal_mode");
        SetAndReadBack(_connection, OpenSql.SetSynchronous, OpenSql.GetSynchronous, "2", "synchronous");
        SetAndReadBack(_connection, OpenSql.SetLockingMode, OpenSql.GetLockingMode, "normal", "locking_mode");
        SetAndReadBack(_connection, OpenSql.SetBusyTimeout, OpenSql.GetBusyTimeout, "5000", "busy_timeout");
        SetAndReadBack(_connection, OpenSql.SetTempStore, OpenSql.GetTempStore, "2", "temp_store");
        SetAndReadBack(_connection, OpenSql.SetForeignKeys, OpenSql.GetForeignKeys, "1", "foreign_keys");
        SetAndReadBack(_connection, OpenSql.SetTrustedSchema, OpenSql.GetTrustedSchema, "0", "trusted_schema");
        if (importCache) SetAndReadBack(_connection, OpenSql.SetImportCacheSize, OpenSql.GetCacheSize, "-65536", "cache_size");
        if (_faults?.LimitDatabaseToTwoThousandPages == true)
        {
            using var limit = _connection.CreateCommand();
            limit.CommandText = FaultSql.LimitToTwoThousandPages;
            limit.ExecuteNonQuery();
        }
        else if (_faults?.LimitDatabaseToTwentyThousandPages == true)
        {
            using var limit = _connection.CreateCommand();
            limit.CommandText = FaultSql.LimitToTwentyThousandPages;
            limit.ExecuteNonQuery();
        }
    }

    /// <summary>The first statement of every connection (reads no database page): <c>sqlite_version()</c> and
    /// <c>sqlite_source_id()</c> must equal the pinned values, exactly (BLD-15). Not a lower bound. A command on the writer, so it
    /// takes the lease.</summary>
    internal void AssertEngine(MutationLease lease)
    {
        Guard(lease, "assert the engine");
        var (expectedVersion, expectedSourceId) = _faults?.ExpectedEngine ?? (LibraryNames.PinnedSqliteVersion, LibraryNames.PinnedSqliteSourceId);
        using var command = _connection.CreateCommand();
        command.CommandText = OpenSql.SelectEngine;
        using var result = command.ExecuteReader();
        if (!result.Read()) throw new UnexpectedEngineException("The SQLite engine did not report its version.");
        var version = result.GetString(0);
        var sourceId = result.GetString(1);
        if (version != expectedVersion || sourceId != expectedSourceId)
        {
            throw new UnexpectedEngineException($"Unexpected SQLite engine: {version} ({sourceId}); expected {expectedVersion} ({expectedSourceId}).");
        }
    }

    /// <summary>The OBS-15 check, for the importer's row counter and any other long loop. Faults and throws when not current, or
    /// when it is not the lease this writer was opened under.</summary>
    internal void CheckCurrent(MutationLease lease) => Guard(lease, "check the lease");

    /// <summary>The token of the cancellation scope that is active on this connection, if any (a statement interrupted by it is a
    /// cancellation, not an error).</summary>
    internal CancellationToken ScopeToken => _scopeToken;

    /// <summary>Starts a cancellation scope: while it lives, the engine's progress callback runs every
    /// <see cref="ProgressGuard.Opcodes"/> virtual-machine steps of every statement on THIS connection and interrupts the statement
    /// when the token is cancelled, so no stretch of work on this connection (a verification query over millions of rows) goes
    /// longer than a few milliseconds without observing it (CAN-01d). It belongs to this writer only: other connections are not
    /// affected. Disposing it uninstalls the callback.</summary>
    internal CancellationScope BeginCancellationScope(MutationLease lease, CancellationToken token, TokenChecker checker)
    {
        Guard(lease, "start a cancellation scope");
        var db = _connection.Handle ?? throw new InvalidOperationException("The writer connection is not open.");
        _scopeToken = token;
        return new CancellationScope(this, new ProgressGuard(db, checker));
    }

    internal void EndCancellationScope() => _scopeToken = default;

    /// <summary><c>PRAGMA page_count</c> inside the transaction (IMP-11: the transaction's image of the database, in pages).</summary>
    internal long PageCount(MutationLease lease) => Convert.ToInt64(Scalar(lease, OpenSql.GetPageCount), System.Globalization.CultureInfo.InvariantCulture);

    /// <summary><c>PRAGMA page_size</c> (IMP-11).</summary>
    internal long PageSize(MutationLease lease) => Convert.ToInt64(Scalar(lease, OpenSql.GetPageSize), System.Globalization.CultureInfo.InvariantCulture);

    internal void Begin(MutationLease lease)
    {
        Guard(lease, "begin the transaction");
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = TransactionSql.BeginImmediate;
            command.ExecuteNonQuery();
        }
        _inTransaction = true;
        _faults?.AfterBegin?.Invoke();
    }

    /// <summary>Checks the lease again IMMEDIATELY before <c>COMMIT</c>: a lease made non-current between BEGIN and COMMIT
    /// prevents the commit (the transaction is rolled back when the connection closes).</summary>
    internal void Commit(MutationLease lease)
    {
        _faults?.BeforeCommit?.Invoke();
        Guard(lease, "commit the transaction");
        using var command = _connection.CreateCommand();
        command.CommandText = TransactionSql.Commit;
        command.ExecuteNonQuery();
        _inTransaction = false;
    }

    /// <summary>Rolls back. A rollback only removes uncommitted work, so it is allowed after the lease has gone stale or the
    /// interlock has Faulted (best-effort release, OBS-13): it takes the lease for A-25's sake but does not require it current.</summary>
    internal void Rollback(MutationLease lease)
    {
        _interlock.RequireSameLease(lease, _openedBy, _operation, "roll back");
        if (!_inTransaction) return;
        using var command = _connection.CreateCommand();
        command.CommandText = TransactionSql.Rollback;
        command.ExecuteNonQuery();
        _inTransaction = false;
    }

    /// <summary>Prepares a constant statement. <paramref name="sql"/> must be one of the <c>*Sql</c> constants (A-05). Hot-path statements
    /// run through SQLitePCLRaw's managed API on the connection's own handle rather than through <c>SqliteCommand</c>: the
    /// ADO.NET layer costs several microseconds per execution, which TEST-P1 measured as the bottleneck of a multi-million-row
    /// import (PERF-01). The SQL, the bound parameters, the lease check and the error classification are the same.</summary>
    internal WriterStatement Prepare(MutationLease lease, string sql, params string[] parameterNames)
    {
        Guard(lease, "prepare a statement");
        var db = _connection.Handle ?? throw new InvalidOperationException("The writer connection is not open.");
        var rc = raw.sqlite3_prepare_v2(db, sql, out var statement);
        if (rc != raw.SQLITE_OK) throw WriterStatement.Error(db, rc);
        // no names: positional parameters (?), bound by position 1..n (the batched statements)
        var indexes = new int[parameterNames.Length == 0 ? raw.sqlite3_bind_parameter_count(statement) : parameterNames.Length];
        for (var i = 0; i < parameterNames.Length; i++)
        {
            indexes[i] = raw.sqlite3_bind_parameter_index(statement, parameterNames[i]);
            if (indexes[i] == 0)
            {
                statement.Dispose();
                throw new InvalidOperationException($"The statement has no parameter {parameterNames[i]}.");
            }
        }
        if (parameterNames.Length == 0) for (var i = 0; i < indexes.Length; i++) indexes[i] = i + 1;
        return new WriterStatement(db, statement, indexes, this);
    }

    /// <summary>Runs a constant query on the writer and returns its first value (used inside a transaction for the checks, which
    /// must see the transaction's own uncommitted rows). The lease is checked first.</summary>
    internal object? Scalar(MutationLease lease, string sql, params (string Name, object? Value)[] parameters)
    {
        Guard(lease, "query");
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        try
        {
            var result = command.ExecuteScalar();
            return result is DBNull ? null : result;
        }
        catch (SqliteException) when (_scopeToken.IsCancellationRequested) { throw new OperationCanceledException(_scopeToken); }
    }

    /// <summary>Runs a constant query on the writer and passes each row to <paramref name="each"/>, through the engine's own API (the
    /// verification streams millions of rows). The lease is checked first.</summary>
    internal void Rows(MutationLease lease, string sql, Action<IRowReader> each, params (string Name, object? Value)[] parameters)
    {
        using var statement = Prepare(lease, sql, [.. parameters.Select(p => p.Name)]);
        for (var i = 0; i < parameters.Length; i++) statement.Set(i, parameters[i].Value);
        statement.ExecuteRows(lease, each);
    }

    /// <summary>OBS-15 before every operation on this connection: the lease must be current, of an allowed kind and the very lease
    /// the connection was opened under. A <c>default</c> lease, a lease of another session (even with an equal id), a stale,
    /// disposed, handed-off or invalidated lease, and a current lease that did not open this connection are all refused here,
    /// before any I/O, and enter Faulted.</summary>
    internal void Guard(MutationLease lease, string what) => _interlock.RequireOpenedBy(lease, _openedBy, _operation, what, _allowed);

    /// <summary>Sets a pragma from its constant and asserts it by reading it back (A-22).</summary>
    private static void SetAndReadBack(SqliteConnection connection, string setSql, string getSql, string expected, string name)
    {
        using (var set = connection.CreateCommand())
        {
            set.CommandText = setSql;
            set.ExecuteNonQuery();
        }
        using var get = connection.CreateCommand();
        get.CommandText = getSql;
        var actual = Convert.ToString(get.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? "";
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new EngineConfigurationException($"{name} reads back as '{actual}', expected '{expected}'.");
        }
    }

    /// <summary>Closes the connection (a transaction still open is rolled back by SQLite) and tells the lease the resource is
    /// released. A release step that throws enters Faulted (OBS-13).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Interlocked.Decrement(ref _openWriters);
        try
        {
            _faults?.OnWriterRelease?.Invoke();
            _connection.Dispose();
            _openedBy.ResourceReleased();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _openedBy.ReleaseFailed("writer connection: " + ex.GetType().Name);
        }
    }
}

/// <summary>A prepared constant statement on the writer, executed through SQLitePCLRaw's managed API on the connection's handle.
/// Executing it needs a current lease of an allowed kind, checked before the statement runs (OBS-15). Values are bound by type:
/// nothing is ever spliced into the SQL text (SEC-04), and an empty byte array is an empty BLOB, never NULL.</summary>
internal sealed class WriterStatement : IDisposable
{
    private readonly sqlite3 _db;
    private readonly sqlite3_stmt _statement;
    private readonly int[] _indexes;
    private readonly WriterConnection _owner;

    internal WriterStatement(sqlite3 db, sqlite3_stmt statement, int[] indexes, WriterConnection owner)
    {
        _db = db;
        _statement = statement;
        _indexes = indexes;
        _owner = owner;
    }

    /// <summary>The engine's own error as a <see cref="SqliteException"/>, so that classification (<c>LibraryDatabase.Classify</c>)
    /// is the same whichever API ran the statement.</summary>
    internal static SqliteException Error(sqlite3 db, int resultCode) =>
        new(raw.sqlite3_errmsg(db).utf8_to_string(), resultCode, raw.sqlite3_extended_errcode(db));

    internal WriterStatement Set(int index, long value) => Check(raw.sqlite3_bind_int64(_statement, _indexes[index], value));

    internal WriterStatement Set(int index, long? value) => value is { } v ? Set(index, v) : Check(raw.sqlite3_bind_null(_statement, _indexes[index]));

    internal WriterStatement Set(int index, byte[]? value) =>
        value is null ? Check(raw.sqlite3_bind_null(_statement, _indexes[index]))
        : Check(value.Length == 0 ? raw.sqlite3_bind_zeroblob(_statement, _indexes[index], 0) : raw.sqlite3_bind_blob(_statement, _indexes[index], value));

    internal WriterStatement Set(int index, string? value) =>
        value is null ? Check(raw.sqlite3_bind_null(_statement, _indexes[index])) : Check(raw.sqlite3_bind_text(_statement, _indexes[index], value));

    private WriterStatement Check(int rc)
    {
        if (rc != raw.SQLITE_OK) throw Error(_db, rc);
        return this;
    }

    /// <summary>Binds a value by position.</summary>
    internal WriterStatement Set(int index, object? value)
    {
        var position = _indexes[index];
        int rc;
        switch (value)
        {
            case null:
            case DBNull:
                rc = raw.sqlite3_bind_null(_statement, position);
                break;
            case long l:
                rc = raw.sqlite3_bind_int64(_statement, position, l);
                break;
            case int i:
                rc = raw.sqlite3_bind_int64(_statement, position, i);
                break;
            case byte[] bytes:
                rc = bytes.Length == 0 ? raw.sqlite3_bind_zeroblob(_statement, position, 0) : raw.sqlite3_bind_blob(_statement, position, bytes);
                break;
            case string text:
                rc = raw.sqlite3_bind_text(_statement, position, text);
                break;
            case bool flag:
                rc = raw.sqlite3_bind_int64(_statement, position, flag ? 1 : 0);
                break;
            default:
                throw new ArgumentException($"A value of type {value.GetType().Name} cannot be bound.", nameof(value));
        }
        if (rc != raw.SQLITE_OK) throw Error(_db, rc);
        return this;
    }

    internal int ExecuteNonQuery(MutationLease lease) => ExecuteNonQuery(lease, CancellationToken.None);

    /// <summary>Executes the statement to completion (a <c>RETURNING</c> statement's rows are consumed) and returns the rows it
    /// changed; cancelling the token interrupts it (<c>sqlite3_interrupt</c>), which rolls the statement back and surfaces as
    /// <see cref="OperationCanceledException"/>.</summary>
    internal int ExecuteNonQuery(MutationLease lease, CancellationToken cancellation)
    {
        _owner.Guard(lease, "execute");
        using var registration = cancellation.CanBeCanceled ? cancellation.Register(static state => raw.sqlite3_interrupt((sqlite3)state!), _db) : default;
        var rc = raw.sqlite3_step(_statement);
        while (rc == raw.SQLITE_ROW) rc = raw.sqlite3_step(_statement);
        return Finish(rc, cancellation) ? raw.sqlite3_changes(_db) : 0;
    }

    /// <summary>Executes a constant query and passes each row to <paramref name="each"/>. The lease is checked once, before the first step.</summary>
    internal void ExecuteRows(MutationLease lease, Action<IRowReader> each)
    {
        _owner.Guard(lease, "execute");
        var reader = new RawRowReader(_statement);
        var rc = raw.sqlite3_step(_statement);
        try
        {
            while (rc == raw.SQLITE_ROW)
            {
                each(reader);
                rc = raw.sqlite3_step(_statement);
            }
        }
        catch
        {
            raw.sqlite3_reset(_statement);   // a row callback that throws (a cancellation, a refusal) leaves no statement running
            throw;
        }
        Finish(rc, CancellationToken.None);
    }

    /// <summary>Executes an <c>INSERT ... ON CONFLICT DO NOTHING</c>: returns true and the new rowid when a row was inserted, false
    /// (and no row) on a conflict.</summary>
    internal bool ExecuteInsertIfAbsent(MutationLease lease, out long rowId)
    {
        _owner.Guard(lease, "execute");
        var rc = raw.sqlite3_step(_statement);
        while (rc == raw.SQLITE_ROW) rc = raw.sqlite3_step(_statement);
        Finish(rc, CancellationToken.None);
        var inserted = raw.sqlite3_changes(_db) == 1;
        rowId = inserted ? raw.sqlite3_last_insert_rowid(_db) : 0;
        return inserted;
    }

    /// <summary>Executes an <c>INSERT</c> and returns the rowid it created (<c>sqlite3_last_insert_rowid</c>).</summary>
    internal long ExecuteInsert(MutationLease lease)
    {
        _owner.Guard(lease, "execute");
        var rc = raw.sqlite3_step(_statement);
        while (rc == raw.SQLITE_ROW) rc = raw.sqlite3_step(_statement);
        Finish(rc, CancellationToken.None);
        return raw.sqlite3_last_insert_rowid(_db);
    }

    /// <summary>Executes the statement and returns the first column of its first row (null when there is none), then runs it to
    /// completion.</summary>
    internal object? ExecuteScalar(MutationLease lease)
    {
        _owner.Guard(lease, "execute");
        var rc = raw.sqlite3_step(_statement);
        object? result = null;
        if (rc == raw.SQLITE_ROW)
        {
            result = raw.sqlite3_column_type(_statement, 0) switch
            {
                raw.SQLITE_INTEGER => raw.sqlite3_column_int64(_statement, 0),
                raw.SQLITE_FLOAT => raw.sqlite3_column_double(_statement, 0),
                raw.SQLITE_TEXT => raw.sqlite3_column_text(_statement, 0).utf8_to_string(),
                raw.SQLITE_BLOB => raw.sqlite3_column_blob(_statement, 0).ToArray(),
                _ => null,
            };
            rc = raw.sqlite3_step(_statement);
            while (rc == raw.SQLITE_ROW) rc = raw.sqlite3_step(_statement);
        }
        Finish(rc, CancellationToken.None);
        return result;
    }

    /// <summary>Resets the statement for reuse and turns a failure into an exception.</summary>
    private bool Finish(int resultCode, CancellationToken cancellation)
    {
        if (resultCode == raw.SQLITE_DONE)
        {
            raw.sqlite3_reset(_statement);
            return true;
        }
        var error = Error(_db, resultCode);
        raw.sqlite3_reset(_statement);
        // a statement interrupted by a cancellation (the caller's token, or the scope's progress callback) is a cancellation
        if (cancellation.IsCancellationRequested) throw new OperationCanceledException(cancellation);
        if (_owner.ScopeToken.IsCancellationRequested) throw new OperationCanceledException(_owner.ScopeToken);
        throw error;
    }

    public void Dispose() => _statement.Dispose();
}

/// <summary>A separate <c>Mode=ReadOnly</c> connection (CONC-05). It opens, reads and closes without writing any file (OBS-03, Q-21).</summary>
internal sealed class ReaderConnection : IQueryRunner, IDisposable
{
    private readonly SqliteConnection _connection;
    private bool _disposed;

    private static long _readersOpenedTotal;

    /// <summary>How many reader connections this process has ever opened (TEST-K1: a process that is In use opens none).</summary>
    internal static long ReadersOpenedTotal => Interlocked.Read(ref _readersOpenedTotal);

    internal ReaderConnection(SqliteConnection connection)
    {
        _connection = connection;
        Interlocked.Increment(ref _readersOpenedTotal);
    }

    /// <summary>Cancelled by the in-process gate when a writer asks for it (CONC-06): the running command is interrupted and
    /// the read surfaces as <see cref="OperationCanceledException"/>, to be re-run by its page.</summary>
    internal CancellationToken Token { get; set; }

    /// <summary>The first statement of every connection: the engine must be exactly the pinned build (BLD-15). Reads no page.</summary>
    internal void AssertEngine(LibraryFaultInjection? faults)
    {
        var (expectedVersion, expectedSourceId) = faults?.ExpectedEngine ?? (LibraryNames.PinnedSqliteVersion, LibraryNames.PinnedSqliteSourceId);
        using var command = _connection.CreateCommand();
        command.CommandText = OpenSql.SelectEngine;
        using var result = command.ExecuteReader();
        if (!result.Read()) throw new UnexpectedEngineException("The SQLite engine did not report its version.");
        var version = result.GetString(0);
        var sourceId = result.GetString(1);
        if (version != expectedVersion || sourceId != expectedSourceId)
        {
            throw new UnexpectedEngineException($"Unexpected SQLite engine: {version} ({sourceId}); expected {expectedVersion} ({expectedSourceId}).");
        }
    }

    /// <summary>Puts the engine's progress callback on THIS reader connection for the lifetime of the returned scope, so that a long
    /// aggregate (the dictionary-footprint query before T-IMPORT) observes the save token as the verification does (CAN-01d). The
    /// callback is removed when the scope is disposed; other connections are unaffected.</summary>
    internal IDisposable BeginProgress(TokenChecker checker)
    {
        var db = _connection.Handle ?? throw new InvalidOperationException("The reader connection is not open.");
        return new ProgressGuard(db, checker);
    }

    /// <summary>The reader's settings: the build's default cache (2 MiB), no temp files, no schema-defined functions. These
    /// statements read the schema, so a reader that meets a hot journal fails here with <c>SQLITE_READONLY_ROLLBACK</c> and
    /// changes nothing.</summary>
    internal void Configure()
    {
        SetAndReadBack(OpenSql.SetTempStore, OpenSql.GetTempStore, "2", "temp_store");
        SetAndReadBack(OpenSql.SetTrustedSchema, OpenSql.GetTrustedSchema, "0", "trusted_schema");
    }

    private void SetAndReadBack(string setSql, string getSql, string expected, string name)
    {
        using (var set = _connection.CreateCommand())
        {
            set.CommandText = setSql;
            set.ExecuteNonQuery();
        }
        var actual = ReadBack(getSql);
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new EngineConfigurationException($"{name} reads back as '{actual}', expected '{expected}'.");
        }
    }

    /// <summary>Executes a constant query and returns its first column of the first row (null when none).</summary>
    public object? Scalar(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Build(sql, parameters);
        using var registration = Token.CanBeCanceled ? Token.Register(command.Cancel) : default;
        try
        {
            var value = command.ExecuteScalar();
            return value is DBNull ? null : value;
        }
        catch (SqliteException) when (Token.IsCancellationRequested) { throw new OperationCanceledException(Token); }
    }

    /// <summary>Executes a query and returns every row as an array of values (long, double, string, byte[] or null), so that
    /// callers need no SQLite types. The connection is read-only, so no statement passed here can change the Library.</summary>
    internal IReadOnlyList<object?[]> Query(string sql, params (string Name, object? Value)[] parameters)
    {
        var rows = new List<object?[]>();
        Rows(sql, reader =>
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++) row[i] = reader.GetValue(i);
            rows.Add(row);
        }, parameters);
        return rows;
    }

    internal long Long(string sql, params (string Name, object? Value)[] parameters) => Convert.ToInt64(Scalar(sql, parameters) ?? 0L, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Executes a constant query and passes each row to <paramref name="each"/>.</summary>
    public void Rows(string sql, Action<IRowReader> each, params (string Name, object? Value)[] parameters)
    {
        using var command = Build(sql, parameters);
        using var registration = Token.CanBeCanceled ? Token.Register(command.Cancel) : default;
        try
        {
            using var reader = command.ExecuteReader();
            var row = new AdoRowReader(reader);
            while (reader.Read()) each(row);
        }
        catch (SqliteException) when (Token.IsCancellationRequested) { throw new OperationCanceledException(Token); }
    }

    /// <summary>Runs <c>PRAGMA quick_check</c> and returns its first row ("ok" when intact). Only after an error that reports
    /// corruption (§6.7), never at every open. A failure of the check itself is returned as text, which is not "ok".</summary>
    internal string QuickCheck()
    {
        try { return ReadBack(OpenSql.QuickCheck); }
        catch (SqliteException ex) { return "quick_check failed: " + ex.Message; }
    }

    internal string PragmaText(string sql) => ReadBack(sql);

    private string ReadBack(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? "";
    }

    private SqliteCommand Build(string sql, (string Name, object? Value)[] parameters)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _connection.Dispose();
    }
}

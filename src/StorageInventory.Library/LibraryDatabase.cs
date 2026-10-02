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

    /// <summary>Pretends the pinned engine is something else, so that the "unexpected SQLite engine" state can be forced
    /// deterministically (the real engine is the one shipped and cannot be swapped in a test).</summary>
    internal (string Version, string SourceId)? ExpectedEngine { get; init; }

    /// <summary>Called right after <c>BEGIN IMMEDIATE</c> succeeded.</summary>
    internal Action? AfterBegin { get; init; }

    /// <summary>Called immediately before the lease check that guards <c>COMMIT</c>.</summary>
    internal Action? BeforeCommit { get; init; }
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
    internal static WriterConnection OpenWriter(MutationLease lease, string mainFile, string operation, MutationKind[] allowed, bool importCache, LibraryFaultInjection? faults)
    {
        lease.Owner?.Require(lease, operation + ": open the writer", allowed);
        if (lease.Owner is null) throw new LeaseViolationException("Refused before any I/O: no lease.");
        EnsureProvider();

        var builder = new SqliteConnectionStringBuilder { DataSource = mainFile, Mode = SqliteOpenMode.ReadWrite, Pooling = false };
        var connection = new SqliteConnection(builder.ConnectionString);
        lease.ResourceOpened();
        var writer = new WriterConnection(connection, lease, operation, allowed, faults);
        try
        {
            connection.Open();
            AssertEngine(connection, faults);
            writer.Configure(importCache);
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
            AssertEngine(connection, faults);
            var reader = new ReaderConnection(connection);
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

    /// <summary>The first statement of every connection (reads no database page): <c>sqlite_version()</c> and
    /// <c>sqlite_source_id()</c> must equal the pinned values, exactly (BLD-15). Not a lower bound.</summary>
    internal static void AssertEngine(SqliteConnection connection, LibraryFaultInjection? faults = null)
    {
        var (expectedVersion, expectedSourceId) = faults?.ExpectedEngine ?? (LibraryNames.PinnedSqliteVersion, LibraryNames.PinnedSqliteSourceId);
        using var command = connection.CreateCommand();
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

    /// <summary>Runs a constant read-back statement and returns its first column as text.</summary>
    internal static string ReadBack(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? "";
    }

    /// <summary>Runs a constant statement that returns a value (a pragma that is set) and returns its text.</summary>
    internal static string SetAndReadBack(SqliteConnection connection, string setSql, string getSql, string expected, string name)
    {
        using (var set = connection.CreateCommand())
        {
            set.CommandText = setSql;
            set.ExecuteNonQuery();
        }
        var actual = ReadBack(connection, getSql);
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new EngineConfigurationException($"{name} reads back as '{actual}', expected '{expected}'.");
        }
        return actual;
    }
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
    private readonly MutationLease _openedBy;
    private readonly string _operation;
    private readonly MutationKind[] _allowed;
    private readonly LibraryFaultInjection? _faults;
    private bool _inTransaction;
    private bool _disposed;

    internal WriterConnection(SqliteConnection connection, MutationLease openedBy, string operation, MutationKind[] allowed, LibraryFaultInjection? faults)
    {
        _connection = connection;
        _openedBy = openedBy;
        _operation = operation;
        _allowed = allowed;
        _faults = faults;
        Interlocked.Increment(ref _openWriters);
    }

    internal bool InTransaction => _inTransaction;

    private static int _openWriters;

    /// <summary>How many writer connections are open in this process right now. The writer exists only inside a mutation lease
    /// (OBS-12), so this is 0 during every observation window: TEST-W1 asserts it.</summary>
    internal static int OpenWriterCount => Volatile.Read(ref _openWriters);

    /// <summary>Sets every §5.8 pragma from its constant and asserts it by reading it back (A-22). The first statement that
    /// touches the database file is <c>journal_mode</c>: if a hot journal exists, SQLite rolls it back there, in its default DELETE
    /// mode, and deletes the journal ([E-8] 5). That can only happen inside a mutation lease, because this connection exists only
    /// inside one.</summary>
    internal void Configure(bool importCache)
    {
        LibraryDatabase.SetAndReadBack(_connection, OpenSql.SetJournalMode, OpenSql.GetJournalMode, "truncate", "journal_mode");
        LibraryDatabase.SetAndReadBack(_connection, OpenSql.SetSynchronous, OpenSql.GetSynchronous, "2", "synchronous");
        LibraryDatabase.SetAndReadBack(_connection, OpenSql.SetLockingMode, OpenSql.GetLockingMode, "normal", "locking_mode");
        LibraryDatabase.SetAndReadBack(_connection, OpenSql.SetBusyTimeout, OpenSql.GetBusyTimeout, "5000", "busy_timeout");
        LibraryDatabase.SetAndReadBack(_connection, OpenSql.SetTempStore, OpenSql.GetTempStore, "2", "temp_store");
        LibraryDatabase.SetAndReadBack(_connection, OpenSql.SetForeignKeys, OpenSql.GetForeignKeys, "1", "foreign_keys");
        LibraryDatabase.SetAndReadBack(_connection, OpenSql.SetTrustedSchema, OpenSql.GetTrustedSchema, "0", "trusted_schema");
        if (importCache) LibraryDatabase.SetAndReadBack(_connection, OpenSql.SetImportCacheSize, OpenSql.GetCacheSize, "-65536", "cache_size");
        if (_faults?.LimitDatabaseToTwoThousandPages == true)
        {
            using var limit = _connection.CreateCommand();
            limit.CommandText = FaultSql.LimitToTwoThousandPages;
            limit.ExecuteNonQuery();
        }
    }

    /// <summary>The OBS-15 check, for the importer's row counter and any other long loop. Faults and throws when not current.</summary>
    internal void CheckCurrent(MutationLease lease) => lease.Owner?.Require(lease, _operation, _allowed);

    internal void Begin(MutationLease lease)
    {
        Guard(lease, "BEGIN IMMEDIATE");
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
        Guard(lease, "COMMIT");
        using var command = _connection.CreateCommand();
        command.CommandText = TransactionSql.Commit;
        command.ExecuteNonQuery();
        _inTransaction = false;
    }

    /// <summary>Rolls back. A rollback only removes uncommitted work, so it is allowed after the lease has gone stale or the
    /// interlock has Faulted (best-effort release, OBS-13): it takes the lease for A-25's sake but does not require it current.</summary>
    internal void Rollback(MutationLease lease)
    {
        _ = lease;
        if (!_inTransaction) return;
        using var command = _connection.CreateCommand();
        command.CommandText = TransactionSql.Rollback;
        command.ExecuteNonQuery();
        _inTransaction = false;
    }

    /// <summary>Prepares a constant statement. <paramref name="sql"/> must be one of the <c>*Sql</c> constants (A-05).</summary>
    internal WriterStatement Prepare(MutationLease lease, string sql, params string[] parameterNames)
    {
        Guard(lease, "prepare a statement");
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        var parameters = new SqliteParameter[parameterNames.Length];
        for (var i = 0; i < parameterNames.Length; i++)
        {
            parameters[i] = command.CreateParameter();
            parameters[i].ParameterName = parameterNames[i];
            command.Parameters.Add(parameters[i]);
        }
        return new WriterStatement(command, parameters, this);
    }

    internal void Guard(MutationLease lease, string what) => lease.Owner?.Require(lease, $"{_operation}: {what}", _allowed);

    /// <summary>Closes the connection (a transaction still open is rolled back by SQLite) and tells the lease the resource is
    /// released. A release step that throws enters Faulted (OBS-13).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Interlocked.Decrement(ref _openWriters);
        try
        {
            _connection.Dispose();
            _openedBy.ResourceReleased();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _openedBy.ReleaseFailed("writer connection: " + ex.GetType().Name);
        }
    }
}

/// <summary>A prepared constant statement on the writer. Executing it needs a current lease of an allowed kind (OBS-15).</summary>
internal sealed class WriterStatement : IDisposable
{
    private readonly SqliteCommand _command;
    private readonly SqliteParameter[] _parameters;
    private readonly WriterConnection _owner;

    internal WriterStatement(SqliteCommand command, SqliteParameter[] parameters, WriterConnection owner)
    {
        _command = command;
        _parameters = parameters;
        _owner = owner;
    }

    /// <summary>Binds a value by position. Always a bound parameter: nothing is ever spliced into the SQL text (SEC-04).</summary>
    internal WriterStatement Set(int index, object? value)
    {
        _parameters[index].Value = value ?? DBNull.Value;
        return this;
    }

    internal int ExecuteNonQuery(MutationLease lease) => ExecuteNonQuery(lease, CancellationToken.None);

    /// <summary>Executes the statement; cancelling the token interrupts it (<c>SqliteCommand.Cancel</c>), which rolls the
    /// statement back and surfaces as <see cref="OperationCanceledException"/>.</summary>
    internal int ExecuteNonQuery(MutationLease lease, CancellationToken cancellation)
    {
        _owner.Guard(lease, "execute");
        using var registration = cancellation.CanBeCanceled ? cancellation.Register(_command.Cancel) : default;
        try { return _command.ExecuteNonQuery(); }
        catch (SqliteException) when (cancellation.IsCancellationRequested) { throw new OperationCanceledException(cancellation); }
    }

    internal object? ExecuteScalar(MutationLease lease)
    {
        _owner.Guard(lease, "execute");
        var value = _command.ExecuteScalar();
        return value is DBNull ? null : value;
    }

    /// <summary>Executes a constant SELECT and hands the reader to <paramref name="read"/>. The lease is checked first.</summary>
    internal void ExecuteReader(MutationLease lease, Action<SqliteDataReader> read)
    {
        _owner.Guard(lease, "execute");
        using var reader = _command.ExecuteReader();
        read(reader);
    }

    public void Dispose() => _command.Dispose();
}

/// <summary>A separate <c>Mode=ReadOnly</c> connection (CONC-05). It opens, reads and closes without writing any file (OBS-03, Q-21).</summary>
internal sealed class ReaderConnection : IQueryRunner, IDisposable
{
    private readonly SqliteConnection _connection;
    private bool _disposed;

    internal ReaderConnection(SqliteConnection connection) => _connection = connection;

    /// <summary>Cancelled by the in-process gate when a writer asks for it (CONC-06): the running command is interrupted and
    /// the read surfaces as <see cref="OperationCanceledException"/>, to be re-run by its page.</summary>
    internal CancellationToken Token { get; set; }

    /// <summary>The reader's settings: the build's default cache (2 MiB), no temp files, no schema-defined functions. These
    /// statements read the schema, so a reader that meets a hot journal fails here with <c>SQLITE_READONLY_ROLLBACK</c> and
    /// changes nothing.</summary>
    internal void Configure()
    {
        LibraryDatabase.SetAndReadBack(_connection, OpenSql.SetTempStore, OpenSql.GetTempStore, "2", "temp_store");
        LibraryDatabase.SetAndReadBack(_connection, OpenSql.SetTrustedSchema, OpenSql.GetTrustedSchema, "0", "trusted_schema");
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
            for (var i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }, parameters);
        return rows;
    }

    internal long Long(string sql, params (string Name, object? Value)[] parameters) => Convert.ToInt64(Scalar(sql, parameters) ?? 0L, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Executes a constant query and passes each row to <paramref name="each"/>.</summary>
    public void Rows(string sql, Action<SqliteDataReader> each, params (string Name, object? Value)[] parameters)
    {
        using var command = Build(sql, parameters);
        using var registration = Token.CanBeCanceled ? Token.Register(command.Cancel) : default;
        try
        {
            using var reader = command.ExecuteReader();
            while (reader.Read()) each(reader);
        }
        catch (SqliteException) when (Token.IsCancellationRequested) { throw new OperationCanceledException(Token); }
    }

    /// <summary>The constant query plan of a statement, one line per step (A-24). Parameters are bound as NULL.</summary>
    internal List<string> ExplainPlan(string sql, params string[] parameterNames)
    {
        var plan = new List<string>();
        using var command = _connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        foreach (var name in parameterNames) command.Parameters.AddWithValue(name, DBNull.Value);
        using var reader = command.ExecuteReader();
        while (reader.Read()) plan.Add(reader.GetString(3));
        return plan;
    }

    /// <summary>Runs <c>PRAGMA quick_check</c> and returns its first row ("ok" when intact). Only after an error that reports
    /// corruption (§6.7), never at every open. A failure of the check itself is returned as text, which is not "ok".</summary>
    internal string QuickCheck()
    {
        try { return LibraryDatabase.ReadBack(_connection, OpenSql.QuickCheck); }
        catch (SqliteException ex) { return "quick_check failed: " + ex.Message; }
    }

    internal string PragmaText(string sql) => LibraryDatabase.ReadBack(_connection, sql);

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

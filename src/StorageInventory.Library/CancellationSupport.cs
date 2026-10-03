using System.Diagnostics;
using SQLitePCL;

namespace StorageInventory.Library;

/// <summary>
/// The save token's observation record (CAN-01d, CAN-03). Every place that looks at the token calls <see cref="Check"/> (explicit
/// checks between statements, at every IMP-11 check, after the verification) or <see cref="Note"/> (the engine's progress callback
/// inside a statement), and the checker records when: the largest gap between two consecutive observations is the number TEST-P1
/// reports against the 0.5 s of CAN-01d. It is touched only by the thread that runs the import (the progress callback runs on the
/// thread that steps the statement), so it needs no lock; the token itself is thread-safe.
/// </summary>
internal sealed class TokenChecker(CancellationToken token)
{
    private long _last = Stopwatch.GetTimestamp();
    private long _maxGap;
    private long _observations;

    /// <summary>The save token.</summary>
    internal CancellationToken Token { get; } = token;

    /// <summary>How many times the token was looked at (explicit checks and progress callbacks).</summary>
    internal long Observations => _observations;

    /// <summary>The longest time between two consecutive observations since the checker was created.</summary>
    internal TimeSpan MaxGap => Stopwatch.GetElapsedTime(0, _maxGap);

    /// <summary>The time since the last observation.</summary>
    internal TimeSpan SinceLast => Stopwatch.GetElapsedTime(_last);

    /// <summary>An explicit check: records the observation and throws <see cref="OperationCanceledException"/> when the token is
    /// cancelled (the caller rolls back).</summary>
    internal void Check()
    {
        Note();
        Token.ThrowIfCancellationRequested();
    }

    /// <summary>Records one observation and reports whether the token is cancelled. Never throws: it is called from the engine's
    /// progress callback, where an exception must not cross the native boundary.</summary>
    internal bool Note()
    {
        var now = Stopwatch.GetTimestamp();
        var gap = now - _last;
        if (gap > _maxGap) _maxGap = gap;
        _last = now;
        _observations++;
        return Token.IsCancellationRequested;
    }

    /// <summary>Closes the record at the end of the transaction: the time since the last observation counts as a gap too.</summary>
    internal void Close()
    {
        var gap = Stopwatch.GetTimestamp() - _last;
        if (gap > _maxGap) _maxGap = gap;
    }
}

/// <summary>
/// SQLite's progress callback on ONE connection: every <see cref="Opcodes"/> virtual-machine steps of every statement running on
/// that connection it records an observation of the save token and, when the token is cancelled, interrupts the running statement
/// (which surfaces as a cancellation, never as an error). It is how a long verification query is made observable without splitting
/// it (CAN-01d: "each query runs over bounded key ranges, or under the engine's progress callback").
/// <para><b>Lifecycle and ownership:</b> constructed with the connection's handle, which the owning connection keeps alive; the
/// callback is installed by the constructor and removed by <see cref="Dispose"/> (an unconditional <c>sqlite3_progress_handler(db, 0,
/// null)</c>), so no callback outlives the scope that asked for it. <b>Thread safety:</b> the callback runs on the thread stepping
/// the statement and touches only the <see cref="TokenChecker"/> and the thread-safe token; it returns without throwing. <b>Isolation:</b>
/// the handler belongs to the connection's handle, so other connections of the process (readers, a later writer) are unaffected.</para>
/// </summary>
internal sealed class ProgressGuard : IDisposable
{
    /// <summary>Virtual-machine steps between callbacks: a few milliseconds of work, and at least three steps per row examined, so
    /// well under 65,536 rows between observations (CAN-01d).</summary>
    internal const int Opcodes = 20_000;

    private readonly sqlite3 _db;
    private bool _disposed;

    internal ProgressGuard(sqlite3 db, TokenChecker checker)
    {
        _db = db;
        raw.sqlite3_progress_handler(db, Opcodes, _ => checker.Note() ? 1 : 0, null);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        raw.sqlite3_progress_handler(_db, 0, null, null);
    }
}

/// <summary>A cancellation scope on the writer: the progress callback and the connection's scope token, removed together.</summary>
internal sealed class CancellationScope : IDisposable
{
    private readonly WriterConnection _writer;
    private readonly ProgressGuard _guard;
    private bool _disposed;

    internal CancellationScope(WriterConnection writer, ProgressGuard guard)
    {
        _writer = writer;
        _guard = guard;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _guard.Dispose();
        _writer.EndCancellationScope();
    }
}

namespace StorageInventory.Library;

/// <summary>
/// The in-process asynchronous reader/writer gate (CONC-06). Readers coexist. A writer takes the gate BEFORE
/// <c>BEGIN IMMEDIATE</c>: the moment it asks, new readers wait and every in-flight reader's token is cancelled (a long read, such as
/// a comparison, is cancelled through <c>SqliteCommand.Cancel</c> by the code that registered it, and re-run by its page
/// afterwards); it then waits for the readers to drain. So a writer never depends on SQLite's <c>busy_timeout</c>, which is only a
/// backstop. Lock order: interlock (state change only), then this gate, then SQLite (OBS-09).
/// <para>Fairness: a waiting writer blocks later readers, so a stream of readers cannot starve it. Writers are served in order;
/// readers that waited are admitted together when no writer is active or waiting. A grant is made INSIDE the gate's lock (the
/// reader is counted as active before its task completes), so a writer can never slip in between an admission and its holder
/// noticing it. After <see cref="Close"/> every request fails: the gate is closed for good when the interlock is Faulted.</para>
/// </summary>
internal sealed class ReaderWriterGate
{
    private readonly object _lock = new();
    private readonly LinkedList<ReaderWaiter> _readerWaiters = new();
    private readonly LinkedList<WriterWaiter> _writerWaiters = new();
    private readonly HashSet<ReaderTicket> _activeReaders = [];
    private bool _writerActive;
    private bool _closed;

    private sealed class ReaderWaiter(TaskCompletionSource<ReaderTicket> completion, CancellationToken caller)
    {
        internal TaskCompletionSource<ReaderTicket> Completion { get; } = completion;
        internal CancellationToken Caller { get; } = caller;
        internal CancellationTokenRegistration Registration { get; set; }
    }

    private sealed class WriterWaiter(TaskCompletionSource<WriterTicket> completion)
    {
        internal TaskCompletionSource<WriterTicket> Completion { get; } = completion;
        internal CancellationTokenRegistration Registration { get; set; }
    }

    internal int ActiveReaders { get { lock (_lock) return _activeReaders.Count; } }

    internal bool WriterActive { get { lock (_lock) return _writerActive; } }

    internal int WaitingWriters { get { lock (_lock) return _writerWaiters.Count; } }

    /// <summary>Closes the gate for good: every waiter and every later request fails with <see cref="ObjectDisposedException"/>,
    /// and every in-flight reader is told to stop.</summary>
    internal void Close()
    {
        List<ReaderWaiter> readers;
        List<WriterWaiter> writers;
        List<ReaderTicket> active;
        lock (_lock)
        {
            _closed = true;
            readers = [.. _readerWaiters];
            writers = [.. _writerWaiters];
            _readerWaiters.Clear();
            _writerWaiters.Clear();
            active = [.. _activeReaders];
        }
        foreach (var waiter in readers) waiter.Completion.TrySetException(new ObjectDisposedException(nameof(ReaderWriterGate)));
        foreach (var waiter in writers) waiter.Completion.TrySetException(new ObjectDisposedException(nameof(ReaderWriterGate)));
        foreach (var reader in active) reader.Preempt();
    }

    /// <summary>Waits for read access. The returned ticket's <see cref="ReaderTicket.Token"/> is cancelled when a writer asks for
    /// the gate or <paramref name="cancellation"/> is cancelled; disposing it releases the read.</summary>
    internal Task<ReaderTicket> AcquireReaderAsync(CancellationToken cancellation = default)
    {
        lock (_lock)
        {
            if (_closed) return Task.FromException<ReaderTicket>(new ObjectDisposedException(nameof(ReaderWriterGate)));
            cancellation.ThrowIfCancellationRequested();
            if (!_writerActive && _writerWaiters.Count == 0) return Task.FromResult(NewReaderLocked(cancellation));

            var completion = new TaskCompletionSource<ReaderTicket>(TaskCreationOptions.RunContinuationsAsynchronously);
            var waiter = new ReaderWaiter(completion, cancellation);
            var node = _readerWaiters.AddLast(waiter);
            waiter.Registration = cancellation.Register(() => CancelWaiter(_readerWaiters, node, completion, cancellation));
            return completion.Task;
        }
    }

    /// <summary>Waits for exclusive access: no new readers are admitted from the moment of the request, every in-flight
    /// reader is cancelled, and the call returns when they have all released.</summary>
    internal Task<WriterTicket> AcquireWriterAsync(CancellationToken cancellation = default)
    {
        List<ReaderTicket> toPreempt;
        Task<WriterTicket> task;
        lock (_lock)
        {
            if (_closed) return Task.FromException<WriterTicket>(new ObjectDisposedException(nameof(ReaderWriterGate)));
            cancellation.ThrowIfCancellationRequested();
            if (!_writerActive && _activeReaders.Count == 0 && _writerWaiters.Count == 0)
            {
                _writerActive = true;
                return Task.FromResult(new WriterTicket(this));
            }
            var completion = new TaskCompletionSource<WriterTicket>(TaskCreationOptions.RunContinuationsAsynchronously);
            var waiter = new WriterWaiter(completion);
            var node = _writerWaiters.AddLast(waiter);
            waiter.Registration = cancellation.Register(() => CancelWaiter(_writerWaiters, node, completion, cancellation));
            toPreempt = [.. _activeReaders];
            task = completion.Task;
        }
        foreach (var reader in toPreempt) reader.Preempt();
        return task;
    }

    private ReaderTicket NewReaderLocked(CancellationToken cancellation)
    {
        var ticket = new ReaderTicket(this, cancellation);
        _activeReaders.Add(ticket);
        return ticket;
    }

    private void CancelWaiter<T, TTicket>(LinkedList<T> list, LinkedListNode<T> node, TaskCompletionSource<TTicket> completion, CancellationToken token)
    {
        List<Action> signals;
        lock (_lock)
        {
            if (node.List is not null) list.Remove(node);
            AdmitLocked(out signals);
        }
        completion.TrySetCanceled(token);
        foreach (var signal in signals) signal();
    }

    internal void ReleaseReader(ReaderTicket ticket)
    {
        List<Action> signals;
        lock (_lock)
        {
            if (!_activeReaders.Remove(ticket)) return;
            AdmitLocked(out signals);
        }
        foreach (var signal in signals) signal();
    }

    internal void ReleaseWriter()
    {
        List<Action> signals;
        lock (_lock)
        {
            if (!_writerActive) return;
            _writerActive = false;
            AdmitLocked(out signals);
        }
        foreach (var signal in signals) signal();
    }

    /// <summary>Decides who may proceed now and makes the grants, inside the lock; the returned actions complete the tasks and run
    /// outside it. A waiter that was cancelled meanwhile gives its grant straight back.</summary>
    private void AdmitLocked(out List<Action> signals)
    {
        signals = [];
        if (_closed || _writerActive) return;
        if (_writerWaiters.Count > 0)
        {
            if (_activeReaders.Count == 0)
            {
                var first = _writerWaiters.First!.Value;
                _writerWaiters.RemoveFirst();
                _writerActive = true;
                var ticket = new WriterTicket(this);
                signals.Add(() =>
                {
                    first.Registration.Dispose();
                    if (!first.Completion.TrySetResult(ticket)) ticket.Dispose();
                });
            }
            return;
        }
        while (_readerWaiters.Count > 0)
        {
            var next = _readerWaiters.First!.Value;
            _readerWaiters.RemoveFirst();
            var ticket = NewReaderLocked(next.Caller);
            signals.Add(() =>
            {
                next.Registration.Dispose();
                if (!next.Completion.TrySetResult(ticket)) ticket.Dispose();
            });
        }
    }
}

/// <summary>A read grant. <see cref="Token"/> is cancelled when a writer asks for the gate: the reader stops and its page re-runs.</summary>
internal sealed class ReaderTicket : IDisposable
{
    private readonly ReaderWriterGate _gate;
    private readonly CancellationTokenSource _preempted;
    private int _disposed;

    internal ReaderTicket(ReaderWriterGate gate, CancellationToken caller)
    {
        _gate = gate;
        _preempted = CancellationTokenSource.CreateLinkedTokenSource(caller);
    }

    internal CancellationToken Token => _preempted.Token;

    internal void Preempt()
    {
        try { _preempted.Cancel(); }
        catch (ObjectDisposedException) { /* already released */ }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _gate.ReleaseReader(this);
        _preempted.Dispose();
    }
}

/// <summary>An exclusive grant.</summary>
internal sealed class WriterTicket(ReaderWriterGate gate) : IDisposable
{
    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        gate.ReleaseWriter();
    }
}

namespace StorageInventory.Core.Scanning;

/// <summary>The exception classes of the scan pipeline (CAT-01 to CAT-03, D-45).</summary>
internal static class ExceptionClasses
{
    /// <summary>Class C: catastrophic but catchable. Never isolated, never retried, never turned into a normal outcome
    /// by new code.</summary>
    public static bool IsCatastrophic(Exception ex) => ex is OutOfMemoryException or InsufficientExecutionStackException;

    /// <summary>Class B: cancellation of this run. An <see cref="OperationCanceledException"/> while the run's token is
    /// not cancelled is class A.</summary>
    public static bool IsCancellation(Exception ex, CancellationToken runToken) =>
        ex is OperationCanceledException && runToken.IsCancellationRequested;
}

/// <summary>An isolated observer that failed with a class A exception and was disconnected from the scan.</summary>
/// <param name="Callback">The callback that threw, for example <c>OnFile</c>.</param>
internal sealed record ObserverFault(IScanObserver Observer, string Callback, Exception Exception);

/// <summary>
/// Delivers one observation stream to every consumer of a scan (SINK-01, SINK-04, SINK-05, D-11). The traversal
/// happens once; the fan-out repeats each call to:
/// <list type="bullet">
/// <item><description>the <b>critical</b> observer (v1's CSV reports, behind <see cref="OutputGuard"/>): its exceptions
/// propagate and abort the scan exactly as in v1;</description></item>
/// <item><description>the <b>isolated</b> observers (the spool writer, test observers): a class A exception is caught,
/// the observer is marked faulted and receives no further calls except <see cref="OnScanEnded"/>, and the scan
/// continues. Class B (cancellation with the run's token set) and class C (<see cref="ExceptionClasses"/>) are never
/// isolated: they propagate.</description></item>
/// </list>
/// The guard is never applied to an isolated observer, so an isolated observer's I/O error can never become the scan's
/// <see cref="ScanFailureKind.OutputError"/> (REV-M03). Delivery is synchronous on the scanning thread: there is no
/// queue, channel or writer thread, so an isolated observer adds at most its own call time to the critical path.
/// </summary>
internal sealed class ObserverFanOut : IScanObserver
{
    private sealed class Slot(IScanObserver observer)
    {
        public IScanObserver Observer { get; } = observer;
        public bool Started;
        public bool Faulted;
        public bool Ended;
    }

    private readonly Slot _critical;
    private readonly Slot[] _isolated;
    private readonly CancellationToken _runToken;
    private readonly List<ObserverFault> _faults = [];

    public ObserverFanOut(IScanObserver critical, IReadOnlyList<IScanObserver> isolated, CancellationToken runToken)
    {
        ArgumentNullException.ThrowIfNull(critical);
        ArgumentNullException.ThrowIfNull(isolated);
        _critical = new Slot(critical);
        _isolated = isolated.Select(o => new Slot(o ?? throw new ArgumentException("An observer is null.", nameof(isolated)))).ToArray();
        _runToken = runToken;
    }

    /// <summary>Isolated observers that failed, in the order they failed.</summary>
    public IReadOnlyList<ObserverFault> Faults => _faults;

    /// <summary>Isolated observers are started first, so that every one of them is started (and later ended) even when
    /// the critical observer cannot create its reports.</summary>
    public void OnScanStarted(in ScanStartInfo start)
    {
        foreach (var slot in _isolated)
        {
            slot.Started = true;
            try { slot.Observer.OnScanStarted(start); }
            catch (Exception ex) when (Isolatable(ex)) { Fault(slot, nameof(OnScanStarted), ex); }
        }
        _critical.Started = true;
        _critical.Observer.OnScanStarted(start);
    }

    public void OnFile(in FileInventoryRecord file, int folderIndex)
    {
        _critical.Observer.OnFile(file, folderIndex);
        foreach (var slot in _isolated)
        {
            if (slot.Faulted) continue;
            try { slot.Observer.OnFile(file, folderIndex); }
            catch (Exception ex) when (Isolatable(ex)) { Fault(slot, nameof(OnFile), ex); }
        }
    }

    public void OnError(ScanErrorRecord error)
    {
        _critical.Observer.OnError(error);
        foreach (var slot in _isolated)
        {
            if (slot.Faulted) continue;
            try { slot.Observer.OnError(error); }
            catch (Exception ex) when (Isolatable(ex)) { Fault(slot, nameof(OnError), ex); }
        }
    }

    public void OnFolderFinalised(int index, int parentIndex, FolderInventoryRecord folder)
    {
        _critical.Observer.OnFolderFinalised(index, parentIndex, folder);
        foreach (var slot in _isolated)
        {
            if (slot.Faulted) continue;
            try { slot.Observer.OnFolderFinalised(index, parentIndex, folder); }
            catch (Exception ex) when (Isolatable(ex)) { Fault(slot, nameof(OnFolderFinalised), ex); }
        }
    }

    /// <summary>The end of a scan whose reports are complete: the critical observer first, then the isolated ones, so
    /// an isolated observer is told "Finished" only once nothing of v1's output can still fail. Faulted isolated
    /// observers are ended too (and must not treat the end as success for themselves).</summary>
    public void OnScanEnded(in ScanEndInfo end)
    {
        if (!_critical.Ended)
        {
            _critical.Ended = true;
            _critical.Observer.OnScanEnded(end);
        }
        foreach (var slot in _isolated)
        {
            if (slot.Ended || !slot.Started) continue;
            slot.Ended = true;
            try { slot.Observer.OnScanEnded(end); }
            catch (Exception ex) when (Isolatable(ex)) { Fault(slot, nameof(OnScanEnded), ex); }
        }
    }

    /// <summary>
    /// Ends every started observer that has not been ended, after the scan has already become Cancelled or Failed.
    /// Nothing of the run can become valid any more, so each observer is told in its own guard and no exception
    /// escapes: isolated observers that throw are recorded as faulted; the critical observer's exceptions are dropped
    /// (the scan's outcome is already decided, exactly as v1's). A class C exception stops the notifications; it is
    /// reported through the return value so the caller can flag the run as catastrophic.
    /// </summary>
    /// <returns>True if a class C exception occurred while ending.</returns>
    public bool EndRemaining(ScanEndInfo end)
    {
        foreach (var slot in _isolated.Prepend(_critical))
        {
            if (slot.Ended || !slot.Started) continue;
            slot.Ended = true;
            try
            {
                slot.Observer.OnScanEnded(end);
            }
            catch (Exception ex)
            {
                if (slot != _critical) Fault(slot, nameof(OnScanEnded), ex);
                if (ExceptionClasses.IsCatastrophic(ex)) return true;
            }
        }
        return false;
    }

    private bool Isolatable(Exception ex) => !ExceptionClasses.IsCatastrophic(ex) && !ExceptionClasses.IsCancellation(ex, _runToken);

    private void Fault(Slot slot, string callback, Exception ex)
    {
        if (!slot.Faulted) _faults.Add(new ObserverFault(slot.Observer, callback, ex));
        slot.Faulted = true;
    }
}

/// <summary>
/// Marks the critical observer's I/O failures as report-write failures, so they are never confused with scan problems
/// (v1's <c>OutputGuardSink</c>). It wraps only the critical observer (SINK-05).
/// </summary>
internal sealed class OutputGuard(IScanObserver inner) : IScanObserver
{
    public void OnScanStarted(in ScanStartInfo start)
    {
        try { inner.OnScanStarted(start); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new ReportWriteException(ex); }
    }

    public void OnFile(in FileInventoryRecord file, int folderIndex)
    {
        try { inner.OnFile(file, folderIndex); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new ReportWriteException(ex); }
    }

    public void OnError(ScanErrorRecord error)
    {
        try { inner.OnError(error); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new ReportWriteException(ex); }
    }

    public void OnFolderFinalised(int index, int parentIndex, FolderInventoryRecord folder)
    {
        try { inner.OnFolderFinalised(index, parentIndex, folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new ReportWriteException(ex); }
    }

    public void OnScanEnded(in ScanEndInfo end)
    {
        try { inner.OnScanEnded(end); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new ReportWriteException(ex); }
    }
}

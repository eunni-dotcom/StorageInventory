namespace StorageInventory.Library;

/// <summary>What a Library mutation lease is for (OBS-08, exhaustive). Appended values only; never renumbered.</summary>
internal enum MutationKind
{
    /// <summary>LIB-08: path validation, lock acquisition, hot-journal rollback, recovery. The start-up open holds one.</summary>
    Open = 1,

    /// <summary>LIB-07 / T-CREATE: directory, lock file, main file, schema.</summary>
    Create = 2,

    /// <summary>P1: creation if needed (or opening a Library that LIB-07 step 4 finds), then T0. Hands off to observation.</summary>
    Prepare = 3,

    /// <summary>P5 to P7: T-IMPORT and T-OUTCOME. Only ever entered by the hand-off from an observation (OBS-10).</summary>
    Save = 4,

    /// <summary>T-DELETE.</summary>
    Delete = 5,

    /// <summary>LIB-13: the only rename.</summary>
    SetAside = 6,
}

/// <summary>The one state of the Library activity interlock (OBS-07).</summary>
internal enum InterlockStateKind
{
    Idle = 0,
    Mutating = 1,
    Observing = 2,
    Faulted = 3,
}

/// <summary>A consistent reading of the interlock, taken inside its monitor.</summary>
/// <param name="Kind">Idle, Mutating, Observing or Faulted.</param>
/// <param name="Mutation">The kind of the current mutation lease while Mutating.</param>
/// <param name="Owner">The capture or run that owns the current lease (0 when the lease belongs to no capture).</param>
/// <param name="LeaseId">The id of the current lease; 0 when there is none.</param>
/// <param name="Reason">Words naming the operation in progress, for the UI (UI-13); the restart instruction when Faulted.</param>
/// <param name="Epoch">The mutation epoch: how many mutation leases have been granted (OBS-04a).</param>
internal readonly record struct InterlockSnapshot(InterlockStateKind Kind, MutationKind? Mutation, long Owner, long LeaseId, string Reason, long Epoch);

/// <summary>An operation reached the Library with a lease that is not current (OBS-15). It is refused before any I/O and the
/// interlock is Faulted.</summary>
internal sealed class LeaseViolationException(string message) : InvalidOperationException(message);

/// <summary>The window's two epoch readings and whether any other transition happened between them (OBS-04a).</summary>
internal readonly record struct ObservationClose(long EpochAtOpen, long EpochAtClose, bool OtherTransition)
{
    public bool Unchanged => EpochAtOpen == EpochAtClose && !OtherTransition;
}

/// <summary>
/// The Library activity interlock (D-51, OBS-07 to OBS-15): one state machine, exactly one of Idle, Mutating(kind, owner),
/// Observing(run) or Faulted, in ONE field group guarded by a private monitor. The monitor is held only to test and change the
/// state (never across I/O or an <c>await</c>); every request is a non-blocking try that grants or refuses at once; there is no queue.
/// <para><b>Start-up (OBS-14):</b> the interlock is constructed already in Mutating(Open) holding the start-up lease, so there
/// is no instant at which it looks Idle before the Library has been examined. It first reaches Idle when that lease ends cleanly.</para>
/// <para><b>Faulted (OBS-13)</b> is terminal: it has no exit transition, grants nothing, and ending a lease while Faulted changes
/// nothing. A lease ends cleanly only if every resource it opened was released and no class C failure was reported.</para>
/// <para><b>Leases (OBS-15):</b> a lease id is never reused; a lease authorises work only while this interlock's current lease id
/// equals it (and so it was issued here, has not been disposed and has not been handed off). The interlock performs no I/O itself.</para>
/// </summary>
internal sealed class LibraryInterlock
{
    internal const string OpeningReason = "Opening the Library…";
    internal const string FaultedReason = "StorageInventory ran out of resources and must be restarted. Close StorageInventory and start it again.";

    private readonly object _monitor = new();
    private InterlockStateKind _kind = InterlockStateKind.Mutating;
    private MutationKind? _mutation = MutationKind.Open;
    private long _owner;
    private long _leaseId = 1;
    private long _nextLeaseId = 2;
    private long _epoch = 1;                       // the start-up Open lease is a grant
    private long _transitions;
    private long _windowStartTransition;
    private long _windowEpochAtOpen;
    private string _reason = OpeningReason;
    private int _resources;
    private bool _flaggedUnclean;
    private bool _startupTaken;
    private string? _faultReason;
    private Action<InterlockSnapshot>? _transitionHook;
    private Action<InterlockSnapshot>? _stateChanged;

    /// <summary>The state with a reason naming the operation in progress, published for the UI to bind to (UI-13). Raised
    /// after the monitor is released, so a handler may do anything; it can observe a newer state than the one it was told of.</summary>
    internal event Action<InterlockSnapshot>? StateChanged
    {
        add { lock (_monitor) _stateChanged += value; }
        remove { lock (_monitor) _stateChanged -= value; }
    }

    /// <summary>A test hook called after every transition (A-26: the side-file audit takes a names-only listing here).
    /// Production code never sets it.</summary>
    internal Action<InterlockSnapshot>? TransitionHook
    {
        get { lock (_monitor) return _transitionHook; }
        set { lock (_monitor) _transitionHook = value; }
    }

    internal InterlockSnapshot Snapshot
    {
        get { lock (_monitor) return Read(); }
    }

    internal bool IsFaulted
    {
        get { lock (_monitor) return _kind == InterlockStateKind.Faulted; }
    }

    internal long MutationEpoch
    {
        get { lock (_monitor) return _epoch; }
    }

    /// <summary>Why the interlock entered Faulted (diagnostics, tests); null until then.</summary>
    internal string? FaultReason
    {
        get { lock (_monitor) return _faultReason; }
    }

    /// <summary>Hands out the start-up Open lease, once. A second call, or a call after it ended, is a lifecycle defect.</summary>
    internal MutationLease TakeStartupLease()
    {
        InterlockSnapshot? changed = null;
        MutationLease lease = default;
        var ok = false;
        lock (_monitor)
        {
            if (!_startupTaken && _kind == InterlockStateKind.Mutating && _leaseId == 1 && _mutation == MutationKind.Open)
            {
                _startupTaken = true;
                lease = new MutationLease(this, 1, MutationKind.Open);
                ok = true;
            }
            else
            {
                changed = FaultLocked("the start-up lease was requested a second time, or after it ended");
            }
        }
        Notify(changed);
        return ok ? lease : throw new LeaseViolationException("The start-up lease is not available.");
    }

    /// <summary>Runs the start-up open (OBS-14): the interlock hands its start-up lease to <paramref name="work"/> as a PARAMETER and
    /// ends it, cleanly or not, when the work returns. No method of the Library other than the interlock ever holds a lease it did
    /// not receive as a parameter (A-25 (a): there is no lease-producer exception).</summary>
    internal T RunStartup<T>(Func<MutationLease, T> work)
    {
        var lease = TakeStartupLease();
        try { return work(lease); }
        finally { lease.Dispose(); }
    }

    /// <summary>Asks for a mutation lease and, when it is granted, hands it to <paramref name="work"/> as a parameter and ends it when
    /// the work returns (A-25 (a)). A refusal is an outcome, not an error: it returns false with the reason.</summary>
    internal bool TryRunMutation<T>(MutationKind kind, long owner, Func<MutationLease, T> work, out T? result, out string refusal)
    {
        result = default;
        if (!TryBeginMutation(kind, owner, out var lease, out refusal)) return false;
        try { result = work(lease); }
        finally { lease.Dispose(); }
        return true;
    }

    /// <summary>Asks for a mutation lease. Granted only from Idle (OBS-07); a Save lease exists only as a hand-off.</summary>
    internal bool TryBeginMutation(MutationKind kind, long owner, out MutationLease lease, out string refusal)
    {
        InterlockSnapshot? changed = null;
        lease = default;
        refusal = "";
        lock (_monitor)
        {
            if (_kind == InterlockStateKind.Faulted) { refusal = FaultedReason; }
            else if (kind == MutationKind.Save) { refusal = "A Save lease is only handed over from a scan's observation window."; }
            else if (_kind != InterlockStateKind.Idle) { refusal = RefusalLocked(); }
            else
            {
                var id = _nextLeaseId++;
                _kind = InterlockStateKind.Mutating;
                _mutation = kind;
                _owner = owner;
                _leaseId = id;
                _epoch++;
                _reason = ReasonFor(kind);
                _resources = 0;
                _flaggedUnclean = false;
                _transitions++;
                lease = new MutationLease(this, id, kind);
                changed = Read();
            }
        }
        Notify(changed);
        return changed is not null;
    }

    /// <summary>Asks to open an observation window for a scan that saves nothing (Idle → Observing, OBS-10).</summary>
    internal bool TryBeginObservation(long run, out ObservationLease lease, out string refusal)
    {
        InterlockSnapshot? changed = null;
        lease = default;
        refusal = "";
        lock (_monitor)
        {
            if (_kind == InterlockStateKind.Faulted) { refusal = FaultedReason; }
            else if (_kind != InterlockStateKind.Idle) { refusal = RefusalLocked(); }
            else
            {
                var id = _nextLeaseId++;
                _kind = InterlockStateKind.Observing;
                _mutation = null;
                _owner = run;
                _leaseId = id;
                _reason = "Scanning…";
                _resources = 0;
                _flaggedUnclean = false;
                _transitions++;
                _windowStartTransition = _transitions;
                _windowEpochAtOpen = _epoch;
                lease = new ObservationLease(this, id, run);
                changed = Read();
            }
        }
        Notify(changed);
        return changed is not null;
    }

    /// <summary>Prepare → Observing, atomically, by the same capture (OBS-10). A non-current lease faults (OBS-15), and so does a
    /// Prepare lease that still holds an open resource (OBS-12: the writer is closed before the window opens).</summary>
    internal ObservationLease HandOffToObservation(MutationLease lease)
    {
        InterlockSnapshot? changed;
        ObservationLease next = default;
        string? failure;
        lock (_monitor)
        {
            failure = CheckLocked(lease, [MutationKind.Prepare]);
            if (failure is null && (_resources != 0 || _flaggedUnclean)) failure = "the Prepare lease still holds an open resource or reported a failed release";
            if (failure is null)
            {
                var id = _nextLeaseId++;
                var run = _owner;
                _kind = InterlockStateKind.Observing;
                _mutation = null;
                _leaseId = id;
                _reason = "Scanning…";
                _transitions++;
                _windowStartTransition = _transitions;
                _windowEpochAtOpen = _epoch;
                next = new ObservationLease(this, id, run);
                changed = Read();
            }
            else
            {
                changed = FaultLocked("hand-off to an observation: " + failure);
            }
        }
        Notify(changed);
        return failure is null ? next : throw new LeaseViolationException("Refused before any I/O: hand-off to an observation: " + failure + ".");
    }

    /// <summary>Observing → Save, atomically, by the same capture; reports M1 and M2 and whether any other transition happened
    /// in between (OBS-04a). M2 is read before the Save grant advances the epoch.</summary>
    internal MutationLease HandOffToSave(ObservationLease lease, out ObservationClose close)
    {
        InterlockSnapshot? changed;
        MutationLease next = default;
        string? failure;
        close = default;
        lock (_monitor)
        {
            failure = CheckObservationLocked(lease);
            if (failure is null && (_resources != 0 || _flaggedUnclean)) failure = "the observation still holds an open resource or reported a failed release";
            if (failure is null)
            {
                close = new ObservationClose(_windowEpochAtOpen, _epoch, _transitions != _windowStartTransition);
                var id = _nextLeaseId++;
                _kind = InterlockStateKind.Mutating;
                _mutation = MutationKind.Save;
                _leaseId = id;
                _epoch++;
                _reason = ReasonFor(MutationKind.Save);
                _resources = 0;
                _flaggedUnclean = false;
                _transitions++;
                next = new MutationLease(this, id, MutationKind.Save);
                changed = Read();
            }
            else
            {
                changed = FaultLocked("hand-off to a Save lease: " + failure);
            }
        }
        Notify(changed);
        return failure is null ? next : throw new LeaseViolationException("Refused before any I/O: hand-off to a Save lease: " + failure + ".");
    }

    /// <summary>Observing → Idle, on a clean end (a Save-off scan, OBS-10). Disposing the lease does the same.</summary>
    internal void EndObservation(ObservationLease lease) => End(lease.Owner == this ? lease.Id : 0, observation: true);

    /// <summary>Mutating → Idle, on a clean end; otherwise Faulted. Disposing the lease does the same.</summary>
    internal void EndMutation(MutationLease lease) => End(lease.Owner == this ? lease.Id : 0, observation: false);   // a lease of another interlock (or a default value) ends nothing here

    private void End(long leaseId, bool observation)
    {
        InterlockSnapshot? changed = null;
        lock (_monitor)
        {
            // A stale lease, a second Dispose, a lease of the other family and any lease ending in Faulted change nothing.
            if (_kind != InterlockStateKind.Faulted && leaseId != 0 && _leaseId == leaseId && observation == (_kind == InterlockStateKind.Observing))
            {
                if (_resources != 0 || _flaggedUnclean)
                {
                    changed = FaultLocked(_flaggedUnclean ? "a lease ended after a class C failure or a failed release" : "a lease ended with a resource still open");
                }
                else
                {
                    _kind = InterlockStateKind.Idle;
                    _mutation = null;
                    _owner = 0;
                    _leaseId = 0;
                    _reason = "";
                    _transitions++;
                    changed = Read();
                }
            }
        }
        Notify(changed);
    }

    /// <summary>Registers a resource the lease has opened (the writer connection, the spool, the root handle). The lease can end
    /// cleanly only when every registered resource has been released (OBS-13).</summary>
    internal void ResourceOpened(long leaseId)
    {
        lock (_monitor)
        {
            if (_leaseId == leaseId && _kind is InterlockStateKind.Mutating or InterlockStateKind.Observing) _resources++;
        }
    }

    internal void ResourceReleased(long leaseId)
    {
        lock (_monitor)
        {
            if (_leaseId == leaseId && _resources > 0) _resources--;
        }
    }

    /// <summary>A release step threw (OBS-13): Faulted in the same monitor section.</summary>
    internal void ReportReleaseFailure(long leaseId, string what) => FaultCurrent(leaseId, "a resource could not be released: " + what);

    /// <summary>A class C exception thrown inside the lease (CAT-03): Faulted in the same monitor section.</summary>
    internal void ReportClassC(long leaseId, string what) => FaultCurrent(leaseId, "a class C failure inside a lease: " + what);

    /// <summary>A class C exception that an App service observed outside any lease (CAT-04): Faulted from whatever state the
    /// interlock is in; a lease held at that moment can then no longer end at Idle.</summary>
    internal void ReportCatastrophic(string reason)
    {
        InterlockSnapshot? changed;
        lock (_monitor) changed = FaultLocked("a class C failure outside any lease: " + reason);
        Notify(changed);
    }

    /// <summary>True for the class C exceptions (CAT-03): out of memory and a stack that cannot grow.</summary>
    internal static bool IsClassC(Exception ex) => ex is OutOfMemoryException or InsufficientExecutionStackException;

    private void FaultCurrent(long leaseId, string reason)
    {
        InterlockSnapshot? changed = null;
        lock (_monitor)
        {
            if (_kind is InterlockStateKind.Mutating or InterlockStateKind.Observing && _leaseId == leaseId) changed = FaultLocked(reason);
        }
        Notify(changed);
    }

    /// <summary>The OBS-15 check for a mutation lease: current, of one of the named kinds. A failure refuses the operation
    /// BEFORE any I/O, enters Faulted (once) and throws; it never returns false. Runs inside the monitor and does no I/O.</summary>
    internal void Require(MutationLease lease, string operation, string detail, MutationKind[] allowed)
    {
        // the hot path (every statement of an import): check under the monitor and compose the words only on failure
        lock (_monitor)
        {
            if (CheckLocked(lease, allowed) is null) return;
        }
        Require(lease, operation + ": " + detail, allowed);
    }

    /// <summary>The check of an operation on a connection that one specific lease opened (OBS-15): the offered lease must be current
    /// and of an allowed kind (as <see cref="Require(MutationLease, string, string, MutationKind[])"/>) AND be that very lease:
    /// another current lease of this interlock (a later grant, a hand-off successor) does not authorise a connection that was not
    /// opened under it. A failure enters Faulted and throws before any I/O.</summary>
    internal void RequireOpenedBy(MutationLease lease, MutationLease openedBy, string operation, string detail, MutationKind[] allowed)
    {
        lock (_monitor)
        {
            if (CheckLocked(lease, allowed) is null && lease.Id == openedBy.Id) return;
        }
        InterlockSnapshot? changed = null;
        string? failure;
        lock (_monitor)
        {
            failure = CheckLocked(lease, allowed) ?? (lease.Id != openedBy.Id ? "the lease is not the one that opened this connection" : null);
            if (failure is not null) changed = FaultLocked($"{operation}: {detail}: {failure}");
        }
        Notify(changed);
        if (failure is not null) throw new LeaseViolationException($"Refused before any I/O: {operation}: {detail}: {failure}.");
    }

    /// <summary>The identity-only check used by a release (a rollback): the offered lease must be the one that opened the connection,
    /// but it need NOT be current, because a rollback removes uncommitted work and is allowed after the lease went stale or the
    /// interlock Faulted (best-effort release, OBS-13). A default lease, a foreign lease or another lease of this interlock is
    /// refused, enters Faulted and throws.</summary>
    internal void RequireSameLease(MutationLease lease, MutationLease openedBy, string operation, string detail)
    {
        if (lease.Owner == openedBy.Owner && lease.Owner == this && lease.Id == openedBy.Id) return;
        InterlockSnapshot? changed;
        lock (_monitor) changed = FaultLocked($"{operation}: {detail}: the lease is not the one that opened this connection");
        Notify(changed);
        throw new LeaseViolationException($"Refused before any I/O: {operation}: {detail}: the lease is not the one that opened this connection.");
    }

    internal void Require(MutationLease lease, string operation, params MutationKind[] allowed)
    {
        InterlockSnapshot? changed = null;
        string? failure;
        lock (_monitor)
        {
            failure = CheckLocked(lease, allowed);
            if (failure is not null) changed = FaultLocked($"{operation}: {failure}");
        }
        Notify(changed);
        if (failure is not null) throw new LeaseViolationException($"Refused before any I/O: {operation}: {failure}.");
    }

    internal void RequireObservation(ObservationLease lease, string operation)
    {
        InterlockSnapshot? changed = null;
        string? failure;
        lock (_monitor)
        {
            failure = CheckObservationLocked(lease);
            if (failure is not null) changed = FaultLocked($"{operation}: {failure}");
        }
        Notify(changed);
        if (failure is not null) throw new LeaseViolationException($"Refused before any I/O: {operation}: {failure}.");
    }

    /// <summary>A non-faulting query: is this lease current right now? For logging and tests.</summary>
    internal bool IsCurrent(MutationLease lease)
    {
        lock (_monitor) return lease.Owner == this && lease.Id != 0 && _leaseId == lease.Id && _kind == InterlockStateKind.Mutating;
    }

    internal bool IsCurrent(ObservationLease lease)
    {
        lock (_monitor) return lease.Owner == this && lease.Id != 0 && _leaseId == lease.Id && _kind == InterlockStateKind.Observing;
    }

    // ---- inside the monitor ----

    private string? CheckLocked(MutationLease lease, MutationKind[] allowed)
    {
        if (lease.Owner != this) return lease.Owner is null ? "no lease (a default lease value)" : "a lease of another LibrarySession";
        if (_kind == InterlockStateKind.Faulted) return "the interlock is Faulted";
        if (lease.Id == 0 || _leaseId != lease.Id || _kind != InterlockStateKind.Mutating) return "the lease is stale, disposed or was handed off";
        if (Array.IndexOf(allowed, lease.Kind) < 0 || _mutation != lease.Kind) return $"a {lease.Kind} lease cannot authorise this";
        return null;
    }

    private string? CheckObservationLocked(ObservationLease lease)
    {
        if (lease.Owner != this) return lease.Owner is null ? "no lease (a default lease value)" : "a lease of another LibrarySession";
        if (_kind == InterlockStateKind.Faulted) return "the interlock is Faulted";
        if (lease.Id == 0 || _leaseId != lease.Id || _kind != InterlockStateKind.Observing) return "the observation lease is stale, disposed or was handed off";
        return null;
    }

    /// <summary>Enters the terminal state. Returns the new snapshot only when this call made the transition, so that a hook
    /// sees each transition once.</summary>
    private InterlockSnapshot? FaultLocked(string reason)
    {
        if (_kind == InterlockStateKind.Faulted) return null;
        _kind = InterlockStateKind.Faulted;
        _mutation = null;
        _owner = 0;
        _leaseId = 0;               // no lease is current again, in this process
        _reason = FaultedReason;
        _faultReason = reason;
        _transitions++;
        return Read();
    }

    private InterlockSnapshot Read() => new(_kind, _mutation, _owner, _leaseId, _reason, _epoch);

    private string RefusalLocked() => _kind switch
    {
        InterlockStateKind.Observing => "A scan is running.",
        InterlockStateKind.Mutating => _reason,
        _ => "",
    };

    private static string ReasonFor(MutationKind kind) => kind switch
    {
        MutationKind.Open => OpeningReason,
        MutationKind.Create => "Creating the Library…",
        MutationKind.Prepare => "Preparing to save to History…",
        MutationKind.Save => "Saving to History…",
        MutationKind.Delete => "Deleting a snapshot…",
        MutationKind.SetAside => "Setting aside the Library…",
        _ => "",
    };

    /// <summary>Runs the hooks for a transition, outside the monitor.</summary>
    private void Notify(InterlockSnapshot? snapshot)
    {
        if (snapshot is not { } s) return;
        Action<InterlockSnapshot>? hook, changed;
        lock (_monitor) { hook = _transitionHook; changed = _stateChanged; }
        hook?.Invoke(s);
        changed?.Invoke(s);
    }
}

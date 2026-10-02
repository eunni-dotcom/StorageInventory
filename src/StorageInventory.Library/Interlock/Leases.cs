namespace StorageInventory.Library;

/// <summary>
/// A grant of the right to mutate the Library (OBS-08, OBS-09). Constructed only by <see cref="LibraryInterlock"/> (A-25 part c),
/// carries a lease id that is never reused, and authorises work only while it is current (OBS-15): issued by this interlock, the
/// interlock's current lease id equals it, it has not been disposed and has not been handed off. A <c>default</c> value is not a
/// lease. Disposing it ends the lease: back to Idle only on a clean end, otherwise Faulted (OBS-13). It is a value type, so a copy
/// is the same lease: ending any copy ends it, and a second <c>Dispose</c> is a harmless no-op.
/// </summary>
internal readonly struct MutationLease : IDisposable
{
    internal MutationLease(LibraryInterlock owner, long id, MutationKind kind)
    {
        Owner = owner;
        Id = id;
        Kind = kind;
    }

    /// <summary>The interlock that issued this lease; null for a <c>default</c> value.</summary>
    internal LibraryInterlock? Owner { get; }

    internal long Id { get; }

    internal MutationKind Kind { get; }

    /// <summary>Opens a resource of this lease (the writer connection, a spool): it must be closed before the lease ends.</summary>
    internal void ResourceOpened() => Owner?.ResourceOpened(Id);

    internal void ResourceReleased() => Owner?.ResourceReleased(Id);

    /// <summary>A release step threw: the interlock is Faulted at once (OBS-13).</summary>
    internal void ReleaseFailed(string what) => Owner?.ReportReleaseFailure(Id, what);

    /// <summary>A class C exception was thrown inside this lease: the interlock is Faulted at once (OBS-13).</summary>
    internal void ClassCFailure(Exception ex) => Owner?.ReportClassC(Id, ex.GetType().Name);

    /// <summary>Prepare → Observing, atomically (OBS-10).</summary>
    internal ObservationLease HandOffToObservation() => (Owner ?? throw new LeaseViolationException("Refused before any I/O: a default lease cannot be handed off.")).HandOffToObservation(this);

    public void Dispose() => Owner?.EndMutation(this);
}

/// <summary>
/// A grant of the right to observe: the window during which the Library must not change (OBS-01, INV-17). Constructed only by
/// <see cref="LibraryInterlock"/>; same validity rules as <see cref="MutationLease"/>.
/// </summary>
internal readonly struct ObservationLease : IDisposable
{
    internal ObservationLease(LibraryInterlock owner, long id, long run)
    {
        Owner = owner;
        Id = id;
        Run = run;
    }

    internal LibraryInterlock? Owner { get; }

    internal long Id { get; }

    /// <summary>The capture or run that owns the window.</summary>
    internal long Run { get; }

    internal void ResourceOpened() => Owner?.ResourceOpened(Id);

    internal void ResourceReleased() => Owner?.ResourceReleased(Id);

    internal void ReleaseFailed(string what) => Owner?.ReportReleaseFailure(Id, what);

    internal void ClassCFailure(Exception ex) => Owner?.ReportClassC(Id, ex.GetType().Name);

    /// <summary>Observing → Save, atomically (OBS-10), reporting the epoch readings of the window (OBS-04a).</summary>
    internal MutationLease HandOffToSave(out ObservationClose close)
    {
        close = default;
        return (Owner ?? throw new LeaseViolationException("Refused before any I/O: a default lease cannot be handed off.")).HandOffToSave(this, out close);
    }

    public void Dispose() => Owner?.EndObservation(this);
}

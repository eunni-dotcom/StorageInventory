namespace StorageInventory.Core.Identity;

/// <summary>Which kind of location a source is (§7.1, §9.4). The stable codes are the schema's (<c>source.kind</c>,
/// <c>snapshot.capture_kind</c>); never reorder or renumber them.</summary>
internal enum SourceKind
{
    /// <summary>A directory on a local volume, identified by the volume plus the root's exact path inside it.</summary>
    LocalVolume = 1,

    /// <summary>A share served over a network (a UNC path, or a mapped drive letter that resolves to one). Identified by
    /// its location only; StorageInventory never resolves names (INV-05).</summary>
    Network = 2,
}

/// <summary>
/// What became of one identity item of a reading (§7.2). The four states are kept apart so that a later gate can say WHY
/// confidence was reduced or saving is unavailable, and so that nothing is ever invented when a native call fails.
/// The default value is <see cref="Unavailable"/>: an item nobody filled in is never mistaken for a value.
/// </summary>
internal enum EvidenceStatus
{
    /// <summary>Not obtained, and no call failed for it: it was not attempted because something it needs is missing (for
    /// example the directory handle could not be opened), or the source reported no usable value.</summary>
    Unavailable = 0,

    /// <summary>The value was obtained.</summary>
    Available = 1,

    /// <summary>The native call was made and Windows reported an error (kept in <see cref="EvidenceItem{T}.Win32Error"/>).</summary>
    CallFailed = 2,

    /// <summary>The call was made and the source's filesystem or provider answered that it does not implement the query
    /// (for example <c>FileIdInfo</c> on a UDF volume answers "invalid parameter"): the source type does not provide this
    /// item. The Win32 error is kept too.</summary>
    NotProvided = 3,
}

/// <summary>
/// One identity item of a reading: a value, or the reason there is none. Immutable. Two items are never compared as a
/// whole; code compares <see cref="Status"/> and <see cref="Value"/> explicitly, because the diagnostic fields
/// (<see cref="Call"/>, <see cref="Win32Error"/>, <see cref="Detail"/>) legitimately differ between readings.
/// </summary>
/// <typeparam name="T">The value's type.</typeparam>
internal readonly record struct EvidenceItem<T>
{
    public EvidenceStatus Status { get; init; }

    /// <summary>The value; meaningful only when <see cref="Status"/> is <see cref="EvidenceStatus.Available"/>.</summary>
    public T? Value { get; init; }

    /// <summary>The native call (or step) that produced the value or failed to.</summary>
    public string? Call { get; init; }

    /// <summary>The Win32 error code of a failed call; 0 when there was none.</summary>
    public int Win32Error { get; init; }

    /// <summary>Why the item is unavailable, in developer-facing English (never shown as the product's own wording).</summary>
    public string? Detail { get; init; }

    public bool IsAvailable => Status == EvidenceStatus.Available;

    public static EvidenceItem<T> Of(T value, string call) =>
        new() { Status = EvidenceStatus.Available, Value = value, Call = call };

    /// <summary>Not obtained and no call failed for it (see <see cref="EvidenceStatus.Unavailable"/>).</summary>
    public static EvidenceItem<T> Missing(string call, string detail) =>
        new() { Status = EvidenceStatus.Unavailable, Call = call, Detail = detail };

    /// <summary>The call failed with <paramref name="win32Error"/>, or, when that error means "this provider does not
    /// implement the query", the source type does not provide the item.</summary>
    public static EvidenceItem<T> Failed(string call, int win32Error) => new()
    {
        Status = NativeErrors.MeansQueryUnsupported(win32Error) ? EvidenceStatus.NotProvided : EvidenceStatus.CallFailed,
        Call = call,
        Win32Error = win32Error,
    };

    public override string ToString() => Status switch
    {
        EvidenceStatus.Available => $"{Value}",
        EvidenceStatus.Unavailable => $"unavailable ({Call}: {Detail})",
        EvidenceStatus.NotProvided => $"not provided by the source ({Call}, Win32 error {Win32Error})",
        _ => $"call failed ({Call}, Win32 error {Win32Error})",
    };
}

/// <summary>Classifies Win32 errors from the identity queries.</summary>
internal static class NativeErrors
{
    /// <summary>
    /// True for the errors a filesystem or provider returns when it does not implement a query, as opposed to a query that
    /// went wrong: <c>ERROR_INVALID_FUNCTION</c> (1), <c>ERROR_NOT_SUPPORTED</c> (50), <c>ERROR_INVALID_PARAMETER</c> (87;
    /// what a UDF volume answers to <c>FileIdInfo</c>, measured in C3), <c>ERROR_CALL_NOT_IMPLEMENTED</c> (120) and
    /// <c>ERROR_INVALID_LEVEL</c> (124). The raw error is always kept beside the classification, and no consumer treats
    /// "not provided" differently from "failed" when deciding confidence or eligibility; it only changes the explanation.
    /// </summary>
    internal static bool MeansQueryUnsupported(int win32Error) => win32Error is 1 or 50 or 87 or 120 or 124;
}

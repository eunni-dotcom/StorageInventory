namespace StorageInventory.Core.Identity;

/// <summary>The two readings taken when the observation window closes (P4, ID-10).</summary>
/// <param name="E2">Through the HELD handle: is the object the window began with still reachable, on the same volume, at
/// the same canonical position?</param>
/// <param name="E3">Through a FRESH open of the enumerated path: does the path the scanner used still lead to that object?</param>
internal readonly record struct WindowEndReadings(VolumeEvidence E2, VolumeEvidence E3);

/// <summary>E0, the preflight reading (P0): through a handle opened on the enumerated path and closed again.</summary>
internal static class PreflightEvidence
{
    /// <summary>Opens <paramref name="enumeratedPath"/>, reads the identity items, and closes the handle. Nothing is held:
    /// the observation window has not opened yet.</summary>
    public static VolumeEvidence Collect(IVolumeEvidenceSource source, string enumeratedPath)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var handle = source.Open(enumeratedPath);
        return handle.Read(EvidenceStage.E0Preflight);
    }
}

/// <summary>
/// The source-root handle that is HELD for a whole observation window (§7.2, ID-10), and the readings taken through it.
/// </summary>
/// <remarks>
/// <para>The lifecycle, in the order a capture uses it (C5 composes these calls; C3 only provides them):</para>
/// <list type="number">
/// <item><description><see cref="Open"/> at P2, before enumeration starts: opens the ENUMERATED path with a zero-access
/// handle, reads <see cref="E1"/> through it and keeps the handle open.</description></item>
/// <item><description><see cref="Finish"/> at P4, after the scan has returned: reads E2 through the same held handle, then
/// opens the enumerated path afresh and reads E3 through that second handle (closed at once), then closes the held
/// handle.</description></item>
/// <item><description><see cref="Dispose"/> always, in <c>finally</c>: a cancelled or failed scan never calls
/// <see cref="Finish"/>, and still releases the handle. It is idempotent.</description></item>
/// </list>
/// <para><b>Both opens use the enumerated path.</b> Never the canonical path: a letter re-pointed with SUBST or a mapped
/// drive leaves the canonical path of the old target unchanged, so a fresh open of it would see no change at all. The
/// held handle follows the object (a renamed or deleted source folder reports its new canonical name), so E2 notices
/// that, and the fresh open notices a re-pointed letter or a vanished path.</para>
/// <para><b>Ownership.</b> One owner; the type does not make concurrent use safe, only its state changes (a lock keeps
/// <see cref="Finish"/> and <see cref="Dispose"/> from racing each other into a double close). The handle is closed
/// deterministically by <see cref="Finish"/> or <see cref="Dispose"/>; there is no finalizer, because the underlying
/// <see cref="Microsoft.Win32.SafeHandles.SafeFileHandle"/> already is the safety net.</para>
/// <para><b>Effects.</b> The handle is opened with desired access 0 and sharing read, write and delete, so it never blocks
/// a rename or delete of the folder, a listing, or a scan. It pins the volume while it is held, which is what blocks "safely
/// remove" for the length of a scan (Q-19).</para>
/// </remarks>
internal sealed class RootIdentityHold : IDisposable
{
    private readonly object _gate = new();
    private readonly IVolumeEvidenceSource _source;
    private IEvidenceHandle? _held;
    private bool _finished;

    private RootIdentityHold(IVolumeEvidenceSource source, string enumeratedPath, IEvidenceHandle held, VolumeEvidence e1)
    {
        _source = source;
        EnumeratedPath = enumeratedPath;
        _held = held;
        E1 = e1;
    }

    /// <summary>The path the scanner enumerates, which every open uses.</summary>
    public string EnumeratedPath { get; }

    /// <summary>The reading taken when the window opened.</summary>
    public VolumeEvidence E1 { get; }

    /// <summary>True from <see cref="Open"/> until the handle is closed by <see cref="Finish"/> or <see cref="Dispose"/>.</summary>
    public bool IsHeld
    {
        get
        {
            lock (_gate) return _held is not null;
        }
    }

    /// <summary>
    /// Opens <paramref name="enumeratedPath"/>, reads E1 through the new handle and holds it. If the path cannot be opened
    /// the hold is still returned: E1 (and later E2 and E3) then say why, and the capture is not eligible. If reading
    /// throws (for example a class C exception), the handle is closed before the exception leaves.
    /// </summary>
    public static RootIdentityHold Open(IVolumeEvidenceSource source, string enumeratedPath)
    {
        ArgumentNullException.ThrowIfNull(source);
        IEvidenceHandle? handle = source.Open(enumeratedPath);
        try
        {
            var e1 = handle.Read(EvidenceStage.E1WindowStart);
            var hold = new RootIdentityHold(source, enumeratedPath, handle, e1);
            handle = null;   // ownership moved to the hold
            return hold;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    /// <summary>
    /// P4: E2 through the held handle, E3 through a fresh open of the enumerated path, then the held handle is closed (also
    /// when a read throws). May be called once.
    /// </summary>
    /// <exception cref="InvalidOperationException">The handle is no longer held (already finished or disposed).</exception>
    public WindowEndReadings Finish()
    {
        IEvidenceHandle held;
        lock (_gate)
        {
            if (_held is null) throw new InvalidOperationException(_finished ? "The window readings were already taken." : "The held handle was already closed.");
            held = _held;
            _finished = true;
        }

        try
        {
            var e2 = held.Read(EvidenceStage.E2WindowEndHeld);
            VolumeEvidence e3;
            using (var fresh = _source.Open(EnumeratedPath))
            {
                e3 = fresh.Read(EvidenceStage.E3WindowEndFresh);
            }
            return new WindowEndReadings(e2, e3);
        }
        finally
        {
            Close();
        }
    }

    /// <summary>Closes the held handle, if it is still open. Safe to call more than once, and after <see cref="Finish"/>.</summary>
    public void Dispose() => Close();

    private void Close()
    {
        IEvidenceHandle? held;
        lock (_gate)
        {
            held = _held;
            _held = null;
        }
        held?.Dispose();
    }
}

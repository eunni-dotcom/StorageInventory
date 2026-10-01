namespace StorageInventory.Core.Identity;

/// <summary>
/// Opens a handle on a source root and reads the identity items through it (§7.2). The one real implementation is
/// <see cref="WindowsEvidenceSource"/>; tests supply a fake that records every open and read, so the call sequence of
/// ID-10 (E1 and E2 through one held handle, E3 through a fresh open, always of the enumerated path) can be proved
/// without a filesystem.
/// </summary>
internal interface IVolumeEvidenceSource
{
    /// <summary>
    /// Opens a zero-access directory handle on exactly <paramref name="enumeratedPath"/>: the path the scanner enumerates,
    /// never a canonical or otherwise derived path. Expected failures (a missing folder, a removed drive) are not
    /// exceptions: the returned handle reports them in every reading it produces. The caller owns the result.
    /// </summary>
    IEvidenceHandle Open(string enumeratedPath);
}

/// <summary>An opened (or failed-to-open) source root. Every <see cref="Read"/> goes through THIS handle, so reading E2
/// through the held handle observes the object the window began with, wherever the path leads now.</summary>
internal interface IEvidenceHandle : IDisposable
{
    /// <summary>The path this handle was opened on (the enumerated path, as given to <see cref="IVolumeEvidenceSource.Open"/>).</summary>
    string OpenedPath { get; }

    /// <summary>Reads every item through this handle. Never throws for a Win32 failure: a failed or impossible read becomes
    /// an item that says so. After <c>Dispose</c> it throws <see cref="ObjectDisposedException"/> (a programming error).</summary>
    VolumeEvidence Read(EvidenceStage stage);
}

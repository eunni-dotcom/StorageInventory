using StorageInventory.Core.Identity;

namespace StorageInventory.History.Identity;

/// <summary>The rows a decision resolved to once applied: the volume (local sources only) and the source.</summary>
internal sealed record AppliedIdentity(VolumeCandidate? Volume, SourceCandidate Source);

/// <summary>
/// An in-memory <see cref="IIdentityCandidates"/> for C3: enough of the Library's identity rows to exercise §7.5, and
/// nothing more. There is no database and no persistence here; the shape follows §9.2's <c>volume</c> and <c>source</c>
/// tables (ids, the unique key of a local source on its volume, the unique key of a network source) and freezes nothing
/// beyond them.
/// </summary>
/// <remarks>
/// <see cref="Apply"/> does what T-IMPORT's identity step will do for a decision: create the rows the decision names, or
/// attach to the existing ones after re-reading them (a chosen row that no longer exists fails, as <c>SourceChanged</c>
/// does). The only change it ever makes to an existing row is the volume's latest label and capacity, which are
/// information. A source's confidence and basis, and a volume's, are fixed when the row is created and are never changed
/// (ID-12): there is no API here that could.
/// </remarks>
internal sealed class InMemoryIdentityStore : IIdentityCandidates
{
    private readonly List<VolumeCandidate> _volumes = [];
    private readonly List<SourceCandidate> _sources = [];
    private long _nextVolumeId = 1;
    private long _nextSourceId = 1;

    public IReadOnlyList<VolumeCandidate> Volumes => _volumes;

    public IReadOnlyList<SourceCandidate> Sources => _sources;

    /// <summary>Adds a volume row. A serial of 0 is not an identity and is refused.</summary>
    public VolumeCandidate AddVolume(string fsType, ulong? serial64, uint? serial32, IdentityConfidence confidence, string? label = null, long? capacityBytes = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(fsType);
        if (serial64 == 0 || serial32 == 0) throw new ArgumentException("A zero serial is not an identity and is never stored.");
        var volume = new VolumeCandidate(_nextVolumeId++, fsType, serial64, serial32, confidence, label, capacityBytes);
        _volumes.Add(volume);
        return volume;
    }

    /// <summary>Adds a local source on an existing volume; the (volume, exact root) pair is unique.</summary>
    public SourceCandidate AddLocalSource(long volumeId, string rootInVolume, IdentityConfidence confidence, IdentityBasis basis)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootInVolume);
        if (GetVolume(volumeId) is null) throw new InvalidOperationException($"There is no volume {volumeId}.");
        if (_sources.Any(s => s.Kind == SourceKind.LocalVolume && s.VolumeId == volumeId && string.Equals(s.RootInVolume, rootInVolume, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"Volume {volumeId} already has a source with the root '{rootInVolume}'.");
        }
        var source = new SourceCandidate(_nextSourceId++, SourceKind.LocalVolume, volumeId, null, rootInVolume, confidence, basis);
        _sources.Add(source);
        return source;
    }

    /// <summary>Adds a network source; the (network root key, exact root) pair is unique.</summary>
    public SourceCandidate AddNetworkSource(string networkRootKey, string rootInVolume, IdentityConfidence confidence, IdentityBasis basis)
    {
        ArgumentException.ThrowIfNullOrEmpty(networkRootKey);
        ArgumentException.ThrowIfNullOrEmpty(rootInVolume);
        if (_sources.Any(s => s.Kind == SourceKind.Network && s.NetworkRootKey == networkRootKey && string.Equals(s.RootInVolume, rootInVolume, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"The share '{networkRootKey}' already has a source with the root '{rootInVolume}'.");
        }
        var source = new SourceCandidate(_nextSourceId++, SourceKind.Network, null, networkRootKey, rootInVolume, confidence, basis);
        _sources.Add(source);
        return source;
    }

    /// <summary>
    /// Applies a decision the way T-IMPORT will: returns the volume and source it resolved to, or null for
    /// <see cref="IdentityDecision.DoNotSave"/>. Chosen rows are re-read first.
    /// </summary>
    /// <exception cref="InvalidOperationException">A row the decision chose no longer exists, or the new source's key is taken.</exception>
    public AppliedIdentity? Apply(IdentityDecision decision, IdentityAssessment capture)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(capture);
        switch (decision)
        {
            case IdentityDecision.DoNotSave:
                return null;

            case IdentityDecision.AttachToSource attach:
            {
                var source = _sources.SingleOrDefault(s => s.SourceId == attach.Source.SourceId)
                    ?? throw new InvalidOperationException($"Source {attach.Source.SourceId} no longer exists.");
                var volume = source.VolumeId is { } volumeId ? RefreshVolumeInformation(volumeId, capture) : null;
                return new AppliedIdentity(volume, source);
            }

            case IdentityDecision.CreateSource create when create.Kind == SourceKind.Network:
                return new AppliedIdentity(null, AddNetworkSource(create.NetworkRootKey!, create.RootInVolume, create.Confidence, create.Basis));

            case IdentityDecision.CreateSource create:
            {
                var volume = create.ExistingVolumeId is { } existing
                    ? RefreshVolumeInformation(existing, capture)
                    : AddVolume(capture.FileSystemName ?? string.Empty, capture.UsableSerial64, capture.UsableSerial32, create.Confidence,
                        capture.VolumeLabel, capture.CapacityBytes);
                return new AppliedIdentity(volume, AddLocalSource(volume.VolumeId, create.RootInVolume, create.Confidence, create.Basis));
            }

            default:
                throw new ArgumentException("Unknown decision.", nameof(decision));
        }
    }

    /// <summary>Records the latest label and capacity (information, never identity). Nothing else about the row changes.</summary>
    private VolumeCandidate RefreshVolumeInformation(long volumeId, IdentityAssessment capture)
    {
        var index = _volumes.FindIndex(v => v.VolumeId == volumeId);
        if (index < 0) throw new InvalidOperationException($"Volume {volumeId} no longer exists.");
        var volume = _volumes[index];
        var refreshed = volume with { Label = capture.VolumeLabel ?? volume.Label, CapacityBytes = capture.CapacityBytes ?? volume.CapacityBytes };
        _volumes[index] = refreshed;
        return refreshed;
    }

    public IReadOnlyList<VolumeCandidate> FindVolumesBySerial64(string fsType, ulong serial64) =>
        _volumes.Where(v => FileSystemNames.Same(v.FsType, fsType) && v.Serial64 == serial64).ToList();

    public IReadOnlyList<VolumeCandidate> FindVolumesBySerial32(string fsType, uint serial32) =>
        _volumes.Where(v => FileSystemNames.Same(v.FsType, fsType) && v.Serial32 == serial32).ToList();

    public VolumeCandidate? GetVolume(long volumeId) => _volumes.SingleOrDefault(v => v.VolumeId == volumeId);

    public IReadOnlyList<SourceCandidate> SourcesOnVolume(long volumeId) =>
        _sources.Where(s => s.Kind == SourceKind.LocalVolume && s.VolumeId == volumeId).ToList();

    public IReadOnlyList<SourceCandidate> LocalSources() => _sources.Where(s => s.Kind == SourceKind.LocalVolume).ToList();

    public IReadOnlyList<SourceCandidate> NetworkSourcesWithKey(string networkRootKey) =>
        _sources.Where(s => s.Kind == SourceKind.Network && s.NetworkRootKey == networkRootKey).ToList();
}

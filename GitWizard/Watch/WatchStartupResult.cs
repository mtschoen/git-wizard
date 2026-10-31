using GitWizard.Discovery;

namespace GitWizard.Watch;

/// <summary>
/// Owns an already-armed volume change source and the immutable initial inventory for watch mode.
/// </summary>
public sealed class WatchStartupResult : IAsyncDisposable
{
    IVolumeChangeSource? _source;
    int _disposed;
    readonly (WatchPath Root, string Volume)[] _rootVolumes = [];

    public DiscoveryInventory Inventory { get; }
    public VolumeArmResult ArmResult { get; }
    public IVolumeChangeSource? Source => Volatile.Read(ref _source);
    public IReadOnlyList<string> WatchVolumes { get; }
    public IReadOnlyList<string> SearchRoots { get; }
    public IReadOnlyList<string> IgnoredRoots { get; }
    public bool IncludeTempDirectories { get; }

    /// <summary>
    /// Change batches the source buffered while discovery armed and reconciled membership.
    /// The watch service delivers them through its normal ordered event path before live
    /// batches, so changes made during catch-up are not lost.
    /// </summary>
    public IReadOnlyList<VolumeChangeBatch> StartupBatches { get; }

    public WatchStartupResult(
        DiscoveryInventory inventory,
        VolumeArmResult armResult,
        IVolumeChangeSource? source,
        IReadOnlyList<string> watchVolumes,
        DiscoveryScope scope,
        IReadOnlyList<VolumeChangeBatch>? startupBatches = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        ArmResult = armResult ?? throw new ArgumentNullException(nameof(armResult));
        _source = source;
        WatchVolumes = Array.AsReadOnly<string>([.. watchVolumes]);
        var effectiveScope = scope;
        if (source is IndexVolumeChangeSource { Session: { } session })
        {
            effectiveScope = ResolvedDiscoveryScope.Create(session.IndexSet, scope);
            var rootVolumes = new List<(WatchPath Root, string Volume)>();
            foreach (var index in session.Indices)
            {
                if (index.Status.Block is null || !session.TryGetVolume(index.Status.Root, out var volume))
                {
                    continue;
                }

                var configured = index.Status.Root.TryGetVolume(out var configuredVolume)
                    ? $"{configuredVolume}:\\"
                    : index.Status.Root.ToString();
                rootVolumes.Add((WatchPath.Parse(configured), volume.ToString()));
                rootVolumes.Add((WatchPath.Parse(index.Root().Path), volume.ToString()));
            }
            _rootVolumes = rootVolumes.ToArray();
        }
        SearchRoots = effectiveScope.SearchRoots;
        IgnoredRoots = effectiveScope.IgnoredRoots;
        IncludeTempDirectories = effectiveScope.Request.Configuration.IncludeTempDirectories ?? false;
        StartupBatches = startupBatches ?? [];
    }

    internal string? GetObservedVolume(string path)
    {
        var parsed = WatchPath.Parse(path);
        return _rootVolumes.Where(mapping => parsed.IsUnderOrEqual(mapping.Root))
            .OrderByDescending(mapping => mapping.Root.Comparison.Length)
            .Select(mapping => mapping.Volume)
            .FirstOrDefault();
    }

    public IVolumeChangeSource? DetachSource() => Interlocked.Exchange(ref _source, null);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var source = DetachSource();
        if (source is not null)
        {
            await source.DisposeAsync().ConfigureAwait(false);
        }
    }
}

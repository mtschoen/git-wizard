using GitWizard.Watch;

namespace GitWizard;

/// <summary>
/// Owns an already-armed volume change source and the immutable initial inventory for watch mode.
/// </summary>
public sealed class WatchStartupResult : IAsyncDisposable
{
    IVolumeChangeSource? _source;
    int _disposed;

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
        WatchVolumes = Array.AsReadOnly(watchVolumes.ToArray());
        SearchRoots = scope.SearchRoots;
        IgnoredRoots = scope.IgnoredRoots;
        IncludeTempDirectories = scope.Request.Configuration.IncludeTempDirectories ?? false;
        StartupBatches = startupBatches ?? [];
    }

    public IVolumeChangeSource? DetachSource() => Interlocked.Exchange(ref _source, null);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        var source = DetachSource();
        if (source is not null)
            await source.DisposeAsync().ConfigureAwait(false);
    }
}

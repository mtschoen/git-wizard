using MFTLib.Index;

namespace GitWizard.Watch;

public sealed partial class IndexVolumeChangeSource
{
    Task StartWatch(MftIndexSession owner, CancellationToken cancellationToken)
    {
        var indices = owner.Indices.Where(index => index.Status is
        { Producer: ProducerKind.Mft, State: RootState.Ready, Watch.IsSupported: true }).ToArray();
        foreach (var index in indices)
        {
            index.Changed += OnChanged;
            index.WatchFaulted += OnWatchFaulted;
            var root = index.Status.Root;
            void ObserveState(WatchStateChange change) => ObserveRootWatchState(owner, root, change);
            _watchStateHandlers[index] = ObserveState;
            index.WatchStateChanged += ObserveState;
        }

        _watchedIndices = indices;
        SeedWatchStates(indices);
        // Starting means the library pumps were scheduled, not that catch-up has drained.
        return StartWatchAndSignalAsync(indices, cancellationToken);
    }

    async Task StartWatchAndSignalAsync(IReadOnlyList<FileIndex> indices, CancellationToken cancellationToken)
    {
        var results = await Task.WhenAll(indices.Select(index => index.StartWatchingAsync(cancellationToken)))
            .ConfigureAwait(false);
        foreach (var result in results.Where(result => result.Outcome == RootOperationOutcome.Failed))
        {
            RecordStartupDriveFailure((Session ?? throw new InvalidOperationException("No index session available."))
                .VolumeFor(result.Root).Letter, result.Failure?.Message ?? "The drive watch could not start.");
        }

        CompleteWatchOpening();
        _pumpStarted.TrySetResult();
    }
}

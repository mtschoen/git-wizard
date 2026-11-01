using MFTLib.Index;

namespace GitWizard.Watch;

public sealed partial class IndexVolumeChangeSource
{
    readonly Dictionary<RootKey, long> _rootWatchVersions = [];
    bool _watchOpeningComplete;

    void ObserveRootWatchState(MftIndexSession owner, RootKey root, WatchStateChange change)
    {
        lock (_watchStateLock)
        {
            if (_watchStatesStopped || (_rootWatchVersions.TryGetValue(root, out var version) && change.Watch.StateVersion <= version))
            {
                return;
            }
            _rootWatchVersions[root] = change.Watch.StateVersion;
            if (!owner.TryGetVolume(root, out _))
            {
                owner.ObserveVolume(root, change.Volume);
                AddUsableDrive(change.Volume.Letter);
                PublishVolumeState(VolumeWatchStatus.CatchingUp(change.Volume.ToString(), OriginOf(owner.IndexFor(root)?.Status)));
            }
            if (_watchedIndices.Length == 1)
            {
                ApplyWatchState(change);
                return;
            }
            AcceptWatchState(change.Volume.Letter, AggregateWatch(owner, change.Volume, change.Watch),
                change.Fault, recordTransition: true);
        }
    }

    RootWatchStatus AggregateWatch(MftIndexSession owner, VolumeKey volume, RootWatchStatus current)
    {
        var states = _watchedIndices.Where(index => !owner.TryGetVolume(index.Status.Root, out var observed) || observed == volume)
            .Select(index => index.Status.Watch.State).ToArray();
        var state = WatchState.CatchingUp;
        if (states.Contains(WatchState.Faulted))
        {
            state = WatchState.Faulted;
        }
        else if (states.Contains(WatchState.Recovering))
        {
            state = WatchState.Recovering;
        }
        else if (_watchOpeningComplete && states.All(item => item == WatchState.CaughtUp))
        {
            state = WatchState.CaughtUp;
        }
        return current with { State = state, StateVersion = _watchStateVersions.GetValueOrDefault(volume.Letter) + 1 };
    }

    void CompleteWatchOpening()
    {
        lock (_watchStateLock)
        {
            _watchOpeningComplete = true;
            if (_watchedIndices.Length <= 1 || Session is not { } owner)
            {
                return;
            }
            foreach (var index in _watchedIndices.DistinctBy(index => owner.VolumeFor(index.Status.Root)))
            {
                var volume = owner.VolumeFor(index.Status.Root);
                AcceptWatchState(volume.Letter, AggregateWatch(owner, volume, index.Status.Watch), null, recordTransition: false);
            }
        }
    }
}

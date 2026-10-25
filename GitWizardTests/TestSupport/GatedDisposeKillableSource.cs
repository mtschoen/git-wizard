using GitWizard.Watch;

namespace GitWizardTests.UI;

// Like ScriptedVolumeChangeSource, but its DisposeAsync parks until ReleaseDispose() is called - giving
// a test a deterministic barrier inside the respawn path's dispose-before-re-check window.
internal sealed class GatedDisposeKillableSource : IVolumeChangeSource
{
    readonly TaskCompletionSource _releaseDispose = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Lock _gate = new();
    Action<string>? _sourceDied;
    string? _deathReason;

    public event Action<string>? SourceDied
    {
        add
        {
            lock (_gate)
            {
                _sourceDied += value;
                if (_deathReason is not null)
                {
                    value?.Invoke(_deathReason);
                }
            }
        }
        remove
        {
            lock (_gate)
            {
                _sourceDied -= value;
            }
        }
    }

    public bool IsArmed { get; private set; }
    public bool DisposeStarted { get; private set; }

    public Task<VolumeArmResult> ArmAndCatchUpAsync(
        IReadOnlyCollection<string> volumes, CancellationToken ct)
    {
        IsArmed = true;
        return Task.FromResult(new VolumeArmResult(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), []));
    }

    public async IAsyncEnumerable<VolumeChangeBatch> WatchAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        yield break;
    }

    public void KillSource(string reason)
    {
        lock (_gate)
        {
            _deathReason = reason;
            _sourceDied?.Invoke(reason);
        }
    }

    public void ReleaseDispose() => _releaseDispose.TrySetResult();

    public async ValueTask DisposeAsync()
    {
        DisposeStarted = true;
        await _releaseDispose.Task.ConfigureAwait(false);
    }
}

using GitWizard.Watch;

namespace GitWizardTests;

internal sealed class BlockingArmVolumeChangeSource : IVolumeChangeSource
{
    readonly TaskCompletionSource _armStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    event Action<string>? IVolumeChangeSource.SourceDied
    {
        add { }
        remove { }
    }

    public Task ArmStarted => _armStarted.Task;

    public async Task<VolumeArmResult> ArmAndCatchUpAsync(
        IReadOnlyCollection<string> volumes, CancellationToken ct)
    {
        _armStarted.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        return new VolumeArmResult(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), []);
    }

    public async IAsyncEnumerable<VolumeChangeBatch> WatchAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        yield break;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

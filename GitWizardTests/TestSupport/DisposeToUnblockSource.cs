using GitWizard.Watch;

namespace GitWizardTests.UI;

// A source whose WatchAsync deliberately ignores its CancellationToken and only unblocks when the
// source itself is disposed - used to prove StopAsync's force-dispose path can stop a source that
// never honours cancellation.
sealed class DisposeToUnblockSource : IVolumeChangeSource
{
    readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsArmed { get; private set; }
    public int DisposeCount { get; private set; }

    // Never dies on its own; a plain field-like event that is never invoked trips CS0067.
    event Action<string>? IVolumeChangeSource.SourceDied
    {
        add { }
        remove { }
    }

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
        await _disposed.Task.ConfigureAwait(false);
        yield break;
    }

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        _disposed.TrySetResult();
        return ValueTask.CompletedTask;
    }
}

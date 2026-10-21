using GitWizard.Watch;

namespace GitWizardTests;

internal sealed class ThrowingWatchVolumeChangeSource : IVolumeChangeSource
{
    readonly VolumeChangeBatch _firstBatch;
    readonly string _faultMessage;
    public event Action<string>? SourceDied;

    public ThrowingWatchVolumeChangeSource(VolumeChangeBatch firstBatch, string faultMessage)
    {
        _firstBatch = firstBatch;
        _faultMessage = faultMessage;
    }

    public Task<VolumeArmResult> ArmAndCatchUpAsync(IReadOnlyCollection<string> volumes, CancellationToken ct) =>
        Task.FromResult(new VolumeArmResult(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), []));

    public async IAsyncEnumerable<VolumeChangeBatch> WatchAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        yield return _firstBatch;
        await Task.Delay(50, ct);
        throw new InvalidOperationException(_faultMessage);
    }

    public void RaiseSourceDied(string reason) => SourceDied?.Invoke(reason);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

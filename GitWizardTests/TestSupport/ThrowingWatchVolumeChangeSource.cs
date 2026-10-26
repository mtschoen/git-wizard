using GitWizard.Watch;

namespace GitWizardTests;

sealed class ThrowingWatchVolumeChangeSource(VolumeChangeBatch firstBatch, string faultMessage) : IVolumeChangeSource
{
    public event Action<string>? SourceDied;

    public Task<VolumeArmResult> ArmAndCatchUpAsync(IReadOnlyCollection<string> volumes, CancellationToken ct) =>
        Task.FromResult(new VolumeArmResult(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), []));

    public async IAsyncEnumerable<VolumeChangeBatch> WatchAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        yield return firstBatch;
        await Task.Delay(50, ct);
        throw new InvalidOperationException(faultMessage);
    }

    public void RaiseSourceDied(string reason) => SourceDied?.Invoke(reason);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

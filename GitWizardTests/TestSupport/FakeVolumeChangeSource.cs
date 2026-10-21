using GitWizard.Watch;

namespace GitWizardTests.Watch;

// A scripted IVolumeChangeSource used across the phase-1/phase-2 tests.
internal sealed class FakeVolumeChangeSource : IVolumeChangeSource
{
    readonly IReadOnlyList<VolumeChangeBatch> _batches;
    readonly IReadOnlyDictionary<string, string> _errors;
    public event Action<string>? SourceDied;

    public FakeVolumeChangeSource(
        IReadOnlyList<VolumeChangeBatch> batches,
        IReadOnlyDictionary<string, string>? errors = null)
    {
        _batches = batches;
        _errors = errors ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public Task<VolumeArmResult> ArmAndCatchUpAsync(
        IReadOnlyCollection<string> volumes, CancellationToken ct) =>
        Task.FromResult(new VolumeArmResult(_errors, []));

    public async IAsyncEnumerable<VolumeChangeBatch> WatchAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var batch in _batches)
        {
            ct.ThrowIfCancellationRequested();
            yield return batch;
            await Task.Yield();
        }
    }

    public void KillSource(string reason) => SourceDied?.Invoke(reason);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

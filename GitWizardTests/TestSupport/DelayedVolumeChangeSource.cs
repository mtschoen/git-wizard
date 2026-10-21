using System.Runtime.CompilerServices;
using GitWizard.Watch;

namespace GitWizardTests.Watch;

// A scripted IVolumeChangeSource that spaces its batches apart in real time, used only to
// exercise RepositoryWatchService's debounce-window flush (FakeVolumeChangeSource yields its
// batches back-to-back, which never lets a debounce window elapse mid-stream).
internal sealed class DelayedVolumeChangeSource : IVolumeChangeSource
{
    readonly IReadOnlyList<VolumeChangeBatch> _batches;
    readonly TimeSpan _delayBetweenBatches;

    // Explicit, empty-bodied implementation: this double never dies, so it never raises the
    // event, but a plain field-like `event` declaration that's never invoked triggers CS0067.
    event Action<string>? IVolumeChangeSource.SourceDied
    {
        add { }
        remove { }
    }

    public DelayedVolumeChangeSource(IReadOnlyList<VolumeChangeBatch> batches, TimeSpan delayBetweenBatches)
    {
        _batches = batches;
        _delayBetweenBatches = delayBetweenBatches;
    }

    public Task<VolumeArmResult> ArmAndCatchUpAsync(
        IReadOnlyCollection<string> volumes, CancellationToken ct) =>
        Task.FromResult(new VolumeArmResult(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), []));

    public async IAsyncEnumerable<VolumeChangeBatch> WatchAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var batch in _batches)
        {
            await Task.Delay(_delayBetweenBatches, ct);
            yield return batch;
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

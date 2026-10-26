using System.Runtime.CompilerServices;
using GitWizard.Watch;

namespace GitWizardTests.Watch;

// A scripted IVolumeChangeSource that spaces its batches apart in real time, used only to
// exercise RepositoryWatchService's debounce-window flush (ScriptedVolumeChangeSource yields its
// batches back-to-back, which never lets a debounce window elapse mid-stream).
sealed class DelayedVolumeChangeSource(IReadOnlyList<VolumeChangeBatch> batches, TimeSpan delayBetweenBatches)
    : IVolumeChangeSource
{
    // Explicit, empty-bodied implementation: this double never dies, so it never raises the
    // event, but a plain field-like `event` declaration that's never invoked triggers CS0067.
    event Action<string>? IVolumeChangeSource.SourceDied
    {
        add { }
        remove { }
    }

    public Task<VolumeArmResult> ArmAndCatchUpAsync(
        IReadOnlyCollection<string> volumes, CancellationToken ct) =>
        Task.FromResult(new VolumeArmResult(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), []));

    public async IAsyncEnumerable<VolumeChangeBatch> WatchAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var batch in batches)
        {
            await Task.Delay(delayBetweenBatches, ct);
            yield return batch;
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

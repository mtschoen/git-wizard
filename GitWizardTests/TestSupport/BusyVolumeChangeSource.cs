using System.Runtime.CompilerServices;
using GitWizard.Watch;

namespace GitWizardTests.Watch;

// Emits one relevant change followed by continuous unrelated drive activity. A global
// idle-based debounce never flushes this stream; a fixed relevant-change window does.
internal sealed class BusyVolumeChangeSource : IVolumeChangeSource
{
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
        yield return new VolumeChangeBatch("C",
            new[] { new VolumeChangeEntry(@"C:\repo\a\test.txt", VolumeEntryKind.Created) });

        for (var index = 0; index < 1000; index++)
        {
            await Task.Delay(5, ct);
            yield return new VolumeChangeBatch("C",
                new[] { new VolumeChangeEntry(@"C:\unrelated\noise.tmp", VolumeEntryKind.Modified) });
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

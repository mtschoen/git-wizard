using System.Runtime.CompilerServices;
using GitWizard.Watch;

namespace GitWizardTests.Watch;

// An IVolumeChangeSource whose WatchAsync never resolves on its own - only cancellation (via
// RepositoryWatchService reacting to KillSource) can unblock it. Used to exercise the
// source-death path deterministically: since no batch is ever classified, there is no pending
// change whose flush-vs-drop outcome could race the cancellation, unlike scripting a real
// batch through FakeVolumeChangeSource and killing the source immediately after.
internal sealed class HangingVolumeChangeSource : IVolumeChangeSource
{
    public event Action<string>? SourceDied;

    public Task<VolumeArmResult> ArmAndCatchUpAsync(
        IReadOnlyCollection<string> volumes, CancellationToken ct) =>
        Task.FromResult(new VolumeArmResult(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), []));

    public async IAsyncEnumerable<VolumeChangeBatch> WatchAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        yield break;
    }

    public void KillSource(string reason) => SourceDied?.Invoke(reason);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

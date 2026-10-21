using GitWizard.Watch;

namespace GitWizardTests;

// An IVolumeChangeSource whose ArmAndCatchUpAsync throws InvalidOperationException, standing in
// for MftIndexSession opening's UAC-decline / launch-failure throw that
// IndexVolumeChangeSource surfaces from the first WatchAsync-arming MoveNextAsync.
internal sealed class ThrowingArmVolumeChangeSource : IVolumeChangeSource
{
    readonly string _message;
    public event Action<string>? SourceDied;

    public ThrowingArmVolumeChangeSource(string message) => _message = message;

    public Task<VolumeArmResult> ArmAndCatchUpAsync(IReadOnlyCollection<string> volumes, CancellationToken ct) =>
        throw new InvalidOperationException(_message);

    public async IAsyncEnumerable<VolumeChangeBatch> WatchAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        yield break;
    }

    public void RaiseSourceDied(string reason) => SourceDied?.Invoke(reason);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

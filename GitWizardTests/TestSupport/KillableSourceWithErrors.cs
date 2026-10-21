using GitWizard.Watch;

namespace GitWizardTests;

// Like HangingKillableSource but with configurable scan errors, used
// to cover FormatLiveScanErrorSuffix's non-empty-errors branch on the death/stop path.
internal sealed class KillableSourceWithErrors : IVolumeChangeSource
{
    readonly IReadOnlyDictionary<string, string> _errors;

    public event Action<string>? SourceDied;

    public bool IsArmed { get; private set; }

    public KillableSourceWithErrors(IReadOnlyDictionary<string, string> errors) => _errors = errors;

    public Task<VolumeArmResult> ArmAndCatchUpAsync(
        IReadOnlyCollection<string> volumes, CancellationToken ct)
    {
        IsArmed = true;
        return Task.FromResult(new VolumeArmResult(_errors, []));
    }

    public async IAsyncEnumerable<VolumeChangeBatch> WatchAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        yield break;
    }

    public void KillSource(string reason) => SourceDied?.Invoke(reason);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

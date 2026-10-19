using System.Runtime.CompilerServices;
using System.Threading.Channels;
using GitWizard.Watch;

namespace GitWizardTests.TestSupport;

internal sealed class DiagnosticVolumeChangeSource : IVolumeChangeSource, IVolumeChangeDiagnostics
{
    readonly Channel<VolumeChangeBatch> _batches = Channel.CreateUnbounded<VolumeChangeBatch>();
    readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    readonly Lock _warningLock = new();

    public event Action<string>? SourceDied;
    public event Action<string, string>? DriveFailed;
    public event Action<JournalWarning>? JournalWarningDetected;
    public Task Started => _started.Task;
    public int DiagnosticSubscribers => (DriveFailed?.GetInvocationList().Length ?? 0)
        + (JournalWarningDetected?.GetInvocationList().Length ?? 0);
    public Dictionary<string, string> ArmErrors { get; } = [];
    public List<JournalWarning> ArmWarnings { get; } = new();
    // What the source holds now; newer than ArmWarnings once a state change landed after the arm.
    public List<JournalWarning> CurrentWarnings { get; } = new();
    public Barrier? HandoffBarrier { get; set; }

    IReadOnlyList<JournalWarning> IVolumeChangeDiagnostics.CurrentJournalWarnings
    {
        get
        {
            HandoffBarrier?.SignalAndWait(TimeSpan.FromSeconds(5));
            lock (_warningLock)
                return CurrentWarnings.ToArray();
        }
    }
    public Action? Arming { get; set; }

    public Task<VolumeArmResult> ArmAndCatchUpAsync(IReadOnlyCollection<string> volumes, CancellationToken ct)
    {
        Arming?.Invoke();
        return Task.FromResult(new VolumeArmResult(ArmErrors, ArmWarnings));
    }

    public async IAsyncEnumerable<VolumeChangeBatch> WatchAsync([EnumeratorCancellation] CancellationToken ct)
    {
        _started.TrySetResult();
        await foreach (var batch in _batches.Reader.ReadAllAsync(ct))
            yield return batch;
    }

    public void FailDrive(string drive, string message) => DriveFailed?.Invoke(drive, message);
    public void RaiseJournalWarning(JournalWarning warning)
    {
        lock (_warningLock)
        {
            CurrentWarnings.RemoveAll(w => string.Equals(w.DriveLetter, warning.DriveLetter, StringComparison.OrdinalIgnoreCase));
            CurrentWarnings.Add(warning);
            HandoffBarrier?.SignalAndWait(TimeSpan.FromSeconds(5));
            JournalWarningDetected?.Invoke(warning);
        }
    }
    public void KillSource(string message) => SourceDied?.Invoke(message);
    public void Publish(VolumeChangeBatch batch) => Assert.That(_batches.Writer.TryWrite(batch), Is.True);
    public void Complete() => _batches.Writer.TryComplete();
    public void Fail(Exception exception) => _batches.Writer.TryComplete(exception);
    public ValueTask DisposeAsync()
    {
        Complete();
        return ValueTask.CompletedTask;
    }
}

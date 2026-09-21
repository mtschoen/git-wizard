using GitWizard.Watch;
using MFTLib.Index;

namespace GitWizardTests.TestSupport;

internal sealed class FakeJournalSettingsSource : IVolumeChangeSource, IUsnJournalGrower
{
    readonly DiagnosticVolumeChangeSource _changes = new();
    public Dictionary<char, uint> VolumeSerials { get; } = new() { ['C'] = 'C', ['D'] = 'D' };
    public Dictionary<char, UsnJournalSettings> Settings { get; } = new()
    {
        ['C'] = new() { MaximumSize = 32 * 1024 * 1024, AllocationDelta = 8 * 1024 * 1024 },
        ['D'] = new() { MaximumSize = 64 * 1024 * 1024, AllocationDelta = 8 * 1024 * 1024 }
    };
    public List<JournalCheckpointLoss> CheckpointLosses { get; } =
    [
        new JournalCheckpointLoss
        {
            DriveLetter = 'C',
            DetectedDuring = JournalCheckpointLossDetection.DriveOpening,
            Cause = JournalCheckpointLossCause.CheckpointTrimmed,
            CheckpointUsn = 0,
            FirstUsn = 32L * 1024 * 1024,
            NextUsn = 130L * 1024 * 1024,
            AllocationDelta = 8 * 1024 * 1024,
            MaximumSize = 32L * 1024 * 1024,
            BytesBehind = 32L * 1024 * 1024,
            SizeThatWouldHaveRetained = 130L * 1024 * 1024
        },
        new JournalCheckpointLoss
        {
            DriveLetter = 'D',
            DetectedDuring = JournalCheckpointLossDetection.DriveOpening,
            Cause = JournalCheckpointLossCause.CheckpointTrimmed,
            CheckpointUsn = 0,
            FirstUsn = 64L * 1024 * 1024,
            NextUsn = 200L * 1024 * 1024,
            AllocationDelta = 8 * 1024 * 1024,
            MaximumSize = 64L * 1024 * 1024,
            BytesBehind = 64L * 1024 * 1024,
            SizeThatWouldHaveRetained = 200L * 1024 * 1024
        }
    ];
    public Task Started => _changes.Started;
    public TaskCompletionSource GrowEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource? GrowRelease { get; set; }
    public Exception? GrowFailure { get; set; }
    public int GrowCalls { get; private set; }
    public bool CompleteAfterArm { get; set; }

    public event Action<string>? SourceDied
    {
        add => _changes.SourceDied += value;
        remove => _changes.SourceDied -= value;
    }

    public Task<VolumeArmResult> ArmAndCatchUpAsync(IReadOnlyCollection<string> volumes, CancellationToken ct)
    {
        var warnings = CheckpointLosses
            .Select(loss => new JournalWarning
            {
                DriveLetter = loss.DriveLetter.ToString(),
                VolumeSerial = VolumeSerials[loss.DriveLetter],
                Loss = loss
            }).ToArray();
        if (CompleteAfterArm)
            _changes.Complete();
        return Task.FromResult(new VolumeArmResult(new Dictionary<string, string>(), warnings));
    }

    public IAsyncEnumerable<VolumeChangeBatch> WatchAsync(CancellationToken ct) => _changes.WatchAsync(ct);
    public UsnJournalSettings QueryUsnJournalSettings(char driveLetter) => Settings[driveLetter];

    public async Task<UsnJournalSettings> GrowUsnJournalAsync(
        char driveLetter, long maximumSize, long allocationDelta, CancellationToken cancellationToken)
    {
        GrowCalls++;
        GrowEntered.TrySetResult();
        if (GrowRelease is { } release)
            await release.Task.WaitAsync(cancellationToken);
        if (GrowFailure is { } failure)
            throw failure;
        var result = new UsnJournalSettings { MaximumSize = maximumSize + allocationDelta, AllocationDelta = allocationDelta };
        Settings[driveLetter] = result;
        return result;
    }

    public ValueTask DisposeAsync() => _changes.DisposeAsync();

    public static JournalCheckpointLoss TrimmedLoss(char driveLetter, long maximumSize, long suggestedSize,
        long allocationDelta = 8 * 1024 * 1024,
        JournalCheckpointLossDetection detectedDuring = JournalCheckpointLossDetection.DriveOpening) => new()
        {
            DriveLetter = driveLetter,
            DetectedDuring = detectedDuring,
            Cause = JournalCheckpointLossCause.CheckpointTrimmed,
            CheckpointUsn = 0,
            FirstUsn = maximumSize,
            NextUsn = suggestedSize,
            AllocationDelta = allocationDelta,
            MaximumSize = maximumSize,
            BytesBehind = maximumSize,
            SizeThatWouldHaveRetained = suggestedSize
        };

    public static JournalCheckpointLoss RecreatedLoss(char driveLetter, long maximumSize,
        long allocationDelta = 8 * 1024 * 1024,
        JournalCheckpointLossDetection detectedDuring = JournalCheckpointLossDetection.DriveOpening) => new()
        {
            DriveLetter = driveLetter,
            DetectedDuring = detectedDuring,
            Cause = JournalCheckpointLossCause.JournalRecreated,
            CheckpointUsn = 0,
            FirstUsn = 0,
            NextUsn = maximumSize,
            AllocationDelta = allocationDelta,
            MaximumSize = maximumSize
        };
}

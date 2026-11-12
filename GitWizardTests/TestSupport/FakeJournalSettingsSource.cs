using GitWizard.Watch;
using MFTLib;
using MFTLib.Index;
using MFTLibTestExtensions;

namespace GitWizardTests.TestSupport;

sealed class FakeJournalSettingsSource : IVolumeChangeSource, IVolumeChangeDiagnostics, IUsnJournalGrower
{
    public const long DefaultMaximumRecommendedSize = 8L * 1024 * 1024 * 1024;
    readonly DiagnosticVolumeChangeSource _changes = new();
    public Dictionary<char, uint> VolumeSerials { get; } = new() { ['C'] = 'C', ['D'] = 'D' };
    public Dictionary<char, UsnJournalSettings> Settings { get; } = new()
    {
        ['C'] = new UsnJournalSettings { MaximumSize = 32 * 1024 * 1024, AllocationDelta = 8 * 1024 * 1024 },
        ['D'] = new UsnJournalSettings { MaximumSize = 64 * 1024 * 1024, AllocationDelta = 8 * 1024 * 1024 }
    };
    public Dictionary<char, long> MaximumRecommendedSizes { get; } = new()
    {
        ['C'] = DefaultMaximumRecommendedSize,
        ['D'] = DefaultMaximumRecommendedSize
    };
    public List<JournalCheckpointLoss> CheckpointLosses { get; } =
    [
        SyntheticNotifications.CreateJournalCheckpointLoss('C', JournalCheckpointLossCause.CheckpointTrimmed, JournalCheckpointLossDetection.RootOpening, new UsnJournalSettings { MaximumSize = 32L * 1024 * 1024, AllocationDelta = 8 * 1024 * 1024 }) with { BytesBehind = 32L * 1024 * 1024, SizeThatWouldHaveRetained = 130L * 1024 * 1024 },
        SyntheticNotifications.CreateJournalCheckpointLoss('D', JournalCheckpointLossCause.CheckpointTrimmed, JournalCheckpointLossDetection.RootOpening, new UsnJournalSettings { MaximumSize = 64L * 1024 * 1024, AllocationDelta = 8 * 1024 * 1024 }) with { BytesBehind = 64L * 1024 * 1024, SizeThatWouldHaveRetained = 200L * 1024 * 1024 }
    ];
    public Task Started => _changes.Started;
    public TaskCompletionSource GrowEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource? GrowRelease { get; set; }
    public Exception? GrowFailure { get; set; }
    public Exception? QueryMaximumRecommendedSizeFailure { get; set; }
    public char? QueryMaximumRecommendedSizeFailureDrive { get; set; }
    public int GrowCalls { get; private set; }
    public UsnJournalSettings? RequestedGrowthTarget { get; private set; }
    public bool? LastAllowAboveMaximumRecommendedSize { get; private set; }
    public UsnJournalGrowthOutcome? ForcedGrowthOutcome { get; set; }
    public long? ReadBackMaximumSize { get; set; }
    public bool CompleteAfterArm { get; init; }
    public IReadOnlyList<JournalWarning> CurrentJournalWarnings => ((IVolumeChangeDiagnostics)_changes).CurrentJournalWarnings;

    public event Action<string>? SourceDied
    {
        add => _changes.SourceDied += value;
        remove => _changes.SourceDied -= value;
    }

    public event Action<string, string>? DriveFailed
    {
        add => _changes.DriveFailed += value;
        remove => _changes.DriveFailed -= value;
    }

    public event Action<JournalWarning>? JournalWarningDetected
    {
        add => _changes.JournalWarningDetected += value;
        remove => _changes.JournalWarningDetected -= value;
    }

    public void RaiseJournalWarning(JournalWarning warning) => _changes.RaiseJournalWarning(warning);

    public Task<VolumeArmResult> ArmAndCatchUpAsync(IReadOnlyCollection<string> volumes, CancellationToken ct)
    {
        var warnings = CheckpointLosses
            .Select(loss => new JournalWarning
            {
                DriveLetter = loss.Volume.ToString(),
                VolumeSerial = VolumeSerials[loss.Volume.Letter],
                Loss = loss
            }).ToArray();
        _changes.CurrentWarnings.Clear();
        _changes.CurrentWarnings.AddRange(warnings);
        if (CompleteAfterArm)
        {
            _changes.Complete();
        }

        return Task.FromResult(new VolumeArmResult(new Dictionary<string, string>(), warnings));
    }

    public IAsyncEnumerable<VolumeChangeBatch> WatchAsync(CancellationToken ct) => _changes.WatchAsync(ct);
    public UsnJournalSettings QueryUsnJournalSettings(VolumeKey volume) => Settings[volume.Letter];
    public long QueryMaximumRecommendedSize(VolumeKey volume)
    {
        if (QueryMaximumRecommendedSizeFailure is { } failure &&
            (QueryMaximumRecommendedSizeFailureDrive is null ||
             QueryMaximumRecommendedSizeFailureDrive == volume.Letter))
        {
            throw failure;
        }

        return MaximumRecommendedSizes.TryGetValue(volume.Letter, out var maximumRecommended)
            ? maximumRecommended
            : DefaultMaximumRecommendedSize;
    }

    public async Task<UsnJournalGrowthResult> GrowUsnJournalAsync(
        VolumeKey volume,
        UsnJournalSettings target,
        CancellationToken cancellationToken,
        bool allowAboveMaximumRecommendedSize = false)
    {
        GrowCalls++;
        RequestedGrowthTarget = target;
        LastAllowAboveMaximumRecommendedSize = allowAboveMaximumRecommendedSize;
        GrowEntered.TrySetResult();
        if (GrowRelease is { } release)
        {
            await release.Task.WaitAsync(cancellationToken);
        }

        if (GrowFailure is { } failure)
        {
            throw failure;
        }

        var maximumRecommended = QueryMaximumRecommendedSize(volume);
        var current = Settings[volume.Letter];
        var exceedsMaximumRecommended = target.MaximumSize > maximumRecommended;
        var effectiveMaximumSize = exceedsMaximumRecommended && !allowAboveMaximumRecommendedSize
            ? maximumRecommended
            : target.MaximumSize;
        if (ForcedGrowthOutcome == UsnJournalGrowthOutcome.AlreadyAtOrAboveTarget ||
            current.MaximumSize >= effectiveMaximumSize)
        {
            return new UsnJournalGrowthResult
            {
                Outcome = UsnJournalGrowthOutcome.AlreadyAtOrAboveTarget,
                RequestedMaximumSize = target.MaximumSize,
                MaximumRecommendedSize = maximumRecommended,
                JournalSettings = current
            };
        }

        if (ForcedGrowthOutcome == UsnJournalGrowthOutcome.Unchanged)
        {
            return new UsnJournalGrowthResult
            {
                Outcome = UsnJournalGrowthOutcome.Unchanged,
                RequestedMaximumSize = target.MaximumSize,
                MaximumRecommendedSize = maximumRecommended,
                JournalSettings = current
            };
        }

        var defaultAppliedMaximumSize = exceedsMaximumRecommended && !allowAboveMaximumRecommendedSize
            ? maximumRecommended
            : target.MaximumSize + target.AllocationDelta;
        var appliedMaximumSize = ReadBackMaximumSize ?? defaultAppliedMaximumSize;
        var result = new UsnJournalSettings { MaximumSize = appliedMaximumSize, AllocationDelta = target.AllocationDelta };
        Settings[volume.Letter] = result;
        var outcome = ForcedGrowthOutcome ?? (appliedMaximumSize <= current.MaximumSize
            ? UsnJournalGrowthOutcome.Unchanged
            : ClassifyGrowthOutcome(exceedsMaximumRecommended, allowAboveMaximumRecommendedSize));
        return new UsnJournalGrowthResult
        {
            Outcome = outcome,
            RequestedMaximumSize = target.MaximumSize,
            MaximumRecommendedSize = maximumRecommended,
            JournalSettings = result
        };
    }

    static UsnJournalGrowthOutcome ClassifyGrowthOutcome(
        bool exceedsMaximumRecommended,
        bool allowAboveMaximumRecommendedSize)
    {
        if (!exceedsMaximumRecommended)
        {
            return UsnJournalGrowthOutcome.GrewAsRequested;
        }

        return allowAboveMaximumRecommendedSize
            ? UsnJournalGrowthOutcome.GrewAboveMaximumRecommendedByOverride
            : UsnJournalGrowthOutcome.GrewClampedToMaximumRecommended;
    }

    public ValueTask DisposeAsync() => _changes.DisposeAsync();

    public static JournalCheckpointLoss TrimmedLoss(char driveLetter, long maximumSize, long suggestedSize,
        long allocationDelta = 8 * 1024 * 1024,
        JournalCheckpointLossDetection detectedDuring = JournalCheckpointLossDetection.RootOpening) => SyntheticNotifications.CreateJournalCheckpointLoss(driveLetter, JournalCheckpointLossCause.CheckpointTrimmed, detectedDuring, new UsnJournalSettings { MaximumSize = maximumSize, AllocationDelta = allocationDelta }) with { BytesBehind = maximumSize, SizeThatWouldHaveRetained = suggestedSize };

    public static JournalCheckpointLoss RecreatedLoss(char driveLetter, long maximumSize,
        long allocationDelta = 8 * 1024 * 1024,
        JournalCheckpointLossDetection detectedDuring = JournalCheckpointLossDetection.RootOpening) => SyntheticNotifications.CreateJournalCheckpointLoss(driveLetter, JournalCheckpointLossCause.JournalRecreated, detectedDuring, new UsnJournalSettings { MaximumSize = maximumSize, AllocationDelta = allocationDelta });
}

using GitWizard;
using GitWizard.Watch;
using GitWizardTests.TestSupport;
using MFTLib;
using MFTLib.Index;
using MFTLibTestExtensions;
using static GitWizardTests.TestSupport.AsyncCondition;

namespace GitWizardTests.Watch;

/// <summary>
/// How each <see cref="WatchFaultKind" /> reaches the source: a drive MFTLib recovers stays usable
/// and quiet, every other fault excludes its drive, and only an empty usable set is source death.
/// </summary>
[TestFixture]
public class IndexVolumeChangeSourceFaultTests
{
    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);

    [TestCase("drive")]
    [TestCase("apply")]
    public async Task RecoverableFault_KeepsTheDriveQuietlyAndReturnsItToCaughtUp(string kind)
    {
        await using var harness = await FaultHarness.StartAsync('X', 'Y');
        harness.Inject(kind, 'X');

        await WaitUntilAsync(() => harness.Scripted.Watches.Count(watch => watch.DriveLetter == 'X') == 2);
        await WaitUntilAsync(() => harness.ReadinessOf('X') == VolumeWatchState.CaughtUp &&
                                   harness.StatusOf('X').WatchCatchUp == WatchCatchUpState.CaughtUp);
        Assert.That(harness.Failures, Is.Empty);
        Assert.That(harness.Deaths, Is.Empty);
        Assert.That(harness.States('X'), Does.Contain(VolumeWatchState.CatchingUp),
            "A recovering drive reads as catching up until its recovered watch catches up.");

        Assert.That(await harness.ReadAsync(), Is.True);
        Assert.That(harness.Current.Volume, Is.EqualTo("X"));
        Assert.That(harness.Current.Entries, Is.EqualTo(new[]
        {
            new VolumeChangeEntry(@"X:\repo\.git", VolumeEntryKind.Modified)
        }), "The recovered drive's surviving repository is refreshed.");

        await harness.Scripted.WatchFor('X').PublishBatchAsync([IndexVolumeChangeSourceTests.Entry(60, 6, "after-recovery", UsnReason.FileCreate)], new UsnJournalCursor(PackedIndexFixture.JournalId, 1003));
        Assert.That(await harness.ReadAsync(), Is.True);
        Assert.That(harness.Current.Volume, Is.EqualTo("X"));
        Assert.That(harness.Current.Entries.Single().FullPath, Is.EqualTo(@"X:\repo\after-recovery"));
    }

    [Test]
    public async Task LastUsableDriveRecovering_IsNotSourceDeath()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = await FaultHarness.StartAsync(['X'],
            token => release.Task.WaitAsync(token));
        harness.Scripted.WatchFor('X').FailDrive(new IOException("X failed"));

        await WaitUntilAsync(() => harness.StatusOf('X').WatchCatchUp == WatchCatchUpState.Recovering &&
                                   harness.ReadinessOf('X') == VolumeWatchState.CatchingUp);
        Assert.That(harness.Deaths, Is.Empty);
        Assert.That(harness.Failures, Is.Empty);
        Assert.That(harness.PendingRead.IsCompleted, Is.False, "The reader stays open while the drive recovers.");

        release.SetResult();
        await WaitUntilAsync(() => harness.ReadinessOf('X') == VolumeWatchState.CaughtUp);
        await harness.Scripted.WatchFor('X').PublishBatchAsync([IndexVolumeChangeSourceTests.Entry(60, 6, "recovered", UsnReason.FileCreate)], new UsnJournalCursor(PackedIndexFixture.JournalId, 1003));
        Assert.That(await harness.ReadAsync(), Is.True);
        Assert.That(harness.Deaths, Is.Empty);
    }

    [TestCase("channel", "channel lost")]
    [TestCase("ended", "ended without being stopped")]
    public async Task ChannelFault_ExcludesTheDrive_AndTheLastExclusionIsSourceDeath(string kind, string message)
    {
        await using var harness = await FaultHarness.StartAsync('X', 'Y');
        harness.Inject(kind, 'X');

        await WaitUntilAsync(() => harness.Failures.Length == 1 &&
                                   harness.ReadinessOf('X') == VolumeWatchState.Excluded);
        Assert.That(harness.Failures.Single().Drive, Is.EqualTo("X"));
        Assert.That(harness.Failures.Single().Message, Does.Contain(message));
        Assert.That(harness.Deaths, Is.Empty);
        Assert.That(harness.Scripted.Starts.Count(start => start.DriveLetter == 'X'), Is.EqualTo(1),
            "A lost channel is not recovered automatically.");

        await harness.Scripted.WatchFor('Y').PublishBatchAsync([IndexVolumeChangeSourceTests.Entry(50, 6, "survivor", UsnReason.FileCreate)], new UsnJournalCursor(PackedIndexFixture.JournalId, 1002));
        Assert.That(await harness.ReadAsync(), Is.True);
        Assert.That(harness.Current.Volume, Is.EqualTo("Y"));

        harness.Inject(kind, 'Y');
        Assert.That(await harness.ReadAsync(), Is.False);
        Assert.That(harness.Deaths, Has.Length.EqualTo(1));
        Assert.That(harness.Deaths.Single(), Does.Contain(message));
    }

    [Test]
    public async Task FailedRecovery_ExcludesTheDrive_AndIsSourceDeathForTheLastDrive()
    {
        await using var harness = await FaultHarness.StartAsync('X');
        harness.Scripted.StartFailure = _ => new IOException("restart refused");
        harness.Scripted.WatchFor('X').FailDrive(new IOException("X failed"));

        Assert.That(await harness.ReadAsync(), Is.False);
        await WaitUntilAsync(() => harness.ReadinessOf('X') == VolumeWatchState.Excluded);
        Assert.That(harness.Failures.Single().Drive, Is.EqualTo("X"));
        Assert.That(harness.Deaths, Has.Length.EqualTo(1));
        Assert.That(harness.StatusOf('X').WatchFailureMessage, Is.Not.Null);
    }

    [Test]
    public async Task RescanRestartFault_ExcludesTheDrive()
    {
        await using var harness = await FaultHarness.StartAsync('X', 'Y');
        harness.Scripted.StartFailure = drive => drive == 'X' ? new IOException("restart refused") : null;

        await harness.Owner.Index.RescanAsync('X', CancellationToken.None);

        await WaitUntilAsync(() => harness.Failures.Length == 1 &&
                                   harness.ReadinessOf('X') == VolumeWatchState.Excluded);
        Assert.That(harness.Failures.Single().Drive, Is.EqualTo("X"));
        Assert.That(harness.Deaths, Is.Empty);
    }

    [TestCase(1, true)]
    [TestCase(FileIndex.LostCatchUpRecoveryLimit, false)]
    public async Task LostCatchUpDuringRecovery_KeepsTheDriveUntilMftLibStopsRetrying(int losses, bool recovers)
    {
        await using var harness = await FaultHarness.StartAsync('X', 'Y');
        var remaining = losses;
        harness.Files.Producer.CatchUpLoss = _ => Interlocked.Decrement(ref remaining) >= 0
            ? SyntheticCheckpointLoss.WithJournalPositions(new JournalCheckpointLoss
            {
                DriveLetter = 'X',
                DetectedDuring = JournalCheckpointLossDetection.ScanCatchUp,
                Cause = JournalCheckpointLossCause.CheckpointTrimmed,
                AllocationDelta = 64,
                MaximumSize = 1024
            }, 1000, 2000, 3000)
            : null;
        harness.Scripted.WatchFor('X').FailDrive(new IOException("X failed"));

        if (recovers)
        {
            await WaitUntilAsync(() => harness.ReadinessOf('X') == VolumeWatchState.CaughtUp &&
                                       harness.StatusOf('X').WatchCatchUp == WatchCatchUpState.CaughtUp);
            Assert.That(harness.Failures, Is.Empty);
        }
        else
        {
            await WaitUntilAsync(() => harness.Failures.Length >= 1 &&
                                       harness.ReadinessOf('X') == VolumeWatchState.Excluded);
            Assert.That(harness.Failures.Select(failure => failure.Drive), Is.All.EqualTo("X"));
            Assert.That(harness.Failures[0].Message, Does.Contain("cannot be watched"));
        }
        Assert.That(harness.StatusOf('X').ConsecutiveLostCatchUps, Is.EqualTo(recovers ? 0 : losses));
        Assert.That(harness.Deaths, Is.Empty);
    }

    [Test]
    public async Task SubscriberFault_ReachesTheReaderAsAnError()
    {
        await using var harness = await FaultHarness.StartAsync('X', 'Y');
        harness.Owner.Index.Changed += _ => throw new InvalidOperationException("subscriber failed");
        await harness.Scripted.WatchFor('X').PublishBatchAsync([IndexVolumeChangeSourceTests.Entry(50, 6, "changed", UsnReason.FileCreate)], new UsnJournalCursor(PackedIndexFixture.JournalId, 1002));

        Assert.That(await harness.ReadAsync(), Is.True);
        var exception = Assert.ThrowsAsync<InvalidOperationException>(async () => await harness.ReadAsync());
        Assert.That(exception!.Message, Is.EqualTo("subscriber failed"));
        Assert.That(harness.Deaths, Is.Empty);
        Assert.That(harness.Failures, Is.Empty);
    }

    [TestCase(WatchFaultKind.Drive, false, true)]
    [TestCase(WatchFaultKind.Apply, false, true)]
    [TestCase(WatchFaultKind.CatchUpLost, false, true)]
    [TestCase(WatchFaultKind.CatchUpLost, true, false)]
    [TestCase(WatchFaultKind.Channel, false, false)]
    [TestCase(WatchFaultKind.Recovery, false, false)]
    [TestCase(WatchFaultKind.RescanRestart, false, false)]
    public void OnlyFaultsMftLibRecoversKeepTheDrive(WatchFaultKind kind, bool recoveryStopped, bool recovering)
    {
        Exception exception = kind == WatchFaultKind.CatchUpLost
            ? new JournalCatchUpLostException(recoveryStopped, "lost")
            : new IOException("failed");

        Assert.That(IndexVolumeChangeSource.IsRecoveringFault(new WatchFault(kind, 'X', exception)),
            Is.EqualTo(recovering));
    }

    [Test]
    public async Task StopDuringRecovery_EndsTheObservation()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var harness = await FaultHarness.StartAsync(['X'],
            token => release.Task.WaitAsync(token));
        try
        {
            harness.Scripted.WatchFor('X').FailDrive(new IOException("X failed"));
            await WaitUntilAsync(() => harness.StatusOf('X').WatchCatchUp == WatchCatchUpState.Recovering);
        }
        finally
        {
            await harness.DisposeAsync().AsTask().WaitAsync(HangGuard);
        }
        Assert.That(harness.Owner.IsDisposed, Is.True);
        Assert.That(harness.Deaths, Is.Empty);
    }

    [Test]
    public async Task StartupWaitOnARecoveringDrive_FollowsItInsteadOfExcludingIt()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repo");
        var scripted = new ScriptedWatchSource();
        var owner = IndexVolumeChangeSourceTests.Owner(await files.OpenMftIndexAsync(scripted, 'X'), "X");
        await using var source = new IndexVolumeChangeSource((_, _, _) => Task.FromResult(owner));
        var failures = new List<string>();
        source.DriveFailed += (drive, _) => failures.Add(drive);

        var arming = source.ArmAndCatchUpAsync(["X"], CancellationToken.None);
        await scripted.WaitForStartAsync('X', 1, CancellationToken.None).WaitAsync(HangGuard);
        scripted.WatchFor('X').FailDrive(new IOException("X failed during catch-up"));
        await scripted.WaitForStartAsync('X', 2, CancellationToken.None).WaitAsync(HangGuard);
        await scripted.WatchFor('X').PublishCaughtUpAsync();

        var result = await arming.WaitAsync(HangGuard);
        Assert.That(result.Errors, Is.Empty);
        Assert.That(failures, Is.Empty);
        Assert.That(source.Readiness.TryGet("X", out var status), Is.True);
        Assert.That(status!.State, Is.EqualTo(VolumeWatchState.CaughtUp));
    }

    sealed class FaultHarness : IAsyncDisposable
    {
        readonly PackedIndexFixture _files;
        readonly CancellationTokenSource _cancellation = new(HangGuard);
        readonly List<VolumeReadinessSnapshot> _snapshots = [];
        IAsyncEnumerator<VolumeChangeBatch>? _reader;

        FaultHarness(PackedIndexFixture files, ScriptedWatchSource scripted, MftIndexSession owner,
            IndexVolumeChangeSource source)
        {
            _files = files;
            Scripted = scripted;
            Owner = owner;
            Source = source;
            source.SourceDied += reason => { lock (_deaths) _deaths.Add(reason); };
            source.DriveFailed += (drive, message) => { lock (_failures) _failures.Add((drive, message)); };
            source.ReadinessChanged += snapshot => { lock (_snapshots) _snapshots.Add(snapshot); };
        }

        public ScriptedWatchSource Scripted { get; }
        public PackedIndexFixture Files => _files;
        public MftIndexSession Owner { get; }
        public IndexVolumeChangeSource Source { get; }
        readonly List<string> _deaths = [];
        readonly List<(string Drive, string Message)> _failures = [];

        public string[] Deaths
        {
            get { lock (_deaths) return _deaths.ToArray(); }
        }

        public (string Drive, string Message)[] Failures
        {
            get { lock (_failures) return _failures.ToArray(); }
        }
        /// <summary>The read the harness issued at start; later reads are issued by <see cref="ReadAsync" />.</summary>
        public Task<bool> PendingRead { get; private set; } = Task.FromResult(false);
        bool _pendingReadConsumed;
        public VolumeChangeBatch Current => _reader!.Current;

        public static Task<FaultHarness> StartAsync(params char[] drives) => StartAsync(drives, null);

        /// <summary>Opens, arms and starts reading a source over a scripted MFT index.</summary>
        /// <param name="drives">The drives to open and watch.</param>
        /// <param name="beforeRescan">Awaited before every production after the open: the recoveries' rescans.</param>
        public static async Task<FaultHarness> StartAsync(char[] drives, Func<CancellationToken, Task>? beforeRescan)
        {
            var files = new PackedIndexFixture();
            files.AddMarker("repo");
            var scripted = new ScriptedWatchSource { CatchUpOnStart = true };
            var names = drives.Select(drive => drive.ToString()).ToArray();
            var index = await files.OpenMftIndexAsync(scripted, drives);
            if (beforeRescan is not null)
                files.Producer.BeforeProduceAsync = (_, token) => beforeRescan(token);
            var owner = IndexVolumeChangeSourceTests.Owner(index, names);
            var source = new IndexVolumeChangeSource((_, _, _) => Task.FromResult(owner));
            var harness = new FaultHarness(files, scripted, owner, source);
            await source.ArmAndCatchUpAsync(names, CancellationToken.None);
            harness._reader = source.WatchAsync(harness._cancellation.Token).GetAsyncEnumerator();
            harness.PendingRead = harness._reader.MoveNextAsync().AsTask();
            return harness;
        }

        public void Inject(string kind, char drive)
        {
            switch (kind)
            {
                case "drive":
                    Scripted.WatchFor(drive).FailDrive(new IOException($"{drive} failed"));
                    break;
                case "apply":
                    _ = Scripted.WatchFor(drive).QueueBatch(null!, new UsnJournalCursor(PackedIndexFixture.JournalId, 1002));
                    break;
                case "channel":
                    Scripted.WatchFor(drive).LoseChannel(new IOException($"{drive} channel lost"));
                    break;
                default:
                    Scripted.WatchFor(drive).End();
                    break;
            }
        }

        public Task<bool> ReadAsync()
        {
            // Current stays valid until the next read is issued, so each read starts here.
            if (_pendingReadConsumed)
                PendingRead = _reader!.MoveNextAsync().AsTask();
            _pendingReadConsumed = true;
            return PendingRead.WaitAsync(HangGuard);
        }

        public DriveStatus StatusOf(char drive) => Owner.Index.Drives.Single(status => status.DriveLetter == drive);

        public VolumeWatchState? ReadinessOf(char drive) =>
            Source.Readiness.TryGet(drive.ToString(), out var status) ? status.State : null;

        public VolumeWatchState[] States(char drive)
        {
            lock (_snapshots)
            {
                return _snapshots.Select(snapshot => snapshot.TryGet(drive.ToString(), out var status)
                        ? status.State
                        : (VolumeWatchState?)null)
                    .OfType<VolumeWatchState>().ToArray();
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Source.DisposeAsync();
                if (_reader is not null)
                {
                    await _cancellation.CancelAsync();
                    try
                    {
                        await PendingRead.WaitAsync(HangGuard);
                    }
                    catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException)
                    {
                        // The reader ends with the source; how it ends is each test's own assertion.
                    }
                }
            }
            finally
            {
                _cancellation.Dispose();
                _files.Dispose();
            }
        }
    }
}

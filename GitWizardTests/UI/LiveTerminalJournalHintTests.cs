using GitWizard;
using GitWizard.Watch;
using GitWizard.Watch.Linux;
using GitWizardTests.TestSupport;
using GitWizardUI.ViewModels;
using MFTLib.Index;

namespace GitWizardTests.UI;

// Regression fixture for the PR 233 review finding: a Live run that ends in source death must
// keep the drive's journal hint visible, whether the loss was found while the drive was opening
// (a sole cache-declined drive) or reported when its live watch faulted. The terminal stop used
// to clear JournalWarnings and build the header from scan errors only, so the checkpoint-loss
// explanation vanished at exactly the moment it mattered.
[TestFixture]
// Covers MainViewModel.LiveStartup.cs, MainViewModel.Journals.cs, and StatusHeaderViewModel.cs.
public class LiveTerminalJournalHintTests
{
    LocalFilesSandbox? _sandbox;
    readonly QueuedUiDispatcher _dispatcher = new();
    DiagnosticVolumeChangeSource _source = null!;
    MainViewModel _main = null!;
    Task _running = null!;

    [SetUp]
    public void SetUp()
    {
        GitWizardLog.SilentMode = true;
        _sandbox = new LocalFilesSandbox();
        _source = new DiagnosticVolumeChangeSource();
        _main = TestViewModels.Create(dispatcher: _dispatcher);
        _main.LiveCapabilityProbe = () => new FanotifyCapabilityResult(FanotifyCapabilityStatus.Available, 0);
        _main.LivePrepareWatchAsync = (request, _, _, cancellationToken) =>
            FakeLiveStartup.FromSourceArmedAsync(_source, request, cancellationToken);
        _main.LiveIsElevated = () => false;
    }

    [TearDown]
    public async Task TearDown()
    {
        await _main.DisposeAsync();
        await _running;
        await _source.DisposeAsync();
        _dispatcher.Drain();
        _sandbox?.Dispose();
        _sandbox = null;
    }

    async Task StartLiveAsync()
    {
        _running = _main.ToggleLiveAsync();
        await _source.Started.WaitAsync(TimeSpan.FromSeconds(5));
        _dispatcher.Drain();
    }

    [Test]
    public async Task SoleCacheDeclinedDriveKeepsHintVisibleAfterWatchDies()
    {
        _source.ArmErrors["C"] = "cache declined";
        var loss = FakeJournalSettingsSource.TrimmedLoss('C', 32L * 1024 * 1024, 136L * 1024 * 1024);
        _source.ArmWarnings.Add(new JournalWarning { DriveLetter = "C", VolumeSerial = 12345, Loss = loss });
        await StartLiveAsync();
        Assert.That(_main.JournalWarnings, Has.Count.EqualTo(1),
            "The arm-time hint must surface before the watch reports death.");

        // With no usable drive the real source reports death as soon as the watch starts.
        _source.KillSource("No requested drives could be watched: cache declined");
        await _running.WaitAsync(TimeSpan.FromSeconds(5));
        _dispatcher.Drain();

        Assert.That(_main.IsLive, Is.False);
        Assert.That(_main.StatusHeader.HeaderText, Does.Contain("Live watch stopped"));
        var warning = _main.JournalWarnings.Single();
        Assert.That(warning.Warning.Message, Does.Contain("Hint on C:").And.Contain("at least 136 MB"));
    }

    [Test]
    public async Task SoleDriveLiveWatchLossKeepsHintVisibleAfterWatchDies()
    {
        await StartLiveAsync();
        Assert.That(_main.JournalWarnings, Is.Empty);
        var loss = FakeJournalSettingsSource.TrimmedLoss('C', 32L * 1024 * 1024, 136L * 1024 * 1024,
            detectedDuring: JournalCheckpointLossDetection.LiveWatch);

        _source.RaiseJournalWarning(new JournalWarning { DriveLetter = "C", VolumeSerial = 12345, Loss = loss });
        _source.FailDrive("C", "watch reached a position the journal no longer holds");
        _source.KillSource("watch reached a position the journal no longer holds");
        await _running.WaitAsync(TimeSpan.FromSeconds(5));
        _dispatcher.Drain();

        Assert.That(_main.IsLive, Is.False);
        Assert.That(_main.StatusHeader.HeaderText, Does.Contain("Live watch stopped"));
        var warning = _main.JournalWarnings.Single();
        Assert.That(warning.Warning.Message, Does.Contain("Hint on C:")
            .And.Contain("Live updates for this drive stopped"));
    }

    [Test]
    public async Task LiveWatchLossOnOneDriveShowsHintWhileLiveContinues()
    {
        await StartLiveAsync();
        Assert.That(_main.JournalWarnings, Is.Empty);
        var loss = FakeJournalSettingsSource.TrimmedLoss('D', 32L * 1024 * 1024, 136L * 1024 * 1024,
            detectedDuring: JournalCheckpointLossDetection.LiveWatch);

        _source.RaiseJournalWarning(new JournalWarning { DriveLetter = "D", VolumeSerial = 12346, Loss = loss, WatchState = WatchState.Recovering });
        _source.FailDrive("D", "watch reached a position the journal no longer holds");
        _dispatcher.Drain();

        Assert.That(_main.IsLive, Is.True);
        Assert.That(_running.IsCompleted, Is.False);
        var warning = _main.JournalWarnings.Single();
        Assert.That(warning.Warning.Message, Does.Contain("Hint on D:")
            .And.Contain("The drive is being rescanned to catch up"));
        Assert.That(_main.StatusHeader.HeaderText, Does.Contain("journal hints"));
    }
}

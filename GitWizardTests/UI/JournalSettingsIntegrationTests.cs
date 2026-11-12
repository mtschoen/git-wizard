using GitWizard;
using GitWizard.CLI;
using GitWizard.Watch;
using GitWizard.Watch.Linux;
using GitWizardTests.TestSupport;
using GitWizardUI.ViewModels;
using MFTLib;
using MFTLib.Index;
using MFTLibTestExtensions;

namespace GitWizardTests.UI;

// Keeps shared Live Watch startup/teardown for journal sizing and Settings integration scenarios in one fixture.
[NonParallelizable]
public class JournalSettingsIntegrationTests
{
    LocalFilesSandbox? _sandbox;
    readonly QueuedUiDispatcher _dispatcher = new();
    FakeJournalSettingsSource _source = null!;
    MainViewModel _main = null!;
    Task _running = null!;
    SettingsViewModel _settings = null!;
    string HintText => string.Join(" ", _main.LiveWatch.JournalWarnings
        .Select(warning => warning.Warning.Message));

    [SetUp]
    public async Task SetUp()
    {
        GitWizardLog.SilentMode = true;
        _sandbox = new LocalFilesSandbox();
        _source = new FakeJournalSettingsSource();
        _main = TestViewModels.Create(dispatcher: _dispatcher);
        _main.LiveWatch.CapabilityProbe = () => new FanotifyCapabilityResult(FanotifyCapabilityStatus.Available, 0);
        _main.LiveWatch.PrepareWatchAsync = (request, _, _, cancellationToken) =>
            FakeLiveStartup.FromSourceArmedAsync(_source, request, cancellationToken);
        _main.LiveWatch.IsElevated = () => false;
        _running = _main.LiveWatch.ToggleLiveAsync();
        await _source.Started.WaitAsync(TestTimeouts.ShortHangGuard);
        _dispatcher.Drain();
        _settings = TestViewModels.CreateSettings(liveWatch: _main.LiveWatch, dispatcher: _dispatcher);
    }

    [TearDown]
    public async Task TearDown()
    {
        _source.GrowRelease?.TrySetResult();
        await _main.DisposeAsync();
        await _running;
        await _source.DisposeAsync();
        _dispatcher.Drain();
        _sandbox?.Dispose();
        _sandbox = null;
    }

    [Test]
    public void LiveShowsEachDriveHintWithSuggestedSize()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_main.LiveWatch.JournalWarnings.Select(warning => warning.DriveLetter), Is.EqualTo(["C", "D"]));
            Assert.That(HintText, Does.Contain("Hint on C:").And.Contain("Hint on D:"));
        }
        Assert.That(HintText, Does.Contain("32 MB").And.Contain("64 MB")
            .And.Contain("130 MB").And.Contain("200 MB")
            .And.Contain("recommended maximum is 8 GB")
            .And.Contain("full rescan was needed"));
    }

    [Test]
    public async Task EnlargeShowsActualBeforeAndAfterAndKeepsOtherDriveHint()
    {
        var warning = _settings.JournalWarnings[0];
        var neededSize = warning.Warning.Loss.SizeThatWouldHaveRetained!.Value;
        await _settings.EnlargeJournalAsync(warning);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_source.GrowCalls, Is.EqualTo(1));
            Assert.That(_source.RequestedGrowthTarget?.MaximumSize, Is.EqualTo(neededSize));
            Assert.That(_source.RequestedGrowthTarget?.AllocationDelta, Is.EqualTo(warning.Warning.Loss.JournalSettings.AllocationDelta));
            Assert.That(_source.LastAllowAboveMaximumRecommendedSize, Is.False);
            Assert.That(warning.StatusMessage, Is.EqualTo(
                $"Before: {32 * 1024 * 1024:N0} bytes (32 MB). " +
                $"After: {_source.Settings['C'].MaximumSize:N0} bytes (138 MB). " +
                $"Needed: {neededSize:N0} bytes (130 MB). " +
                $"Recommended maximum: {FakeJournalSettingsSource.DefaultMaximumRecommendedSize:N0} bytes (8 GB). " +
                "Enlarged to the needed size."));
            Assert.That(HintText, Does.Not.Contain("Hint on C:").And.Contain("Hint on D:"));
        }
    }

    [Test]
    public async Task EnlargeRefusesConcurrentCommands()
    {
        _source.GrowRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = _settings.EnlargeJournalAsync(_settings.JournalWarnings[0]);
        await _source.GrowEntered.Task.WaitAsync(TestTimeouts.ShortHangGuard);
        var duplicate = _settings.EnlargeJournalAsync(_settings.JournalWarnings[1]);
        try
        {
            await _settings.IgnoreJournalWarningAsync(_settings.JournalWarnings[1]);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_source.GrowCalls, Is.EqualTo(1));
                Assert.That(_settings.JournalWarnings, Has.Count.EqualTo(2));
            }
        }
        finally
        {
            _source.GrowRelease.SetResult();
            await first;
            await duplicate;
        }
    }

    [Test]
    public async Task RestartRefreshesEachDriveAndKeepsSettingsCollectionCurrent()
    {
        var oldWarning = _settings.JournalWarnings[0];
        await _main.LiveWatch.ToggleLiveAsync();
        await _running;
        await _source.DisposeAsync();
        _source = new FakeJournalSettingsSource();
        _source.CheckpointLosses.Clear();
        _source.CheckpointLosses.Add(FakeJournalSettingsSource.TrimmedLoss('D', 16L * 1024 * 1024, 48L * 1024 * 1024));
        _running = _main.LiveWatch.ToggleLiveAsync();
        await _source.Started.WaitAsync(TestTimeouts.ShortHangGuard);
        _dispatcher.Drain();
        Assert.That(_settings.JournalWarnings, Has.Count.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_settings.JournalWarnings[0].DriveLetter, Is.EqualTo("D"));
            Assert.That(_settings.JournalWarnings[0].Warning.Message, Does.Contain("48 MB"));
        }
        await _settings.EnlargeJournalAsync(oldWarning);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(oldWarning.StatusMessage, Does.Contain("re-armed"));
            Assert.That(_source.GrowCalls, Is.Zero);
        }
    }

    [Test]
    public async Task RestartWithNoWarningsClearsPriorWarningStatus()
    {
        await _main.LiveWatch.ToggleLiveAsync();
        await _running;
        await _source.DisposeAsync();
        _source = new FakeJournalSettingsSource();
        _source.CheckpointLosses.Clear();
        _running = _main.LiveWatch.ToggleLiveAsync();
        await _source.Started.WaitAsync(TestTimeouts.ShortHangGuard);
        _dispatcher.Drain();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_main.StatusHeader.HeaderText, Is.EqualTo("Live watch active"));
            Assert.That(_settings.JournalWarnings, Is.Empty);
        }
    }

    [Test]
    public async Task EnlargeReportsBrokerFailure()
    {
        _source.GrowFailure = new InvalidOperationException("broker refused");
        var warning = _settings.JournalWarnings[0];
        var previousLogger = GitWizardLog.LogMethod;
        var messages = new List<string?>();
        GitWizardLog.LogMethod = messages.Add;
        try
        {
            await _settings.EnlargeJournalAsync(warning);
        }
        finally
        {
            GitWizardLog.LogMethod = previousLogger;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(messages, Has.Some.Contains("broker refused"));
            Assert.That(warning.StatusMessage, Is.EqualTo("Failed: Could not enlarge the change journal on C:. Check the log for details."));
            Assert.That(warning.StatusMessage, Does.Not.Contain("broker refused"));
        }
    }

    [TestCase(0)]
    [TestCase(1024)]
    public async Task EnlargeRequeriesAndRefusesEqualOrLargerJournal(long additionalBytes)
    {
        var warning = _settings.JournalWarnings[0];
        var suggestedSize = warning.Warning.Loss.SizeThatWouldHaveRetained!.Value;
        _source.Settings['C'] = _source.Settings['C'] with
        {
            MaximumSize = suggestedSize + additionalBytes
        };
        await _settings.EnlargeJournalAsync(warning);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_source.GrowCalls, Is.Zero);
            Assert.That(warning.StatusMessage, Does.Not.StartWith("Failed:")
                .And.Contain("Journal was already at or above the target size."));
        }
    }

    [Test]
    public async Task IgnorePersistsVolumeSerialAndPreservesOtherSettings()
    {
        _settings.ForkPath = "/custom/fork";
        await _settings.IgnoreJournalWarningAsync(_settings.JournalWarnings[0]);
        _settings.SearchPaths.Add("/repositories");
        await _settings.SaveAsync();
        var saved = await GitWizardConfiguration.GetConfigurationAtPathAsync(GitWizardConfiguration.GetGlobalConfigurationPath());
        Assert.That(saved, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(saved!.IgnoredJournalWarnings, Is.EquivalentTo(new uint[] { 'C' }));
            Assert.That(saved.ForkPath, Is.EqualTo("/custom/fork"));
            Assert.That(HintText, Does.Not.Contain("Hint on C:").And.Contain("Hint on D:"));
        }
    }

    [Test]
    public async Task IgnoredPreferenceFollowsSerialAfterDriveLetterChanges()
    {
        await _settings.IgnoreJournalWarningAsync(_settings.JournalWarnings[0]);
        await _main.LiveWatch.ToggleLiveAsync();
        await _running;
        await _source.DisposeAsync();
        _source = new FakeJournalSettingsSource();
        _source.VolumeSerials['C'] = 999;
        _source.VolumeSerials['D'] = 'C';
        _running = _main.LiveWatch.ToggleLiveAsync();
        await _source.Started.WaitAsync(TestTimeouts.ShortHangGuard);
        _dispatcher.Drain();
        Assert.That(_settings.JournalWarnings, Has.Count.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_settings.JournalWarnings[0].DriveLetter, Is.EqualTo("C"));
            Assert.That(_settings.JournalWarnings[0].Warning.VolumeSerial, Is.EqualTo(999));
        }
    }

    [Test]
    public async Task IgnoreSaveFailureKeepsWarningVisibleAndPreferenceUnchanged()
    {
        var configurationPath = GitWizardConfiguration.GetGlobalConfigurationPath();
        File.Delete(configurationPath);
        Directory.CreateDirectory(configurationPath);
        var warning = _settings.JournalWarnings[0];
        await _settings.IgnoreJournalWarningAsync(warning);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_settings.JournalWarnings, Does.Contain(warning));
            Assert.That((await GitWizardConfiguration.GetGlobalConfigurationAsync()).IgnoredJournalWarnings, Is.Empty);
            Assert.That(warning.StatusMessage, Does.StartWith("Failed:"));
        }
    }

    [Test]
    public async Task SettingsOpenedBeforeStopCannotReportSuccessfulEnlarge()
    {
        var warning = _settings.JournalWarnings[0];
        await _main.LiveWatch.ToggleLiveAsync();
        await _running;
        await _settings.EnlargeJournalAsync(warning);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(warning.StatusMessage, Does.StartWith("Failed: Start Live"));
            Assert.That(_source.GrowCalls, Is.Zero);
        }
    }

    [Test]
    public async Task CommandButtonsUseClickHandlers()
    {
        var root = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."));
        var markup = await File.ReadAllTextAsync(Path.Combine(root, "GitWizardUI", "Views", "SettingsWindow.axaml"));
        Assert.That(markup, Does.Contain("Click=\"EnlargeJournal_Click\"").And.Contain("Click=\"IgnoreJournalWarning_Click\""));
        Assert.That(markup, Does.Not.Contain("Command=\""));
    }

    [Test]
    public async Task EnlargeButtonStaysInLayoutDisabledWithReasonWhenNoGrowAction()
    {
        var root = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."));
        var markup = await File.ReadAllTextAsync(Path.Combine(root, "GitWizardUI", "Views", "SettingsWindow.axaml"));
        Assert.That(markup, Does.Contain("IsEnabled=\"{Binding HasGrowAction}\""));
        Assert.That(markup, Does.Contain("{Binding GrowUnavailableReason}"));
        Assert.That(markup, Does.Not.Contain("IsVisible=\"{Binding HasGrowAction}\""));
    }

    [Test]
    public async Task CliPrintsOneHintPerDriveFromCheckpointLoss()
    {
        await using var source = new FakeJournalSettingsSource { CompleteAfterArm = true };
        var service = new RepositoryWatchService(source, [@"C:\repository", @"D:\repository"], []);
        var lines = new List<string>();
        await Program.RunWatchLoopAsync(service, lines.Add, _ => Assert.Fail("Unexpected exit"), CancellationToken.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(lines.Count(line => line.StartsWith("Hint on C:", StringComparison.Ordinal)), Is.EqualTo(1));
            Assert.That(lines.Count(line => line.StartsWith("Hint on D:", StringComparison.Ordinal)), Is.EqualTo(1));
            Assert.That(lines, Has.Some.Contains("130 MB"));
        }
    }

    [Test]
    public async Task RecreatedCauseShowsHintWithNoGrowAction()
    {
        await _main.LiveWatch.ToggleLiveAsync();
        await _running;
        await _source.DisposeAsync();
        _source = new FakeJournalSettingsSource();
        _source.CheckpointLosses.Clear();
        _source.CheckpointLosses.Add(FakeJournalSettingsSource.RecreatedLoss('C', 64L * 1024 * 1024));
        _running = _main.LiveWatch.ToggleLiveAsync();
        await _source.Started.WaitAsync(TestTimeouts.ShortHangGuard);
        _dispatcher.Drain();
        Assert.That(_settings.JournalWarnings, Has.Count.EqualTo(1));
        Assert.That(_settings.JournalWarnings[0].HasGrowAction, Is.False);
        await _settings.EnlargeJournalAsync(_settings.JournalWarnings[0]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_source.GrowCalls, Is.Zero);
            Assert.That(_settings.JournalWarnings[0].Warning.Message, Does.Contain("journal was recreated"));
        }
        Assert.That(_settings.JournalWarnings[0].Warning.Message, Does.Not.Contain("increase the journal size"));
    }

    [Test]
    public async Task EnlargeRecreatedCauseReportsNoSizeWouldHaveHelped()
    {
        await _main.LiveWatch.ToggleLiveAsync();
        await _running;
        await _source.DisposeAsync();
        _source = new FakeJournalSettingsSource();
        _source.CheckpointLosses.Clear();
        _source.CheckpointLosses.Add(FakeJournalSettingsSource.RecreatedLoss('C', 64L * 1024 * 1024));
        _running = _main.LiveWatch.ToggleLiveAsync();
        await _source.Started.WaitAsync(TestTimeouts.ShortHangGuard);
        _dispatcher.Drain();
        var warning = _settings.JournalWarnings[0];
        await _settings.EnlargeJournalAsync(warning);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_source.GrowCalls, Is.Zero);
            Assert.That(warning.StatusMessage, Does.Contain("no size that would have avoided the rescan"));
        }
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task EnlargeWithoutValidTargetReportsReason(bool missingSize)
    {
        await _main.LiveWatch.ToggleLiveAsync();
        await _running;
        await _source.DisposeAsync();
        _source = new FakeJournalSettingsSource();
        _source.CheckpointLosses.Clear();
        _source.CheckpointLosses.Add(SyntheticNotifications.CreateJournalCheckpointLoss('C', JournalCheckpointLossCause.CheckpointTrimmed, JournalCheckpointLossDetection.RootOpening, new UsnJournalSettings { MaximumSize = 32L * 1024 * 1024, AllocationDelta = missingSize ? 8 * 1024 * 1024 : 0 }) with { BytesBehind = 32L * 1024 * 1024, SizeThatWouldHaveRetained = missingSize ? null : 64L * 1024 * 1024 });
        _running = _main.LiveWatch.ToggleLiveAsync();
        await _source.Started.WaitAsync(TestTimeouts.ShortHangGuard);
        _dispatcher.Drain();
        var warning = _settings.JournalWarnings[0];
        Assert.That(warning.HasGrowAction, Is.False);
        await _settings.EnlargeJournalAsync(warning);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_source.GrowCalls, Is.Zero);
            Assert.That(warning.StatusMessage, Does.Contain(missingSize ? "too large to represent" : "valid growth target"));
        }
    }

    [Test]
    public async Task NoCheckpointLossShowsNoHint()
    {
        await _main.LiveWatch.ToggleLiveAsync();
        await _running;
        await _source.DisposeAsync();
        _source = new FakeJournalSettingsSource();
        _source.CheckpointLosses.Clear();
        _running = _main.LiveWatch.ToggleLiveAsync();
        await _source.Started.WaitAsync(TestTimeouts.ShortHangGuard);
        _dispatcher.Drain();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_main.LiveWatch.JournalWarnings, Is.Empty);
            Assert.That(_main.StatusHeader.HeaderText, Is.EqualTo("Live watch active"));
        }
    }

    [Test]
    public async Task SuggestedSizeAtOrBelowCurrentMaximumOffersNoGrowAction()
    {
        await _main.LiveWatch.ToggleLiveAsync();
        await _running;
        await _source.DisposeAsync();
        _source = new FakeJournalSettingsSource();
        _source.CheckpointLosses.Clear();
        _source.CheckpointLosses.Add(FakeJournalSettingsSource.TrimmedLoss('C', 128L * 1024 * 1024, 128L * 1024 * 1024));
        _running = _main.LiveWatch.ToggleLiveAsync();
        await _source.Started.WaitAsync(TestTimeouts.ShortHangGuard);
        _dispatcher.Drain();
        Assert.That(_settings.JournalWarnings, Has.Count.EqualTo(1));
        Assert.That(_settings.JournalWarnings[0].HasGrowAction, Is.False);
        await _settings.EnlargeJournalAsync(_settings.JournalWarnings[0]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_source.GrowCalls, Is.Zero);
            Assert.That(_settings.JournalWarnings[0].StatusMessage, Does.Contain("already at least as large"));
        }
    }

    [Test]
    public async Task EnlargeAboveMaximumRecommendedClampsByDefaultAndAllowsOverride()
    {
        const long eightGigabytes = 8L * 1024 * 1024 * 1024;
        const long tenGigabytes = 10L * 1024 * 1024 * 1024;
        await _main.LiveWatch.ToggleLiveAsync();
        await _running;
        await _source.DisposeAsync();
        _source = new FakeJournalSettingsSource();
        _source.CheckpointLosses.Clear();
        _source.CheckpointLosses.Add(FakeJournalSettingsSource.TrimmedLoss('C', 32L * 1024 * 1024, tenGigabytes));
        _running = _main.LiveWatch.ToggleLiveAsync();
        await _source.Started.WaitAsync(TestTimeouts.ShortHangGuard);
        _dispatcher.Drain();

        var warning = _settings.JournalWarnings.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(warning.HasGrowAction, Is.True);
            Assert.That(warning.CanGrowAboveMaximumRecommended, Is.True);
            Assert.That(warning.Warning.Message, Does.Contain("at least 10 GB")
                .And.Contain("recommended maximum is 8 GB")
                .And.Contain("Enlarge clamps to 8 GB unless overridden"));
        }

        await _settings.EnlargeJournalAsync(warning);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_source.GrowCalls, Is.EqualTo(1));
            Assert.That(_source.LastAllowAboveMaximumRecommendedSize, Is.False);
            Assert.That(_source.Settings['C'].MaximumSize, Is.EqualTo(eightGigabytes));
            Assert.That(warning.StatusMessage, Does.Contain($"After: {eightGigabytes:N0} bytes (8 GB)")
                .And.Contain($"Needed: {tenGigabytes:N0} bytes (10 GB)")
                .And.Contain($"Recommended maximum: {eightGigabytes:N0} bytes (8 GB)")
                .And.Contain("Clamped to the recommended maximum."));
            Assert.That(_settings.JournalWarnings, Does.Contain(warning));
            Assert.That(warning.HasGrowAction, Is.False);
            Assert.That(warning.CanGrowAboveMaximumRecommended, Is.True);
            Assert.That(warning.Warning.Loss.JournalSettings.MaximumSize, Is.EqualTo(32L * 1024 * 1024));
            Assert.That(warning.Warning.Message, Does.Contain("it was 32 MB when this was detected")
                .And.Contain("at least 10 GB")
                .And.Contain("recommended maximum is 8 GB")
                .And.Contain("applied 8 GB"));
        }

        await _settings.EnlargeJournalAsync(warning);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_source.GrowCalls, Is.EqualTo(1));
            Assert.That(warning.StatusMessage, Does.Not.StartWith("Failed:")
                .And.Contain($"Before: {eightGigabytes:N0} bytes (8 GB)")
                .And.Contain($"After: {eightGigabytes:N0} bytes (8 GB)")
                .And.Contain("Journal was already at or above the target size."));
        }

        await _settings.EnlargeJournalAsync(warning, allowAboveMaximumRecommendedSize: true);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_source.GrowCalls, Is.EqualTo(2));
            Assert.That(_source.LastAllowAboveMaximumRecommendedSize, Is.True);
            Assert.That(warning.StatusMessage, Does.Contain($"Before: {eightGigabytes:N0} bytes (8 GB)")
                .And.Contain($"Needed: {tenGigabytes:N0} bytes (10 GB)")
                .And.Contain($"Recommended maximum: {eightGigabytes:N0} bytes (8 GB)")
                .And.Contain("Enlarged above the recommended maximum by override."));
            Assert.That(_settings.JournalWarnings, Is.Empty);
        }
    }

    [Test]
    public async Task EnlargeWhenBrokerReportsAlreadyAtOrAboveTargetFormatsOutcome()
    {
        _source.ForcedGrowthOutcome = UsnJournalGrowthOutcome.AlreadyAtOrAboveTarget;
        var warning = _settings.JournalWarnings[0];

        await _settings.EnlargeJournalAsync(warning);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_source.GrowCalls, Is.EqualTo(1));
            Assert.That(warning.StatusMessage, Does.Contain("Before: 33,554,432 bytes (32 MB)")
                .And.Contain("After: 33,554,432 bytes (32 MB)")
                .And.Contain("Needed: 136,314,880 bytes (130 MB)")
                .And.Contain("Recommended maximum: 8,589,934,592 bytes (8 GB)")
                .And.Contain("Journal was already at or above the target size."));
        }
    }

    [Test]
    public async Task FailedRecommendedMaximumQueryLeavesCapNullAndGrowRequeries()
    {
        await _main.LiveWatch.ToggleLiveAsync();
        await _running;
        await _source.DisposeAsync();
        _source = new FakeJournalSettingsSource
        {
            QueryMaximumRecommendedSizeFailure = new IOException("volume cap unavailable"),
            QueryMaximumRecommendedSizeFailureDrive = 'C'
        };
        var previousLogger = GitWizardLog.LogMethod;
        var messages = new List<string?>();
        GitWizardLog.LogMethod = messages.Add;
        try
        {
            _running = _main.LiveWatch.ToggleLiveAsync();
            await _source.Started.WaitAsync(TestTimeouts.ShortHangGuard);
            _dispatcher.Drain();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(messages, Has.Some.Contains("volume cap unavailable"));
                Assert.That(_settings.JournalWarnings.Select(row => row.DriveLetter), Is.EqualTo(["C", "D"]));
                Assert.That(_settings.JournalWarnings[0].Warning.MaximumRecommendedSize, Is.Null);
                Assert.That(_settings.JournalWarnings[0].HasGrowAction, Is.True);
                Assert.That(_settings.JournalWarnings[0].CanGrowAboveMaximumRecommended, Is.False);
                Assert.That(_settings.JournalWarnings[0].Warning.Message, Does.Not.Contain("recommended maximum"));
                Assert.That(_settings.JournalWarnings[1].Warning.MaximumRecommendedSize, Is.EqualTo(FakeJournalSettingsSource.DefaultMaximumRecommendedSize));
            }

            messages.Clear();
            var updatedLoss = FakeJournalSettingsSource.TrimmedLoss('C', 32L * 1024 * 1024, 160L * 1024 * 1024,
                detectedDuring: JournalCheckpointLossDetection.LiveWatch);
            _source.RaiseJournalWarning(new JournalWarning
            {
                DriveLetter = "C",
                VolumeSerial = 'C',
                Loss = updatedLoss,
                WatchState = WatchState.CaughtUp
            });
            _dispatcher.Drain();

            var warning = _settings.JournalWarnings[0];
            using (Assert.EnterMultipleScope())
            {
                Assert.That(messages, Has.Some.Contains("volume cap unavailable"));
                Assert.That(_settings.JournalWarnings.Select(row => row.DriveLetter), Is.EqualTo(["C", "D"]));
                Assert.That(warning.Warning.MaximumRecommendedSize, Is.Null);
                Assert.That(warning.Warning.Message, Does.Contain("160 MB").And.Not.Contain("recommended maximum"));
                Assert.That(_settings.JournalWarnings[1].Warning.MaximumRecommendedSize, Is.EqualTo(FakeJournalSettingsSource.DefaultMaximumRecommendedSize));
            }

            messages.Clear();
            await _settings.EnlargeJournalAsync(warning);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_source.GrowCalls, Is.Zero);
                Assert.That(messages, Has.Some.Contains("volume cap unavailable"));
                Assert.That(warning.StatusMessage, Is.EqualTo("Failed: Could not enlarge the change journal on C:. Check the log for details."));
                Assert.That(warning.StatusMessage, Does.Not.Contain("volume cap unavailable"));
            }

            _source.QueryMaximumRecommendedSizeFailure = null;
            await _settings.EnlargeJournalAsync(warning);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_source.GrowCalls, Is.EqualTo(1));
                Assert.That(warning.StatusMessage, Does.Contain("Enlarged to the needed size."));
            }
        }
        finally
        {
            GitWizardLog.LogMethod = previousLogger;
        }
    }

    [Test]
    public async Task ScanWarningsRebuildAfterGrowPreservesCurrentSizeAndDoesNotResurrectEnlarge()
    {
        const long eightGigabytes = 8L * 1024 * 1024 * 1024;
        const long tenGigabytes = 10L * 1024 * 1024 * 1024;
        await _main.LiveWatch.ToggleLiveAsync();
        await _running;
        await _source.DisposeAsync();
        _source = new FakeJournalSettingsSource();
        _source.CheckpointLosses.Clear();
        var cLoss = FakeJournalSettingsSource.TrimmedLoss('C', 32L * 1024 * 1024, tenGigabytes,
            detectedDuring: JournalCheckpointLossDetection.LiveWatch);
        var dLoss = FakeJournalSettingsSource.TrimmedLoss('D', 64L * 1024 * 1024, 200L * 1024 * 1024,
            detectedDuring: JournalCheckpointLossDetection.LiveWatch);
        _source.CheckpointLosses.Add(cLoss);
        _source.CheckpointLosses.Add(dLoss);
        _running = _main.LiveWatch.ToggleLiveAsync();
        await _source.Started.WaitAsync(TestTimeouts.ShortHangGuard);
        _dispatcher.Drain();

        await _settings.EnlargeJournalAsync(_settings.JournalWarnings.Single(row => row.DriveLetter == "C"));
        await _settings.EnlargeJournalAsync(_settings.JournalWarnings.Single(row => row.DriveLetter == "D"));

        _source.RaiseJournalWarning(new JournalWarning
        {
            DriveLetter = "C",
            VolumeSerial = 'C',
            Loss = cLoss,
            WatchState = WatchState.CaughtUp
        });
        _dispatcher.Drain();

        var rebuiltC = _settings.JournalWarnings.Single(row => row.DriveLetter == "C");
        var rebuiltD = _settings.JournalWarnings.Single(row => row.DriveLetter == "D");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rebuiltC.HasGrowAction, Is.False);
            Assert.That(rebuiltC.CanGrowAboveMaximumRecommended, Is.True);
            Assert.That(rebuiltC.GrowUnavailableReason, Does.Contain("recommended maximum of 8 GB").And.Contain("needed 10 GB"));
            Assert.That(rebuiltC.Warning.Message, Does.Contain("it was 32 MB when this was detected")
                .And.Contain($"applied {ByteSizeFormatter.Format(eightGigabytes)}"));
            Assert.That(rebuiltD.HasGrowAction, Is.False);
            Assert.That(rebuiltD.CanGrowAboveMaximumRecommended, Is.False);
            Assert.That(rebuiltD.GrowUnavailableReason, Does.Contain("already at least as large"));
        }
    }

    [Test]
    public async Task EnlargeRejectsStaleRowWhenControllerScanWarningSerialChangedBeforeUiDrain()
    {
        var staleRow = _settings.JournalWarnings[0];
        _source.RaiseJournalWarning(staleRow.Warning with { VolumeSerial = 999 });

        await _settings.EnlargeJournalAsync(staleRow);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_source.GrowCalls, Is.Zero);
            Assert.That(staleRow.StatusMessage, Does.Contain("re-armed"));
        }
    }

    [TestCase(JournalCheckpointLossCause.JournalRecreated, null)]
    [TestCase(JournalCheckpointLossCause.JournalAdvanced, null)]
    [TestCase(JournalCheckpointLossCause.CheckpointTrimmed, 160L * 1024 * 1024)]
    public async Task EnlargeRejectsSupersededLossOnSameVolumeBeforeUiDrain(
        JournalCheckpointLossCause cause, long? neededSize)
    {
        var staleRow = _settings.JournalWarnings[0];
        var before = _source.Settings['C'];
        var replacement = staleRow.Warning with
        {
            Loss = staleRow.Warning.Loss with { Cause = cause, SizeThatWouldHaveRetained = neededSize }
        };
        _source.RaiseJournalWarning(replacement);
        Assert.That(_main.LiveWatch.Controller!.ScanWarnings.Single(warning => warning.DriveLetter == "C").Loss,
            Is.EqualTo(replacement.Loss));

        await _settings.EnlargeJournalAsync(staleRow);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_source.GrowCalls, Is.Zero);
            Assert.That(_source.Settings['C'], Is.EqualTo(before));
            Assert.That(staleRow.StatusMessage, Does.StartWith("Failed:").And.Contain("Review its current journal warning"));
        }
        _dispatcher.Drain();
        Assert.That(_settings.JournalWarnings.Single(row => row.DriveLetter == "C").Warning.Loss,
            Is.EqualTo(replacement.Loss));
    }

    [TestCase(JournalCheckpointLossCause.JournalRecreated, null)]
    [TestCase(JournalCheckpointLossCause.JournalAdvanced, null)]
    [TestCase(JournalCheckpointLossCause.CheckpointTrimmed, 160L * 1024 * 1024)]
    public async Task EnlargeCompletionLeavesSupersededLossOnSameVolumeUntouched(
        JournalCheckpointLossCause cause, long? neededSize)
    {
        var originalRow = _settings.JournalWarnings[0];
        _source.GrowRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var growTask = _settings.EnlargeJournalAsync(originalRow);
        await _source.GrowEntered.Task.WaitAsync(TestTimeouts.ShortHangGuard);

        var replacement = originalRow.Warning with
        {
            Loss = originalRow.Warning.Loss with { Cause = cause, SizeThatWouldHaveRetained = neededSize }
        };
        _source.RaiseJournalWarning(replacement);
        _dispatcher.Drain();
        var replacementRow = _settings.JournalWarnings.Single(row => row.DriveLetter == "C");
        var replacementWarningBeforeCompletion = replacementRow.Warning;

        _source.GrowRelease.SetResult();
        await growTask;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_settings.JournalWarnings.Select(row => (row.DriveLetter, row.Warning.Loss.Cause)),
                Is.EqualTo([("C", cause), ("D", JournalCheckpointLossCause.CheckpointTrimmed)]));
            Assert.That(_settings.JournalWarnings.Single(row => row.DriveLetter == "C"), Is.SameAs(replacementRow));
            Assert.That(replacementRow.Warning, Is.EqualTo(replacementWarningBeforeCompletion));
            Assert.That(replacementRow.Warning.LastGrowthOutcome, Is.Null);
            Assert.That(replacementRow.StatusMessage, Is.Empty);
        }
    }

    [Test]
    public async Task EnlargeWhenWindowsKeepsJournalUnchangedReportsKeptSizeAndDisablesRetry()
    {
        const long currentSize = 32L * 1024 * 1024;
        const long neededSize = 130L * 1024 * 1024;
        _source.ReadBackMaximumSize = currentSize;
        var warning = _settings.JournalWarnings[0];

        await _settings.EnlargeJournalAsync(warning);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_source.GrowCalls, Is.EqualTo(1));
            Assert.That(_source.Settings['C'].MaximumSize, Is.EqualTo(currentSize));
            Assert.That(warning.StatusMessage, Is.EqualTo(
                $"Before: {currentSize:N0} bytes (32 MB). " +
                $"After: {currentSize:N0} bytes (32 MB). " +
                $"Needed: {neededSize:N0} bytes (130 MB). " +
                $"Recommended maximum: {FakeJournalSettingsSource.DefaultMaximumRecommendedSize:N0} bytes (8 GB). " +
                $"Windows accepted the request but kept the journal at {currentSize:N0} bytes (32 MB)."));
            Assert.That(warning.StatusMessage, Does.Not.Contain("Enlarged").And.Not.Contain("applied"));
            Assert.That(_settings.JournalWarnings, Does.Contain(warning));
            Assert.That(warning.HasGrowAction, Is.False);
            Assert.That(warning.CanGrowAboveMaximumRecommended, Is.False);
            Assert.That(warning.GrowUnavailableReason, Is.EqualTo("Windows accepted the request but kept the journal at 32 MB."));
            Assert.That(warning.Warning.Message, Does.Not.Contain("applied"));
        }

        await _settings.EnlargeJournalAsync(warning);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_source.GrowCalls, Is.EqualTo(1));
            Assert.That(warning.StatusMessage, Is.EqualTo("Failed: Windows accepted the request but kept the journal at 32 MB."));
        }
    }

    [Test]
    public async Task EnlargeWhenNtfsRoundsDownAppliedMaximumDisplaysReadBackSizeAndResolvesWarning()
    {
        const long requestedBytes = 214_643_302L;
        const long readBackBytes = 214_433_792L;
        await _main.LiveWatch.ToggleLiveAsync();
        await _running;
        await _source.DisposeAsync();
        _source = new FakeJournalSettingsSource
        {
            ReadBackMaximumSize = readBackBytes
        };
        _source.CheckpointLosses.Clear();
        _source.CheckpointLosses.Add(FakeJournalSettingsSource.TrimmedLoss('C', 32L * 1024 * 1024, requestedBytes));
        _running = _main.LiveWatch.ToggleLiveAsync();
        await _source.Started.WaitAsync(TestTimeouts.ShortHangGuard);
        _dispatcher.Drain();

        var warning = _settings.JournalWarnings.Single();
        await _settings.EnlargeJournalAsync(warning);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_source.GrowCalls, Is.EqualTo(1));
            Assert.That(warning.StatusMessage, Is.EqualTo(
                $"Before: {32L * 1024 * 1024:N0} bytes (32 MB). " +
                $"After: {readBackBytes:N0} bytes ({ByteSizeFormatter.Format(readBackBytes)}). " +
                $"Needed: {requestedBytes:N0} bytes ({ByteSizeFormatter.Format(requestedBytes)}). " +
                $"Recommended maximum: {FakeJournalSettingsSource.DefaultMaximumRecommendedSize:N0} bytes (8 GB). " +
                "Enlarged to the needed size."));
            Assert.That(_settings.JournalWarnings, Is.Empty);
        }
    }
}

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

public class JournalSettingsIntegrationTests
{
    string _temporaryRoot = null!;
    readonly QueuedLiveUiDispatcher _dispatcher = new();
    FakeJournalSettingsSource _source = null!;
    MainViewModel _main = null!;
    Task _running = null!;
    SettingsViewModel _settings = null!;
    string HintText => string.Join(" ", _main.JournalWarnings
        .Select(warning => warning.Warning.Message));

    [SetUp]
    public async Task SetUp()
    {
        GitWizardLog.SilentMode = true;
        _temporaryRoot = TestUtilities.RedirectLocalFilesToTemp();
        TestUtilities.ResetStaticCaches();
        _source = new FakeJournalSettingsSource();
        _main = new MainViewModel(_dispatcher, new StubUserDialogs(), new StubClipboardService())
        {
            LiveCapabilityProbe = () => new FanotifyCapabilityResult(FanotifyCapabilityStatus.Available, 0),
            LivePrepareWatchAsync = (request, _, _, cancellationToken) =>
                FakeLiveStartup.FromSourceArmedAsync(_source, request, cancellationToken),
            LiveIsElevated = () => false
        };
        _running = _main.ToggleLiveAsync();
        await _source.Started.WaitAsync(TimeSpan.FromSeconds(5));
        _dispatcher.Drain();
        _settings = new SettingsViewModel(new StubFolderPicker(), _main, _dispatcher);
    }

    [TearDown]
    public async Task TearDown()
    {
        _source.GrowRelease?.TrySetResult();
        await _main.DisposeAsync();
        await _running;
        await _source.DisposeAsync();
        _dispatcher.Drain();
        TestUtilities.ResetStaticCaches();
        TestUtilities.ClearLocalFilesRedirect(_temporaryRoot);
    }

    [Test]
    public void LiveShowsEachDriveHintWithSuggestedSize()
    {
        Assert.That(_main.JournalWarnings.Select(warning => warning.DriveLetter), Is.EqualTo(new[] { "C", "D" }));
        Assert.That(HintText, Does.Contain("Hint on C:").And.Contain("Hint on D:"));
        Assert.That(HintText, Does.Contain("32 MB").And.Contain("64 MB")
            .And.Contain("130 MB").And.Contain("200 MB")
            .And.Contain("full rescan was needed"));
    }

    [Test]
    public async Task EnlargeShowsActualBeforeAndAfterAndKeepsOtherDriveHint()
    {
        var warning = _settings.JournalWarnings[0];
        await _settings.EnlargeJournalAsync(warning);
        Assert.That(_source.GrowCalls, Is.EqualTo(1));
        Assert.That(_source.RequestedGrowthTarget?.MaximumSize, Is.EqualTo(warning.Warning.Loss.SizeThatWouldHaveRetained));
        Assert.That(_source.RequestedGrowthTarget?.AllocationDelta, Is.EqualTo(warning.Warning.Loss.JournalSettings.AllocationDelta));
        Assert.That(warning.StatusMessage, Is.EqualTo(
            $"Before: {32 * 1024 * 1024:N0} bytes. After: {_source.Settings['C'].MaximumSize:N0} bytes."));
        Assert.That(HintText, Does.Not.Contain("Hint on C:").And.Contain("Hint on D:"));
    }

    [Test]
    public async Task EnlargeRefusesConcurrentCommands()
    {
        _source.GrowRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = _settings.EnlargeJournalAsync(_settings.JournalWarnings[0]);
        await _source.GrowEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var duplicate = _settings.EnlargeJournalAsync(_settings.JournalWarnings[1]);
        try
        {
            await _settings.IgnoreJournalWarningAsync(_settings.JournalWarnings[1]);
            Assert.That(_source.GrowCalls, Is.EqualTo(1));
            Assert.That(_settings.JournalWarnings, Has.Count.EqualTo(2));
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
        await _main.ToggleLiveAsync();
        await _running;
        await _source.DisposeAsync();
        _source = new FakeJournalSettingsSource();
        _source.CheckpointLosses.Clear();
        _source.CheckpointLosses.Add(FakeJournalSettingsSource.TrimmedLoss('D', 16L * 1024 * 1024, 48L * 1024 * 1024));
        _running = _main.ToggleLiveAsync();
        await _source.Started.WaitAsync(TimeSpan.FromSeconds(5));
        _dispatcher.Drain();
        Assert.That(_settings.JournalWarnings, Has.Count.EqualTo(1));
        Assert.That(_settings.JournalWarnings[0].DriveLetter, Is.EqualTo("D"));
        Assert.That(_settings.JournalWarnings[0].Warning.Message, Does.Contain("48 MB"));
        await _settings.EnlargeJournalAsync(oldWarning);
        Assert.That(oldWarning.StatusMessage, Does.Contain("re-armed"));
        Assert.That(_source.GrowCalls, Is.Zero);
    }

    [Test]
    public async Task RestartWithNoWarningsClearsPriorWarningStatus()
    {
        await _main.ToggleLiveAsync();
        await _running;
        await _source.DisposeAsync();
        _source = new FakeJournalSettingsSource();
        _source.CheckpointLosses.Clear();
        _running = _main.ToggleLiveAsync();
        await _source.Started.WaitAsync(TimeSpan.FromSeconds(5));
        _dispatcher.Drain();
        Assert.That(_main.HeaderText, Is.EqualTo("Live watch active"));
        Assert.That(_settings.JournalWarnings, Is.Empty);
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
        Assert.That(messages, Has.Some.Contains("broker refused"));
        Assert.That(warning.StatusMessage, Is.EqualTo("Failed: broker refused"));
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
        Assert.That(_source.GrowCalls, Is.Zero);
        Assert.That(warning.StatusMessage, Does.Contain("Refusing to shrink or resize"));
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
        Assert.That(saved!.IgnoredJournalWarnings, Is.EquivalentTo(new uint[] { 'C' }));
        Assert.That(saved.ForkPath, Is.EqualTo("/custom/fork"));
        Assert.That(HintText, Does.Not.Contain("Hint on C:").And.Contain("Hint on D:"));
    }

    [Test]
    public async Task IgnoredPreferenceFollowsSerialAfterDriveLetterChanges()
    {
        await _settings.IgnoreJournalWarningAsync(_settings.JournalWarnings[0]);
        await _main.ToggleLiveAsync();
        await _running;
        await _source.DisposeAsync();
        _source = new FakeJournalSettingsSource();
        _source.VolumeSerials['C'] = 999;
        _source.VolumeSerials['D'] = 'C';
        _running = _main.ToggleLiveAsync();
        await _source.Started.WaitAsync(TimeSpan.FromSeconds(5));
        _dispatcher.Drain();
        Assert.That(_settings.JournalWarnings, Has.Count.EqualTo(1));
        Assert.That(_settings.JournalWarnings[0].DriveLetter, Is.EqualTo("C"));
        Assert.That(_settings.JournalWarnings[0].Warning.VolumeSerial, Is.EqualTo(999));
    }

    [Test]
    public async Task IgnoreSaveFailureKeepsWarningVisibleAndPreferenceUnchanged()
    {
        var configurationPath = GitWizardConfiguration.GetGlobalConfigurationPath();
        File.Delete(configurationPath);
        Directory.CreateDirectory(configurationPath);
        var warning = _settings.JournalWarnings[0];
        await _settings.IgnoreJournalWarningAsync(warning);
        Assert.That(_settings.JournalWarnings, Does.Contain(warning));
        Assert.That((await GitWizardConfiguration.GetGlobalConfigurationAsync()).IgnoredJournalWarnings, Is.Empty);
        Assert.That(warning.StatusMessage, Does.StartWith("Failed:"));
    }

    [Test]
    public async Task SettingsOpenedBeforeStopCannotReportSuccessfulEnlarge()
    {
        var warning = _settings.JournalWarnings[0];
        await _main.ToggleLiveAsync();
        await _running;
        await _settings.EnlargeJournalAsync(warning);
        Assert.That(warning.StatusMessage, Does.StartWith("Failed: Start Live"));
        Assert.That(_source.GrowCalls, Is.Zero);
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
        Assert.That(lines.Count(line => line.StartsWith("Hint on C:", StringComparison.Ordinal)), Is.EqualTo(1));
        Assert.That(lines.Count(line => line.StartsWith("Hint on D:", StringComparison.Ordinal)), Is.EqualTo(1));
        Assert.That(lines, Has.Some.Contains("130 MB"));
    }

    [Test]
    public async Task RecreatedCauseShowsHintWithNoGrowAction()
    {
        await _main.ToggleLiveAsync();
        await _running;
        await _source.DisposeAsync();
        _source = new FakeJournalSettingsSource();
        _source.CheckpointLosses.Clear();
        _source.CheckpointLosses.Add(FakeJournalSettingsSource.RecreatedLoss('C', 64L * 1024 * 1024));
        _running = _main.ToggleLiveAsync();
        await _source.Started.WaitAsync(TimeSpan.FromSeconds(5));
        _dispatcher.Drain();
        Assert.That(_settings.JournalWarnings, Has.Count.EqualTo(1));
        Assert.That(_settings.JournalWarnings[0].HasGrowAction, Is.False);
        await _settings.EnlargeJournalAsync(_settings.JournalWarnings[0]);
        Assert.That(_source.GrowCalls, Is.Zero);
        Assert.That(_settings.JournalWarnings[0].Warning.Message, Does.Contain("journal was recreated"));
        Assert.That(_settings.JournalWarnings[0].Warning.Message, Does.Not.Contain("increase the journal size"));
    }

    [Test]
    public async Task EnlargeRecreatedCauseReportsNoSizeWouldHaveHelped()
    {
        await _main.ToggleLiveAsync();
        await _running;
        await _source.DisposeAsync();
        _source = new FakeJournalSettingsSource();
        _source.CheckpointLosses.Clear();
        _source.CheckpointLosses.Add(FakeJournalSettingsSource.RecreatedLoss('C', 64L * 1024 * 1024));
        _running = _main.ToggleLiveAsync();
        await _source.Started.WaitAsync(TimeSpan.FromSeconds(5));
        _dispatcher.Drain();
        var warning = _settings.JournalWarnings[0];
        await _settings.EnlargeJournalAsync(warning);
        Assert.That(_source.GrowCalls, Is.Zero);
        Assert.That(warning.StatusMessage, Does.Contain("no size that would have avoided the rescan"));
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task EnlargeWithoutValidTargetReportsReason(bool missingSize)
    {
        await _main.ToggleLiveAsync();
        await _running;
        await _source.DisposeAsync();
        _source = new FakeJournalSettingsSource();
        _source.CheckpointLosses.Clear();
        _source.CheckpointLosses.Add(SyntheticNotifications.CreateJournalCheckpointLoss('C', JournalCheckpointLossCause.CheckpointTrimmed, JournalCheckpointLossDetection.DriveOpening, new UsnJournalSettings { MaximumSize = 32L * 1024 * 1024, AllocationDelta = missingSize ? 8 * 1024 * 1024 : 0 }) with { BytesBehind = 32L * 1024 * 1024, SizeThatWouldHaveRetained = missingSize ? null : 64L * 1024 * 1024 });
        _running = _main.ToggleLiveAsync();
        await _source.Started.WaitAsync(TimeSpan.FromSeconds(5));
        _dispatcher.Drain();
        var warning = _settings.JournalWarnings[0];
        Assert.That(warning.HasGrowAction, Is.False);
        await _settings.EnlargeJournalAsync(warning);
        Assert.That(_source.GrowCalls, Is.Zero);
        Assert.That(warning.StatusMessage, Does.Contain(missingSize ? "too large to represent" : "valid growth target"));
    }

    [Test]
    public async Task NoCheckpointLossShowsNoHint()
    {
        await _main.ToggleLiveAsync();
        await _running;
        await _source.DisposeAsync();
        _source = new FakeJournalSettingsSource();
        _source.CheckpointLosses.Clear();
        _running = _main.ToggleLiveAsync();
        await _source.Started.WaitAsync(TimeSpan.FromSeconds(5));
        _dispatcher.Drain();
        Assert.That(_main.JournalWarnings, Is.Empty);
        Assert.That(_main.HeaderText, Is.EqualTo("Live watch active"));
    }

    [Test]
    public async Task SuggestedSizeAtOrBelowCurrentMaximumOffersNoGrowAction()
    {
        await _main.ToggleLiveAsync();
        await _running;
        await _source.DisposeAsync();
        _source = new FakeJournalSettingsSource();
        _source.CheckpointLosses.Clear();
        _source.CheckpointLosses.Add(FakeJournalSettingsSource.TrimmedLoss('C', 128L * 1024 * 1024, 128L * 1024 * 1024));
        _running = _main.ToggleLiveAsync();
        await _source.Started.WaitAsync(TimeSpan.FromSeconds(5));
        _dispatcher.Drain();
        Assert.That(_settings.JournalWarnings, Has.Count.EqualTo(1));
        Assert.That(_settings.JournalWarnings[0].HasGrowAction, Is.False);
        await _settings.EnlargeJournalAsync(_settings.JournalWarnings[0]);
        Assert.That(_source.GrowCalls, Is.Zero);
        Assert.That(_settings.JournalWarnings[0].StatusMessage, Does.Contain("already at least as large"));
    }
}

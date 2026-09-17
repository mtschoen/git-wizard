using GitWizard;
using GitWizard.CLI;
using GitWizard.Watch;
using GitWizard.Watch.Linux;
using GitWizardTests.TestSupport;
using GitWizardUI.ViewModels;
using MFTLib.Index;

namespace GitWizardTests.UI;

public class JournalSettingsIntegrationTests
{
    string _temporaryRoot = null!;
    readonly QueuedLiveUiDispatcher _dispatcher = new();
    FakeJournalSettingsSource _source = null!;
    MainViewModel _main = null!;
    Task _running = null!;
    SettingsViewModel _settings = null!;
    string WarningText => string.Join(" ", _main.JournalWarnings.Where(warning => warning.IsBelowRecommended)
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
            LiveVolumeChangeSourceFactory = () => _source,
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
    public void LiveShowsEachDriveSizeRecommendationAndConsequence()
    {
        Assert.That(_main.JournalWarnings.Select(warning => warning.DriveLetter), Is.EqualTo(new[] { "C", "D" }));
        Assert.That(WarningText, Does.Contain("warning on C:").And.Contain("warning on D:"));
        Assert.That(WarningText, Does.Contain("32 MB").And.Contain("64 MB")
            .And.Contain($"{UsnJournalRecommendations.RecommendedMaximumSize / 1024 / 1024} MB")
            .And.Contain("faults the live watch and forces a rescan"));
    }

    [Test]
    public async Task EnlargeShowsActualBeforeAndAfterAndKeepsOtherDriveWarning()
    {
        var warning = _settings.JournalWarnings[0];
        await _settings.EnlargeJournalAsync(warning);
        Assert.That(_source.GrowCalls, Is.EqualTo(1));
        Assert.That(warning.StatusMessage, Is.EqualTo(
            $"Before: {32 * 1024 * 1024:N0} bytes. After: {_source.Settings['C'].MaximumSize:N0} bytes."));
        Assert.That(WarningText, Does.Not.Contain("warning on C:").And.Contain("warning on D:"));
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
        _source.Settings['C'] = _source.Settings['C'] with { MaximumSize = UsnJournalRecommendations.RecommendedMaximumSize };
        _source.Settings['D'] = _source.Settings['D'] with { MaximumSize = 16 * 1024 * 1024 };
        _running = _main.ToggleLiveAsync();
        await _source.Started.WaitAsync(TimeSpan.FromSeconds(5));
        _dispatcher.Drain();
        Assert.That(_settings.JournalWarnings, Has.Count.EqualTo(1));
        Assert.That(_settings.JournalWarnings[0].DriveLetter, Is.EqualTo("D"));
        Assert.That(_settings.JournalWarnings[0].CurrentSizeText, Is.EqualTo("16 MB"));
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
        _source.Settings.Clear();
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
        Assert.That(warning.CurrentSizeText, Is.EqualTo("32 MB"));
    }

    [TestCase(0)]
    [TestCase(1024)]
    public async Task EnlargeRequeriesAndRefusesEqualOrLargerJournal(long additionalBytes)
    {
        _source.Settings['C'] = _source.Settings['C'] with
        {
            MaximumSize = UsnJournalRecommendations.RecommendedMaximumSize + additionalBytes
        };
        var warning = _settings.JournalWarnings[0];
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
        Assert.That(WarningText, Does.Not.Contain("warning on C:").And.Contain("warning on D:"));
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
    public async Task CliPrintsOneWarningPerDriveFromJournalSettings()
    {
        await using var source = new FakeJournalSettingsSource { CompleteAfterArm = true };
        var service = new RepositoryWatchService(source, [@"C:\repository", @"D:\repository"], []);
        var lines = new List<string>();
        await Program.RunWatchLoopAsync(service, lines.Add, _ => Assert.Fail("Unexpected exit"), CancellationToken.None);
        Assert.That(lines.Count(line => line.StartsWith("warning on C:", StringComparison.Ordinal)), Is.EqualTo(1));
        Assert.That(lines.Count(line => line.StartsWith("warning on D:", StringComparison.Ordinal)), Is.EqualTo(1));
        Assert.That(lines, Has.Some.Contains("32 MB"));
    }
}

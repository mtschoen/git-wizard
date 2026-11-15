using System.Collections.Concurrent;
using GitWizard;
using GitWizard.Watch;
using GitWizard.Watch.Linux;
using GitWizardTests.TestSupport;
using GitWizardUI.ViewModels;

namespace GitWizardTests.UI;

// The desktop capacity warning: Live shows it beside the journal hints, Settings offers its rescan,
// and the rescan restarts Live so the watch preparation rescans the flagged drive.
[NonParallelizable]
public class CapacityWarningSettingsTests
{
    LocalFilesSandbox? _sandbox;
    readonly QueuedUiDispatcher _dispatcher = new();
    ConcurrentQueue<DiscoveryRequest> _preparations = [];
    ConcurrentQueue<DiagnosticVolumeChangeSource> _sources = [];
    MainViewModel _main = null!;
    Task _running = null!;
    SettingsViewModel _settings = null!;

    [SetUp]
    public async Task SetUp()
    {
        GitWizardLog.SilentMode = true;
        _sandbox = new LocalFilesSandbox();
        _preparations = [];
        _sources = [];
        _main = TestViewModels.Create(dispatcher: _dispatcher);
        _main.LiveWatch.CapabilityProbe = () => new FanotifyCapabilityResult(FanotifyCapabilityStatus.Available, 0);
        _main.LiveWatch.IsElevated = () => false;
        _main.LiveWatch.PrepareWatchAsync = (request, _, _, cancellationToken) =>
        {
            var source = new DiagnosticVolumeChangeSource();
            // Only the first run's block is flagged: the restart's preparation rescanned it.
            if (_preparations.IsEmpty)
            {
                source.SetCapacityWarnings(new CapacityWarning { DriveLetter = "C", Detail = "the name region is at its clamp" });
            }

            _preparations.Enqueue(request);
            _sources.Enqueue(source);
            return FakeLiveStartup.FromSourceArmedAsync(source, request, cancellationToken);
        };
        _running = _main.LiveWatch.ToggleLiveAsync();
        await WaitForStartedSourceAsync(1);
        _settings = TestViewModels.CreateSettings(liveWatch: _main.LiveWatch, dispatcher: _dispatcher);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _main.DisposeAsync();
        await _running;
        foreach (var source in _sources)
        {
            await source.DisposeAsync();
        }

        _dispatcher.Drain();
        _sandbox?.Dispose();
        _sandbox = null;
    }

    [Test]
    public void LiveExposesTheWarningAndSettingsSharesIt()
    {
        var row = _main.LiveWatch.CapacityWarnings.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(row.DriveLetter, Is.EqualTo("C"));
            Assert.That(row.Warning.Message, Does.Contain("Index on C:").And.Contain("the name region is at its clamp"));
            Assert.That(row.Warning.RescanEffect, Does.Contain("C:"));
            Assert.That(_settings.CapacityWarnings, Is.SameAs(_main.LiveWatch.CapacityWarnings));
            Assert.That(_main.LiveWatch.JournalWarnings, Is.Empty, "A capacity warning is not a journal hint.");
            Assert.That(_main.StatusHeader.HeaderText, Does.Contain("rescan offered"));
        }
    }

    [Test]
    public async Task RescanRestartsLiveThroughTheWatchPreparationAndClearsTheWarning()
    {
        await _settings.RescanCapacityWarningAsync(_settings.CapacityWarnings.Single());
        await WaitForStartedSourceAsync(2);

        var requests = _preparations.ToArray();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(requests, Has.Length.EqualTo(2));
            Assert.That(requests[1].Purpose, Is.EqualTo(DiscoveryPurpose.Watch),
                "A watch preparation rescans every drive whose block is flagged.");
            Assert.That(_main.LiveWatch.IsLive, Is.True);
            Assert.That(_main.LiveWatch.CapacityWarnings, Is.Empty);
        }
    }

    [Test]
    public void MidRunClearRemovesTheRowWithoutARestart()
    {
        Assert.That(_main.LiveWatch.CapacityWarnings, Has.Count.EqualTo(1));
        _sources.Last().SetCapacityWarnings();
        _dispatcher.Drain();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_main.LiveWatch.CapacityWarnings, Is.Empty);
            Assert.That(_preparations, Has.Count.EqualTo(1));
        }
    }

    async Task WaitForStartedSourceAsync(int count)
    {
        await AsyncCondition.WaitUntilAsync(() =>
        {
            _dispatcher.Drain();
            return _sources.Count == count && _sources.Last().Started.IsCompleted && _main.LiveWatch.IsLive;
        });
        _dispatcher.Drain();
    }
}

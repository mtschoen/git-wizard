using GitWizard;
using GitWizard.Watch;
using GitWizardTests.TestSupport;
using GitWizardUI.ViewModels;

namespace GitWizardTests.UI;

// Live watch tests are split by concern because the combined fixture exceeds 500 lines.
// Covers MainViewModel.LiveProgress.cs.
public class MainViewModelLiveProgressTests
{
    [SetUp]
    public void SetUp() => GitWizardLog.SilentMode = true;

    static MainViewModel NewViewModel()
        => new(new StubUiDispatcher(), new StubUserDialogs(), new StubClipboardService());

    static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, timeout.Token);
        }
    }


    [Test]
    public async Task ToggleLiveAsync_ScanProgress_ReportedWhileLive_ShowsThenClearsOnStop()
    {
        var viewModel = NewViewModel();
        var source = new GatedProgressVolumeChangeSource();
        viewModel.LivePrepareWatchAsync = (request, _, _, cancellationToken) =>
            FakeLiveStartup.FromSourceArmedAsync(source, request, cancellationToken);
        viewModel.LiveIsElevated = () => false;

        var startTask = viewModel.ToggleLiveAsync();
        await source.ArmStarted.WaitAsync(TimeSpan.FromSeconds(5));

        // Arming runs inside the startup prepare, before the service subscribes scan progress,
        // so the starting state shows without progress.
        Assert.Multiple(() =>
        {
            Assert.That(viewModel.IsLiveStarting, Is.True);
            Assert.That(viewModel.IsLiveScanProgressVisible, Is.False);
        });

        source.ReleaseArm();
        await WaitUntilAsync(() => viewModel.IsLive);

        source.ReportScanProgress(new VolumeScanProgress("D", 2, 5, 1_200_000, 2_400_000));
        await WaitUntilAsync(() => viewModel.IsLiveScanProgressVisible);

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.LiveScanProgressText,
                Is.EqualTo("Scanning D: (2 of 5), 1.2M of 2.4M records"));
            Assert.That(viewModel.LiveScanProgressValue, Is.EqualTo(0.3).Within(0.001));
            Assert.That(viewModel.IsLiveScanProgressIndeterminate, Is.False);
        });

        await viewModel.ToggleLiveAsync();
        await startTask;

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.IsLiveScanProgressVisible, Is.False);
            Assert.That(viewModel.LiveScanProgressText, Is.Empty);
        });
    }

    [Test]
    public async Task ToggleLiveAsync_ArmFailureDuringStartupPrepare_EndsNotLiveWithStatus()
    {
        var viewModel = NewViewModel();
        var source = new GatedProgressVolumeChangeSource(new InvalidOperationException("scan blew up"));
        viewModel.LivePrepareWatchAsync = (request, _, _, cancellationToken) =>
            FakeLiveStartup.FromSourceArmedAsync(source, request, cancellationToken);
        viewModel.LiveIsElevated = () => false;

        var startTask = viewModel.ToggleLiveAsync();
        await source.ArmStarted.WaitAsync(TimeSpan.FromSeconds(5));
        source.ReleaseArm();
        await startTask;

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.IsLive, Is.False);
            Assert.That(viewModel.IsLiveStarting, Is.False);
            Assert.That(viewModel.IsLiveScanProgressVisible, Is.False);
            Assert.That(viewModel.LiveScanProgressText, Is.Empty);
            Assert.That(viewModel.HeaderText, Does.Contain("Live watch could not start: scan blew up"));
        });
    }
}

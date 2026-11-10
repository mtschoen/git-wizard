using System.Runtime.CompilerServices;
using GitWizard;
using GitWizard.Watch;
using GitWizardTests.TestSupport;
using GitWizardUI.ViewModels;

namespace GitWizardTests.UI;

// Live watch tests are split by concern because the combined fixture exceeds 500 lines.
// Covers LiveWatchViewModel.Readiness.cs and StatusHeaderViewModel.cs.
public class MainViewModelLiveProgressTests
{
    [SetUp]
    public void SetUp() => GitWizardLog.SilentMode = true;

    static MainViewModel NewViewModel()
        => TestViewModels.Create();

    static Task WaitUntilAsync(Func<bool> condition, [CallerArgumentExpression(nameof(condition))] string? expression = null) =>
        AsyncCondition.WaitUntilAsync(condition, TestTimeouts.ShortHangGuard, expression);


    [Test]
    public async Task ToggleLiveAsync_ScanProgress_ReportedWhileLive_ShowsThenClearsOnStop()
    {
        var viewModel = NewViewModel();
        var armGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new ScriptedVolumeChangeSource
        {
            ArmGate = armGate.Task,
            KeepWatching = true
        };
        viewModel.LiveWatch.PrepareWatchAsync = (request, _, _, cancellationToken) =>
            FakeLiveStartup.FromSourceArmedAsync(source, request, cancellationToken);
        viewModel.LiveWatch.IsElevated = () => false;

        var startTask = viewModel.LiveWatch.ToggleLiveAsync();
        await source.ArmStarted.WaitAsync(TestTimeouts.ShortHangGuard);

        // Arming runs inside the startup prepare, before the service subscribes scan progress,
        // so the starting state shows without progress.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(viewModel.LiveWatch.IsLiveStarting, Is.True);
            Assert.That(viewModel.LiveWatch.IsLiveScanProgressVisible, Is.False);
        }

        armGate.SetResult();
        await WaitUntilAsync(() => viewModel.LiveWatch.IsLive);

        source.ReportScanProgress(new VolumeScanProgress("D", 2, 5, 1_200_000, 2_400_000));
        await WaitUntilAsync(() => viewModel.LiveWatch.IsLiveScanProgressVisible);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(viewModel.LiveWatch.LiveScanProgressText,
                Is.EqualTo("Scanning D: (2 of 5), 1.2M of 2.4M records"));
            Assert.That(viewModel.LiveWatch.LiveScanProgressValue, Is.EqualTo(0.3).Within(0.001));
            Assert.That(viewModel.LiveWatch.IsLiveScanProgressIndeterminate, Is.False);
        }

        await viewModel.LiveWatch.ToggleLiveAsync();
        await startTask;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(viewModel.LiveWatch.IsLiveScanProgressVisible, Is.False);
            Assert.That(viewModel.LiveWatch.LiveScanProgressText, Is.Empty);
        }
    }

    [Test]
    public async Task ToggleLiveAsync_ArmFailureDuringStartupPrepare_EndsNotLiveWithStatus()
    {
        var viewModel = NewViewModel();
        var armGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new ScriptedVolumeChangeSource
        {
            ArmGate = armGate.Task,
            ArmFailure = new InvalidOperationException("scan blew up"),
            KeepWatching = true
        };
        viewModel.LiveWatch.PrepareWatchAsync = (request, _, _, cancellationToken) =>
            FakeLiveStartup.FromSourceArmedAsync(source, request, cancellationToken);
        viewModel.LiveWatch.IsElevated = () => false;

        var startTask = viewModel.LiveWatch.ToggleLiveAsync();
        await source.ArmStarted.WaitAsync(TestTimeouts.ShortHangGuard);
        armGate.SetResult();
        await startTask;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(viewModel.LiveWatch.IsLive, Is.False);
            Assert.That(viewModel.LiveWatch.IsLiveStarting, Is.False);
            Assert.That(viewModel.LiveWatch.IsLiveScanProgressVisible, Is.False);
            Assert.That(viewModel.LiveWatch.LiveScanProgressText, Is.Empty);
            Assert.That(viewModel.StatusHeader.HeaderText, Does.Contain("Live watch could not start: scan blew up"));
        }
    }

    [TestCase(WatchStartupPhase.Opening, null, "Opening index...")]
    [TestCase(WatchStartupPhase.RequestingElevation, null, "Requesting elevation...")]
    [TestCase(WatchStartupPhase.BrokerConnected, null, "Broker connected")]
    [TestCase(WatchStartupPhase.Scanning, "C", "Scanning C:")]
    [TestCase(WatchStartupPhase.CatchingUp, null, "Catching up journal...")]
    [TestCase(WatchStartupPhase.Discovering, null, "Discovering repositories...")]
    [TestCase(WatchStartupPhase.Reconciling, null, "Reconciling repositories...")]
    public async Task ToggleLiveAsync_StartupPhase_IsShownAsIndeterminateProgress(
        WatchStartupPhase phase, string? volume, string expectedText)
    {
        var viewModel = NewViewModel();
        var armGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new ScriptedVolumeChangeSource { ArmGate = armGate.Task, KeepWatching = true };
        viewModel.LiveWatch.PrepareWatchAsync = (request, _, progress, cancellationToken) =>
        {
            progress?.Report(new WatchStartupProgress(phase, volume));
            return FakeLiveStartup.FromSourceArmedAsync(source, request, cancellationToken);
        };
        viewModel.LiveWatch.IsElevated = () => false;

        var startTask = viewModel.LiveWatch.ToggleLiveAsync();
        await source.ArmStarted.WaitAsync(TestTimeouts.ShortHangGuard);
        await WaitUntilAsync(() => viewModel.LiveWatch.IsLiveScanProgressVisible);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(viewModel.LiveWatch.LiveScanProgressText, Is.EqualTo(expectedText));
            Assert.That(viewModel.LiveWatch.IsLiveScanProgressIndeterminate, Is.True);
            Assert.That(viewModel.LiveWatch.LiveScanProgressValue, Is.Zero);
        }

        armGate.SetResult();
        await WaitUntilAsync(() => viewModel.LiveWatch.IsLive);
        await viewModel.LiveWatch.ToggleLiveAsync();
        await startTask;
    }

    [TestCase(500u, null, "Scanning D: (1 of 2), 500 records")]
    [TestCase(12_500u, null, "Scanning D: (1 of 2), 12.5k records")]
    [TestCase(2_000u, 4_000u, "Scanning D: (1 of 2), 2.0k of 4.0k records")]
    public async Task ToggleLiveAsync_ScanSample_FormatsRecordCountsAndKnownOrUnknownTotals(
        uint rowsWritten, uint? totalRows, string expectedText)
    {
        var viewModel = NewViewModel();
        var source = new ScriptedVolumeChangeSource { KeepWatching = true };
        viewModel.LiveWatch.PrepareWatchAsync = (request, _, _, cancellationToken) =>
            FakeLiveStartup.FromSourceArmedAsync(source, request, cancellationToken);
        viewModel.LiveWatch.IsElevated = () => false;
        var startTask = viewModel.LiveWatch.ToggleLiveAsync();
        await WaitUntilAsync(() => viewModel.LiveWatch.IsLive);

        source.ReportScanProgress(new VolumeScanProgress("D", 1, 2, rowsWritten, totalRows));
        await WaitUntilAsync(() => viewModel.LiveWatch.IsLiveScanProgressVisible);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(viewModel.LiveWatch.LiveScanProgressText, Is.EqualTo(expectedText));
            Assert.That(viewModel.LiveWatch.IsLiveScanProgressIndeterminate, Is.EqualTo(totalRows is null));
        }

        await viewModel.LiveWatch.ToggleLiveAsync();
        await startTask;
    }
}

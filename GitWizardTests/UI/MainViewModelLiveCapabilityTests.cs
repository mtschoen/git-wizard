using GitWizard;
using GitWizard.Watch.Linux;
using GitWizardTests.TestSupport;
using GitWizardUI.ViewModels;

namespace GitWizardTests.UI;

// Live watch tests are split by concern because the combined fixture exceeds 500 lines.
// Covers LiveWatchViewModel.cs and StatusHeaderViewModel.cs (CanToggleLive and header state).
public class MainViewModelLiveCapabilityTests
{
    [SetUp]
    public void SetUp() => GitWizardLog.SilentMode = true;

    static MainViewModel NewViewModel()
        => TestViewModels.Create();


    [Test]
    public void IsLive_DefaultsFalse_AndToggleLiveIsAllowed()
    {
        var viewModel = NewViewModel();

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.LiveWatch.IsLive, Is.False);
            Assert.That(viewModel.LiveWatch.CanToggleLive, Is.True);
        });
    }

    [Test]
    public void CanToggleLive_FalseWhileRefreshing_TrueWhileLive()
    {
        var viewModel = NewViewModel();
        viewModel.LiveWatch.CapabilityProbe = () => new FanotifyCapabilityResult(FanotifyCapabilityStatus.Available, 0);
        var changes = new List<string?>();
        viewModel.LiveWatch.PropertyChanged += (_, arguments) => changes.Add(arguments.PropertyName);
        viewModel.Refresh.IsRefreshing = true;
        viewModel.Refresh.IsRefreshing = true;
        Assert.That(viewModel.LiveWatch.CanToggleLive, Is.False);
        Assert.That(changes, Is.EqualTo(new[]
        {
            nameof(LiveWatchViewModel.CanToggleLive), nameof(LiveWatchViewModel.LiveTooltip)
        }));
        changes.Clear();
        viewModel.Refresh.IsRefreshing = false;
        Assert.That(viewModel.LiveWatch.CanToggleLive, Is.True);
        Assert.That(changes, Is.EqualTo(new[]
        {
            nameof(LiveWatchViewModel.CanToggleLive), nameof(LiveWatchViewModel.LiveTooltip)
        }));
        viewModel.LiveWatch.IsLive = true;
        viewModel.Refresh.IsRefreshing = true;
        Assert.That(viewModel.LiveWatch.CanToggleLive, Is.True, "Stopping remains available during refresh.");
    }

    [Test]
    public async Task ToggleLiveAsync_WhileRefreshing_IsNoOpAndDoesNotChangeHeader()
    {
        var viewModel = NewViewModel();
        viewModel.Refresh.IsRefreshing = true;
        viewModel.StatusHeader.ShowRefreshMessage("Refreshing repositories...");

        await viewModel.LiveWatch.ToggleLiveAsync();

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.LiveWatch.IsLive, Is.False);
            Assert.That(viewModel.LiveWatch.IsLiveStarting, Is.False);
            Assert.That(viewModel.StatusHeader.HeaderText, Is.EqualTo("Refreshing repositories..."));
        });
    }

    [Test]
    public async Task CanToggleLive_UnsupportedKernel_IsFalse_AndTooltipSurfacesReason()
    {
        var viewModel = NewViewModel();
        viewModel.LiveWatch.CapabilityProbe = () => new FanotifyCapabilityResult(FanotifyCapabilityStatus.UnsupportedKernel, 0);

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.LiveWatch.CanToggleLive, Is.False);
            Assert.That(viewModel.LiveWatch.LiveTooltip, Does.Contain("kernel 5.9"));
        });

        await viewModel.LiveWatch.ToggleLiveAsync();
        Assert.Multiple(() =>
        {
            Assert.That(viewModel.LiveWatch.IsLive, Is.False);
            Assert.That(viewModel.StatusHeader.HeaderText, Does.Contain("Live watch unavailable"));
            Assert.That(viewModel.StatusHeader.HeaderText, Does.Contain("kernel 5.9"));
        });
    }

    [Test]
    public async Task CanToggleLive_UnsupportedFileSystem_IsFalse_AndTooltipSurfacesReason()
    {
        var viewModel = NewViewModel();
        viewModel.LiveWatch.CapabilityProbe = () => new FanotifyCapabilityResult(FanotifyCapabilityStatus.UnsupportedFileSystem, 0);

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.LiveWatch.CanToggleLive, Is.False);
            Assert.That(viewModel.LiveWatch.LiveTooltip, Does.Contain("unsupported on this filesystem"));
        });

        await viewModel.LiveWatch.ToggleLiveAsync();
        Assert.Multiple(() =>
        {
            Assert.That(viewModel.LiveWatch.IsLive, Is.False);
            Assert.That(viewModel.StatusHeader.HeaderText, Does.Contain("Live watch unavailable"));
            Assert.That(viewModel.StatusHeader.HeaderText, Does.Contain("unsupported on this filesystem"));
        });
    }

    [Test]
    public void CanToggleLive_Available_IsTrue_AndTooltipIsDefault()
    {
        var viewModel = NewViewModel();
        viewModel.LiveWatch.CapabilityProbe = () => new FanotifyCapabilityResult(FanotifyCapabilityStatus.Available, 0);

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.LiveWatch.CanToggleLive, Is.True);
            Assert.That(viewModel.LiveWatch.LiveTooltip, Is.EqualTo("Start or stop live repository monitoring (requires elevation when starting)"));
        });
    }

    [Test]
    public void CanToggleLive_RequiresElevation_IsTrue_AndTooltipIsDefault()
    {
        var viewModel = NewViewModel();
        viewModel.LiveWatch.CapabilityProbe = () => new FanotifyCapabilityResult(FanotifyCapabilityStatus.RequiresElevation, 1);

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.LiveWatch.CanToggleLive, Is.True);
            Assert.That(viewModel.LiveWatch.LiveTooltip, Is.EqualTo("Start or stop live repository monitoring (requires elevation when starting)"));
        });
    }
}

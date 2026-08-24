using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using GitWizardUI.ViewModels;
using GitWizardUI.Views;

namespace GitWizardTests.UI;

public class MainWindowLiveTests
{
    [AvaloniaTest]
    public void LiveButton_ReflectsViewModelStateAndAvailability()
    {
        var window = new MainWindow();
        var viewModel = (MainViewModel)window.DataContext!;
        var button = window.FindControl<Button>("LiveButton");

        Assert.That(button, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(button.Content, Is.EqualTo("Live"));
            Assert.That(button.Classes.Contains("live"), Is.False);
            Assert.That(button.IsEnabled, Is.True);
        });

        viewModel.IsRefreshing = true;
        Assert.That(button.IsEnabled, Is.False);

        viewModel.IsRefreshing = false;
        viewModel.IsLiveStarting = true;
        Assert.Multiple(() =>
        {
            Assert.That(button.Content, Is.EqualTo("Starting Live..."));
            Assert.That(button.Classes.Contains("liveStarting"), Is.True);
            Assert.That(button.Classes.Contains("live"), Is.False);
            Assert.That(button.IsEnabled, Is.True);
        });

        viewModel.IsLiveStarting = false;
        viewModel.IsLive = true;
        Assert.Multiple(() =>
        {
            Assert.That(button.Content, Is.EqualTo("Live"));
            Assert.That(button.Classes.Contains("live"), Is.True);
            Assert.That(button.Classes.Contains("liveStarting"), Is.False);
            Assert.That(button.IsEnabled, Is.True);
        });
    }

    [AvaloniaTest]
    public void LiveButton_UnsupportedCapability_IsDisabledWithTooltip()
    {
        var window = new MainWindow();
        var viewModel = (MainViewModel)window.DataContext!;
        var button = window.FindControl<Button>("LiveButton");

        Assert.That(button, Is.Not.Null);

        viewModel.LiveCapabilityProbe = () => new GitWizard.Watch.Linux.FanotifyCapabilityResult(
            GitWizard.Watch.Linux.FanotifyCapabilityStatus.UnsupportedKernel, 0);

        Assert.Multiple(() =>
        {
            Assert.That(button.IsEnabled, Is.False);
            Assert.That(ToolTip.GetTip(button), Does.Contain("kernel 5.9"));
        });
    }
}

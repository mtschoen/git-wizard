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

    [AvaloniaTest]
    public void LiveScanProgress_ReflectsViewModelState()
    {
        var window = new MainWindow();
        var viewModel = (MainViewModel)window.DataContext!;
        var panel = window.FindControl<StackPanel>("LiveScanProgressPanel");
        var bar = window.FindControl<ProgressBar>("LiveScanProgressBar");
        var text = window.FindControl<TextBlock>("LiveScanProgressText");

        Assert.That(panel, Is.Not.Null);
        Assert.That(panel!.IsVisible, Is.False);

        viewModel.IsLiveScanProgressVisible = true;
        viewModel.LiveScanProgressText = "Scanning D: (2 of 5), 1.2M of 2.4M records";
        viewModel.LiveScanProgressValue = 0.3;
        viewModel.IsLiveScanProgressIndeterminate = false;

        Assert.Multiple(() =>
        {
            Assert.That(panel.IsVisible, Is.True);
            Assert.That(bar!.Value, Is.EqualTo(0.3).Within(0.001));
            Assert.That(bar.IsIndeterminate, Is.False);
            Assert.That(text!.Text, Is.EqualTo("Scanning D: (2 of 5), 1.2M of 2.4M records"));
        });

        viewModel.IsLiveScanProgressVisible = false;
        Assert.That(panel.IsVisible, Is.False);
    }
}

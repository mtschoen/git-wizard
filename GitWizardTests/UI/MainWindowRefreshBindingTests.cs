using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using GitWizardUI.ViewModels;
using GitWizardUI.Views;

namespace GitWizardTests.UI;

// Covers MainWindow.axaml and RefreshCoordinator.cs: refresh component bindings.
public class MainWindowRefreshBindingTests
{
    [AvaloniaTest]
    public async Task ScanningIndicator_FollowsRefreshIsScanning()
    {
        await using var window = new MainWindow();
        var viewModel = (MainViewModel)window.DataContext!;
        var indicator = window.FindControl<StackPanel>("ScanningIndicator")!;
        Assert.That(indicator, Is.Not.Null);
        Assert.That(indicator.IsVisible, Is.False);

        viewModel.Refresh.IsScanning = true;
        Dispatcher.UIThread.RunJobs();
        Assert.That(indicator.IsVisible, Is.True);

        viewModel.Refresh.IsScanning = false;
        Dispatcher.UIThread.RunJobs();
        Assert.That(indicator.IsVisible, Is.False);
    }

    [AvaloniaTest]
    public async Task RefreshProgressPanel_FollowsStartProgress()
    {
        await using var window = new MainWindow();
        var viewModel = (MainViewModel)window.DataContext!;
        var panel = window.FindControl<StackPanel>("RefreshProgressPanel")!;
        Assert.That(panel, Is.Not.Null);
        Assert.That(panel.IsVisible, Is.False);
        viewModel.Refresh.IsScanning = true;

        viewModel.Refresh.StartProgress("Refreshing", 4);
        Dispatcher.UIThread.RunJobs();

        Assert.Multiple(() =>
        {
            Assert.That(panel.IsVisible, Is.True);
            Assert.That(panel.Children.OfType<ProgressBar>().Single().Value, Is.Zero);
            Assert.That(panel.Children.OfType<TextBlock>().Single().Text,
                Is.EqualTo("Refreshing 0 / 4"));
            Assert.That(window.FindControl<StackPanel>("ScanningIndicator")!.IsVisible, Is.False);
        });
    }
}

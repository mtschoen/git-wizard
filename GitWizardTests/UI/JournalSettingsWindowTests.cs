using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitWizard.Watch;
using GitWizard.Watch.Linux;
using GitWizardTests.TestSupport;
using GitWizardUI.Services;
using GitWizardUI.ViewModels;
using GitWizardUI.Views;

namespace GitWizardTests.UI;

// Covers MainWindow.axaml, SettingsWindow.axaml, and StatusHeaderViewModel.cs.
public class JournalSettingsWindowTests
{
    [AvaloniaTest]
    public async Task MainWindowKeepsWarningsSeparateFromRefreshStatus()
    {
        var window = new MainWindow();
        try
        {
            var main = window.DataContext as MainViewModel;
            Assert.That(main, Is.Not.Null);
            var warning = new JournalWarningViewModel(new JournalWarning
            {
                DriveLetter = "C",
                VolumeSerial = 123,
                Loss = FakeJournalSettingsSource.TrimmedLoss('C', 32, 128, 8)
            });
            main.LiveWatch.JournalWarnings.Add(warning);
            var list = window.FindControl<ItemsControl>("JournalWarningList");
            Assert.That(list, Is.Not.Null);
            main.StatusHeader.ShowRefreshMessage("Refreshing repositories...");
            Dispatcher.UIThread.RunJobs();
            Assert.That(list.ItemsSource, Does.Contain(warning));
        }
        finally
        {
            await window.DisposeAsync();
        }
    }

    [AvaloniaTest]
    public async Task EnlargeButtonStaysVisibleButDisabledWithoutGrowAction()
    {
        using var sandbox = new LocalFilesSandbox();
        var source = new FakeJournalSettingsSource();
        source.CheckpointLosses.Clear();
        source.CheckpointLosses.Add(FakeJournalSettingsSource.RecreatedLoss('C', 64L * 1024 * 1024));
        var main = TestViewModels.Create(dispatcher: new AvaloniaUiDispatcher());
        main.LiveWatch.PrepareWatchAsync = (request, _, _, cancellationToken) =>
            FakeLiveStartup.FromSourceArmedAsync(source, request, cancellationToken);
        main.LiveWatch.IsElevated = () => false;
        main.LiveWatch.CapabilityProbe = () => new FanotifyCapabilityResult(FanotifyCapabilityStatus.Available, 0);
        var running = main.LiveWatch.ToggleLiveAsync();
        SettingsWindow? window = null;
        try
        {
            await source.Started.WaitAsync(TimeSpan.FromSeconds(5));
            Dispatcher.UIThread.RunJobs();
            window = new SettingsWindow(main.LiveWatch);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var buttons = window.GetVisualDescendants().OfType<Button>()
                .Where(button => Equals(button.Content, "Enlarge")).ToArray();
            Assert.That(buttons, Has.Length.EqualTo(1));
            Assert.That(buttons[0].IsVisible, Is.True);
            Assert.That(buttons[0].IsEnabled, Is.False);
            Assert.That(main.LiveWatch.JournalWarnings[0].GrowUnavailableReason, Does.Contain("journal was recreated"));
        }
        finally
        {
            window?.Close();
            await main.DisposeAsync();
            await running;
            await source.DisposeAsync();

        }
    }

    [AvaloniaTest]
    public async Task EnlargeButtonUsesExistingSourceAndUpdatesOnUiThread()
    {
        using var sandbox = new LocalFilesSandbox();
        var source = new FakeJournalSettingsSource();
        var main = TestViewModels.Create(dispatcher: new AvaloniaUiDispatcher());
        main.LiveWatch.PrepareWatchAsync = (request, _, _, cancellationToken) =>
            FakeLiveStartup.FromSourceArmedAsync(source, request, cancellationToken);
        main.LiveWatch.IsElevated = () => false;
        main.LiveWatch.CapabilityProbe = () => new FanotifyCapabilityResult(FanotifyCapabilityStatus.Available, 0);
        var running = main.LiveWatch.ToggleLiveAsync();
        SettingsWindow? window = null;
        try
        {
            await source.Started.WaitAsync(TimeSpan.FromSeconds(5));
            Dispatcher.UIThread.RunJobs();
            window = new SettingsWindow(main.LiveWatch);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var buttons = window.GetVisualDescendants().OfType<Button>()
                .Where(button => Equals(button.Content, "Enlarge")).ToArray();
            Assert.That(buttons, Has.Length.EqualTo(2));
            Assert.That(buttons.All(button => button.IsEnabled), Is.True);
            var warning = main.LiveWatch.JournalWarnings[0];
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var offThreadUpdates = 0;
            warning.PropertyChanged += (_, _) =>
            {
                if (!Dispatcher.UIThread.CheckAccess())
                {
                    Interlocked.Increment(ref offThreadUpdates);
                }

                if (warning.StatusMessage.StartsWith("Before:", StringComparison.Ordinal))
                {
                    completed.TrySetResult();
                }
            };

            buttons[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.That(source.GrowCalls, Is.EqualTo(1));
            Assert.That(offThreadUpdates, Is.Zero);
            Assert.That(warning.StatusMessage, Does.Contain("After:"));
        }
        finally
        {
            window?.Close();
            await main.DisposeAsync();
            await running;
            await source.DisposeAsync();

        }
    }
}

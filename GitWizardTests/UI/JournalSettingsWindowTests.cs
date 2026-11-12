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
[NonParallelizable]
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
            await source.Started.WaitAsync(TestTimeouts.ShortHangGuard);
            Dispatcher.UIThread.RunJobs();
            window = new SettingsWindow(main.LiveWatch);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var buttons = window.GetVisualDescendants().OfType<Button>()
                .Where(button => Equals(button.Content, "Enlarge")).ToArray();
            Assert.That(buttons, Has.Length.EqualTo(1));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(buttons[0].IsVisible, Is.True);
                Assert.That(buttons[0].IsEnabled, Is.False);
                Assert.That(main.LiveWatch.JournalWarnings[0].GrowUnavailableReason, Does.Contain("journal was recreated"));
            }
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
            await source.Started.WaitAsync(TestTimeouts.ShortHangGuard);
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
            await completed.Task.WaitAsync(TestTimeouts.ShortHangGuard);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(source.GrowCalls, Is.EqualTo(1));
                Assert.That(offThreadUpdates, Is.Zero);
                Assert.That(warning.StatusMessage, Does.Contain("After:"));
            }
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
    public async Task OverrideButtonEnlargesAboveRecommendedMaximumOnUiThread()
    {
        using var sandbox = new LocalFilesSandbox();
        var source = new FakeJournalSettingsSource();
        source.CheckpointLosses.Clear();
        source.CheckpointLosses.Add(FakeJournalSettingsSource.TrimmedLoss('C', 32L * 1024 * 1024, 10L * 1024 * 1024 * 1024));
        var main = TestViewModels.Create(dispatcher: new AvaloniaUiDispatcher());
        main.LiveWatch.PrepareWatchAsync = (request, _, _, cancellationToken) =>
            FakeLiveStartup.FromSourceArmedAsync(source, request, cancellationToken);
        main.LiveWatch.IsElevated = () => false;
        main.LiveWatch.CapabilityProbe = () => new FanotifyCapabilityResult(FanotifyCapabilityStatus.Available, 0);
        var running = main.LiveWatch.ToggleLiveAsync();
        SettingsWindow? window = null;
        try
        {
            await source.Started.WaitAsync(TestTimeouts.ShortHangGuard);
            Dispatcher.UIThread.RunJobs();
            window = new SettingsWindow(main.LiveWatch);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var enlargeButtons = window.GetVisualDescendants().OfType<Button>()
                .Where(button => Equals(button.Content, "Enlarge")).ToArray();
            var overrideButtons = window.GetVisualDescendants().OfType<Button>()
                .Where(button => Equals(button.Content, "Enlarge to needed size")).ToArray();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(enlargeButtons, Has.Length.EqualTo(1));
                Assert.That(overrideButtons, Has.Length.EqualTo(1));
            }
            using (Assert.EnterMultipleScope())
            {
                Assert.That(enlargeButtons[0].IsEnabled, Is.True);
                Assert.That(overrideButtons[0].IsVisible, Is.True);
            }
            var warning = main.LiveWatch.JournalWarnings[0];
            var clampedCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var overrideCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            warning.PropertyChanged += (_, _) =>
            {
                if (warning.StatusMessage.Contains("Clamped to the recommended maximum.", StringComparison.Ordinal))
                {
                    clampedCompleted.TrySetResult();
                }

                if (warning.StatusMessage.Contains("Enlarged above the recommended maximum by override.", StringComparison.Ordinal))
                {
                    overrideCompleted.TrySetResult();
                }
            };

            enlargeButtons[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await clampedCompleted.Task.WaitAsync(TestTimeouts.ShortHangGuard);
            Dispatcher.UIThread.RunJobs();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(source.GrowCalls, Is.EqualTo(1));
                Assert.That(source.LastAllowAboveMaximumRecommendedSize, Is.False);
                Assert.That(enlargeButtons[0].IsEnabled, Is.False);
                Assert.That(overrideButtons[0].IsVisible, Is.True);
                Assert.That(warning.Warning.Message, Does.Contain("it was 32 MB when this was detected")
                    .And.Contain("applied 8 GB"));
            }

            overrideButtons[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await overrideCompleted.Task.WaitAsync(TestTimeouts.ShortHangGuard);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(source.GrowCalls, Is.EqualTo(2));
                Assert.That(source.LastAllowAboveMaximumRecommendedSize, Is.True);
                Assert.That(warning.StatusMessage, Does.Contain("Enlarged above the recommended maximum by override."));
            }
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
    public async Task EnlargeButtonDisablesWithReasonWhenWindowsKeepsJournalUnchanged()
    {
        using var sandbox = new LocalFilesSandbox();
        var source = new FakeJournalSettingsSource
        {
            ReadBackMaximumSize = 2L * 1024 * 1024
        };
        source.Settings['C'] = new MFTLib.UsnJournalSettings
        {
            MaximumSize = 2L * 1024 * 1024,
            AllocationDelta = 512L * 1024
        };
        source.CheckpointLosses.Clear();
        source.CheckpointLosses.Add(FakeJournalSettingsSource.TrimmedLoss('C', 2L * 1024 * 1024, 10L * 1024 * 1024 * 1024, 512L * 1024));
        var main = TestViewModels.Create(dispatcher: new AvaloniaUiDispatcher());
        main.LiveWatch.PrepareWatchAsync = (request, _, _, cancellationToken) =>
            FakeLiveStartup.FromSourceArmedAsync(source, request, cancellationToken);
        main.LiveWatch.IsElevated = () => false;
        main.LiveWatch.CapabilityProbe = () => new FanotifyCapabilityResult(FanotifyCapabilityStatus.Available, 0);
        var running = main.LiveWatch.ToggleLiveAsync();
        SettingsWindow? window = null;
        try
        {
            await source.Started.WaitAsync(TestTimeouts.ShortHangGuard);
            Dispatcher.UIThread.RunJobs();
            window = new SettingsWindow(main.LiveWatch);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var enlargeButtons = window.GetVisualDescendants().OfType<Button>()
                .Where(button => Equals(button.Content, "Enlarge")).ToArray();
            var overrideButtons = window.GetVisualDescendants().OfType<Button>()
                .Where(button => Equals(button.Content, "Enlarge to needed size")).ToArray();
            var warning = main.LiveWatch.JournalWarnings[0];
            var unchangedCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            warning.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(JournalWarningViewModel.StatusMessage) &&
                    warning.StatusMessage != "Enlarging...")
                {
                    unchangedCompleted.TrySetResult();
                }
            };

            enlargeButtons[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await unchangedCompleted.Task.WaitAsync(TestTimeouts.ShortHangGuard);
            Dispatcher.UIThread.RunJobs();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(source.GrowCalls, Is.EqualTo(1));
                Assert.That(enlargeButtons[0].IsEnabled, Is.False);
                Assert.That(overrideButtons[0].IsVisible, Is.False);
                Assert.That(warning.GrowUnavailableReason, Is.EqualTo("Windows accepted the request but kept the journal at 2 MB."));
                Assert.That(warning.StatusMessage, Does.Contain("Windows accepted the request but kept the journal at 2,097,152 bytes (2 MB).")
                    .And.Not.Contain("Enlarged")
                    .And.Not.Contain("applied"));
                Assert.That(warning.Warning.Message, Does.Not.Contain("applied"));
            }
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

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
            var warning = new JournalWarningViewModel(new JournalWarning("C", 123, 32, 128, 8));
            main.JournalWarnings.Add(warning);
            var list = window.FindControl<ItemsControl>("JournalWarningList");
            Assert.That(list, Is.Not.Null);
            main.HeaderText = "Refreshing repositories...";
            Dispatcher.UIThread.RunJobs();
            Assert.That(list.ItemsSource, Does.Contain(warning));
        }
        finally
        {
            await window.DisposeAsync();
        }
    }

    [AvaloniaTest]
    public async Task EnlargeButtonUsesExistingSourceAndUpdatesOnUiThread()
    {
        var temporaryRoot = TestUtilities.RedirectLocalFilesToTemp();
        TestUtilities.ResetStaticCaches();
        var source = new FakeJournalSettingsSource();
        var main = new MainViewModel(new AvaloniaUiDispatcher(), new StubUserDialogs(), new StubClipboardService())
        {
            LiveVolumeChangeSourceFactory = () => source,
            LiveIsElevated = () => false,
            LiveCapabilityProbe = () => new FanotifyCapabilityResult(FanotifyCapabilityStatus.Available, 0)
        };
        var running = main.ToggleLiveAsync();
        SettingsWindow? window = null;
        try
        {
            await source.Started.WaitAsync(TimeSpan.FromSeconds(5));
            Dispatcher.UIThread.RunJobs();
            window = new SettingsWindow(main);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var buttons = window.GetVisualDescendants().OfType<Button>()
                .Where(button => Equals(button.Content, "Enlarge")).ToArray();
            Assert.That(buttons, Has.Length.EqualTo(2));
            Assert.That(buttons.All(button => button.IsEnabled), Is.True);
            var warning = main.JournalWarnings[0];
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var offThreadUpdates = 0;
            warning.PropertyChanged += (_, _) =>
            {
                if (!Dispatcher.UIThread.CheckAccess())
                    Interlocked.Increment(ref offThreadUpdates);
                if (warning.StatusMessage.StartsWith("Before:", StringComparison.Ordinal))
                    completed.TrySetResult();
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
            TestUtilities.ResetStaticCaches();
            TestUtilities.ClearLocalFilesRedirect(temporaryRoot);
        }
    }
}

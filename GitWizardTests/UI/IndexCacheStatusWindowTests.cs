using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using GitWizard;
using GitWizardTests.TestSupport;
using GitWizardUI.Services;
using GitWizardUI.ViewModels;
using GitWizardUI.Views;

namespace GitWizardTests.UI;

public class IndexCacheStatusWindowTests
{
    [AvaloniaTest]
    public async Task RefreshClickPublishesReadableStatusOnDispatcher()
    {
        using var files = new PackedIndexFixture();
        var row = new MFTLib.Index.CachedBlockStatus(
            new MFTLib.Index.CachedBlockFile('X', 42, "synthetic", 4096,
                FrozenCacheClock.Now.UtcDateTime.AddHours(-2)),
            MFTLib.Index.CachedBlockAvailability.InUse, null, null, null);
        var snapshot = new MftIndexCacheInspection(new FrozenCacheClock(), true,
            _ => new[] { row }).Capture(files.DirectoryPath);
        var model = new IndexCacheStatusViewModel(new AvaloniaUiDispatcher(), () => snapshot);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var offThread = false;
        model.PropertyChanged += (_, arguments) =>
        {
            offThread |= !Dispatcher.UIThread.CheckAccess();
            if (arguments.PropertyName == nameof(model.Snapshot))
                completed.TrySetResult();
        };
        var window = new IndexCacheStatusWindow(model);
        try
        {
            window.Show();
            window.FindControl<Button>("RefreshStatusButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Dispatcher.UIThread.RunJobs();
            var text = window.FindControl<SelectableTextBlock>("CacheStatusText")!.Text;
            Assert.That(text, Does.Contain("Drive X: serial 0000002A"));
            Assert.That(text, Does.Contain("age: 2h"));
            Assert.That(text, Does.Contain("4096 bytes"));
            Assert.That(text, Does.Contain("holder identity unavailable"));
            Assert.That(offThread, Is.False);
            Assert.That(window.FindControl<Button>("ClearIndexCacheButton"), Is.Null);
        }
        finally { window.Close(); }
    }
}

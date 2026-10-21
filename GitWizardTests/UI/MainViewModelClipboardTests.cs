using GitWizard;
using GitWizardTests.TestSupport;
using GitWizardUI.ViewModels;
using GitWizardUI.ViewModels.Services;

namespace GitWizardTests;

// Clipboard tests exercise row copy commands, delay restoration, and failure paths.
// Covers MainViewModel.Commands.cs.
public class MainViewModelClipboardTests
{
    static RepositoryNodeViewModel RepoNode(string workingDirectory)
        => new(new GitWizardRepository(workingDirectory));

    sealed class ThrowingClipboardService : IClipboardService
    {
        public Task SetPlainTextAsync(string text) => throw new InvalidOperationException("clipboard unavailable");
    }

    [Test]
    public void CopyToClipboard_CopiesWorkingDirectoryToClipboard()
    {
        var clipboard = new StubClipboardService();
        var vm = new MainViewModel(new StubUiDispatcher(), new StubUserDialogs(), clipboard);

        // The stub clipboard and dispatcher complete synchronously, so the write and the indicator
        // post have already run before the returned (still-pending) task is dropped.
        _ = vm.CopyToClipboardAsync(RepoNode("C:/projects/widget"));

        Assert.That(clipboard.Writes, Does.Contain("C:/projects/widget"));
    }

    // The "Copied" modal dialog was replaced with a per-row indicator: copying must light a transient
    // flag on the node (which the view binds an icon to), not pop a dialog.
    [Test]
    public void CopyToClipboard_LightsTheRowCopiedIndicator()
    {
        var vm = new MainViewModel(new StubUiDispatcher(), new StubUserDialogs(), new StubClipboardService());
        var node = RepoNode("C:/projects/widget");

        _ = vm.CopyToClipboardAsync(node);

        // The stub clipboard completes synchronously and the stub dispatcher posts inline, so the
        // indicator is already lit when the copy task yields on its reset delay.
        Assert.That(node.JustCopied, Is.True);
    }

    [Test]
    public async Task CopyToClipboardAsync_ClearsTheCopiedIndicatorAfterTheDelay()
    {
        var clock = new ControlledDelayClock();
        var vm = new MainViewModel(new StubUiDispatcher(), new StubUserDialogs(),
            new StubClipboardService(), clock);
        var node = RepoNode("C:/projects/widget");
        var copy = vm.CopyToClipboardAsync(node);
        try
        {
            Assert.That(clock.Timer, Is.Not.Null, "The indicator must use the injected clock.");
            Assert.That(node.JustCopied, Is.True);
            Assert.That(clock.Timer!.DueTime, Is.EqualTo(TimeSpan.FromMilliseconds(1500)));
            Assert.That(clock.Timer.Period, Is.EqualTo(Timeout.InfiniteTimeSpan));
            Assert.That(copy.IsCompleted, Is.False);
            clock.Timer.Fire();
            await copy;
            Assert.That(node.JustCopied, Is.False);
            Assert.That(clock.Timer.IsDisposed, Is.True);
        }
        finally
        {
            clock.Timer?.Fire();
            await copy;
        }
    }

    [Test]
    public async Task CopyToClipboard_DoesNothingForNullOrGroupHeaderNodes()
    {
        var clipboard = new StubClipboardService();
        var vm = new MainViewModel(new StubUiDispatcher(), new StubUserDialogs(), clipboard);

        await vm.CopyToClipboardAsync(null);
        await vm.CopyToClipboardAsync(RepositoryNodeViewModel.CreateGroupHeader("C:"));

        Assert.That(clipboard.Writes, Is.Empty, "A null node and a group header (no working directory) must not copy.");
    }

    // A clipboard failure must propagate to the caller (a code-behind fire-and-forget drop leaves
    // it unobserved) and must not light a false "copied" indicator.
    [Test]
    public void CopyToClipboardAsync_FailsWithoutLightingIndicatorWhenClipboardThrows()
    {
        var vm = new MainViewModel(new StubUiDispatcher(), new StubUserDialogs(), new ThrowingClipboardService());
        var node = RepoNode("C:/projects/widget");

        Assert.ThrowsAsync<InvalidOperationException>(() => vm.CopyToClipboardAsync(node));
        Assert.That(node.JustCopied, Is.False, "A failed clipboard write must not light the copied indicator.");
    }

    [Test]
    public async Task CopyToClipboard_DoesNothing_ForNodeWithoutWorkingDirectory()
    {
        var clipboard = new StubClipboardService();
        var vm = new MainViewModel(new StubUiDispatcher(), new StubUserDialogs(), clipboard);
        var node = new RepositoryNodeViewModel(new GitWizardRepository(""));

        await vm.CopyToClipboardAsync(node);

        Assert.That(clipboard.Writes, Is.Empty);
    }
}

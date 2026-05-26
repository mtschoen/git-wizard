using GitWizard;
using GitWizardUI.ViewModels.Services;

namespace GitWizardTests;

/// <summary>
/// Tests for the StubClipboardService and StubUiDispatcher stubs.
/// These are small helper classes that need coverage too.
/// </summary>
public class StubServiceTests
{
    [Test]
    public void Post_InvokesActionSynchronously()
    {
        var dispatcher = new StubUiDispatcher();
        var called = false;

        dispatcher.Post(() => called = true);

        Assert.That(called, Is.True);
    }

    [Test]
    public async Task InvokeAsync_RunsAndCompletes()
    {
        var dispatcher = new StubUiDispatcher();
        var called = false;

        await dispatcher.InvokeAsync(() => called = true);

        Assert.That(called, Is.True);
    }

    [Test]
    public void IsOnUiThread_AlwaysTrue()
    {
        Assert.That(new StubUiDispatcher().IsOnUiThread, Is.True);
    }

    [Test]
    public async Task InvokeAsync_AsyncAction_RunsAndCompletes()
    {
        var dispatcher = new StubUiDispatcher();
        var called = false;

        await dispatcher.InvokeAsync(async () => { await Task.Delay(0); called = true; });

        Assert.That(called, Is.True);
    }

    [Test]
    public async Task DisplayAlertAsync_RecordsCall()
    {
        var dialogs = new StubUserDialogs();

        await dialogs.DisplayAlertAsync("Title", "Body");

        Assert.That(dialogs.AlertCalls, Has.Count.EqualTo(1));
        Assert.That(dialogs.AlertCalls[0].Title, Is.EqualTo("Title"));
        Assert.That(dialogs.AlertCalls[0].Message, Is.EqualTo("Body"));
    }

    [Test]
    public async Task DisplayConfirmAsync_ReturnsScriptedAnswer()
    {
        var dialogs = new StubUserDialogs { NextConfirmResult = true };

        var result = await dialogs.DisplayConfirmAsync("Are you sure?", "Really?");

        Assert.That(result, Is.True);
        Assert.That(dialogs.ConfirmCalls, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task PickFolderAsync_ReturnsScriptedPath()
    {
        var picker = new StubFolderPicker { NextResult = "/tmp/repos" };

        var result = await picker.PickFolderAsync();

        Assert.That(result, Is.EqualTo("/tmp/repos"));
        Assert.That(picker.PickCount, Is.EqualTo(1));
    }

    [Test]
    public async Task PickFolderAsync_NullMeansCancelled()
    {
        var picker = new StubFolderPicker { NextResult = null };

        var result = await picker.PickFolderAsync();

        Assert.That(result, Is.Null);
    }
}

/// <summary>
/// Tests for the StubClipboardService.
/// </summary>
public class StubClipboardServiceTests
{
    [Test]
    public async Task SetPlainTextAsync_AppendsToWrites()
    {
        var clipboard = new StubClipboardService();

        await clipboard.SetPlainTextAsync("test content");

        Assert.That(clipboard.Writes, Has.Count.EqualTo(1));
        Assert.That(clipboard.Writes[0], Is.EqualTo("test content"));
    }

    [Test]
    public async Task SetPlainTextAsync_AccumulatesWrites()
    {
        var clipboard = new StubClipboardService();

        await clipboard.SetPlainTextAsync("first");
        await clipboard.SetPlainTextAsync("second");
        await clipboard.SetPlainTextAsync("third");

        Assert.That(clipboard.Writes, Has.Count.EqualTo(3));
        Assert.That(clipboard.Writes, Is.EqualTo(new[] { "first", "second", "third" }));
    }

    [Test]
    public async Task SetPlainTextAsync_ReturnsCompletedTask()
    {
        var clipboard = new StubClipboardService();
        var task = clipboard.SetPlainTextAsync("content");

        Assert.That(task.IsCompleted, Is.True);
        Assert.That(task.IsFaulted, Is.False);
        Assert.That(task.IsCanceled, Is.False);
    }

    [Test]
    public void Writes_Collection_IsNotNull()
    {
        var clipboard = new StubClipboardService();
        Assert.That(clipboard.Writes, Is.Not.Null);
    }

    [Test]
    public void Writes_Collection_IsInitiallyEmpty()
    {
        var clipboard = new StubClipboardService();
        Assert.That(clipboard.Writes, Is.Empty);
    }
}

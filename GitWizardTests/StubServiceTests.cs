using GitWizardUI.ViewModels.Services;

namespace GitWizardTests;

public class StubUiDispatcherTests
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
}

public class StubUserDialogsTests
{
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
}

public class StubFolderPickerTests
{
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

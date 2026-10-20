using GitWizardUI.ViewModels.Services;

namespace GitWizardTests;

public sealed class StubClipboardService : IClipboardService
{
    public List<string> Writes { get; } = new();

    public Task SetPlainTextAsync(string text)
    {
        Writes.Add(text);
        return Task.CompletedTask;
    }
}

public sealed class StubFolderPicker : IFolderPicker
{
    public string? NextResult { get; set; }
    public int PickCount { get; private set; }

    public Task<string?> PickFolderAsync()
    {
        PickCount++;
        return Task.FromResult(NextResult);
    }
}

public sealed class StubUserDialogs : IUserDialogs
{
    public record AlertCall(string Title, string Message);

    public List<AlertCall> AlertCalls { get; } = new();
    public bool NextConfirmResult { get; set; }

    public Task ShowAlertAsync(string title, string message, string okLabel = "OK")
    {
        AlertCalls.Add(new AlertCall(title, message));
        return Task.CompletedTask;
    }

    public Task<bool> ConfirmAsync(string title, string message, string acceptLabel = "Yes", string cancelLabel = "No")
    {
        return Task.FromResult(NextConfirmResult);
    }
}

/// <summary>Synchronous test stub. Pretends every call site is the UI thread.</summary>
public sealed class StubUiDispatcher : IUiDispatcher
{
    public bool IsOnUiThread => true;
    public void Post(Action action) => action();
    public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
    public Task InvokeAsync(Func<Task> action) => action();
}

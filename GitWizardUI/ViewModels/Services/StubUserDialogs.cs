namespace GitWizardUI.ViewModels.Services;

/// <summary>Test stub: records every call, returns scripted answers for confirms.</summary>
public sealed class StubUserDialogs : IUserDialogs
{
    public record AlertCall(string Title, string Message);
    public record ConfirmCall(string Title, string Message);

    public List<AlertCall> AlertCalls { get; } = new();
    public List<ConfirmCall> ConfirmCalls { get; } = new();
    public bool NextConfirmResult { get; set; }

    public Task DisplayAlertAsync(string title, string message, string okLabel = "OK")
    {
        AlertCalls.Add(new AlertCall(title, message));
        return Task.CompletedTask;
    }

    public Task<bool> DisplayConfirmAsync(string title, string message, string acceptLabel = "Yes", string cancelLabel = "No")
    {
        ConfirmCalls.Add(new ConfirmCall(title, message));
        return Task.FromResult(NextConfirmResult);
    }
}

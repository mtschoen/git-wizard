using GitWizardUI.ViewModels.Services;

namespace GitWizardTests;

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

namespace GitWizardUI.ViewModels.Services;

/// <summary>Show modal user-facing dialogs. Owner window resolution is the impl's responsibility.</summary>
public interface IUserDialogs
{
    Task ShowAlertAsync(string title, string message, string okLabel = "OK");
    Task<bool> ConfirmAsync(string title, string message, string acceptLabel = "Yes", string cancelLabel = "No");
}

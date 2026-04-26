namespace GitWizardUI.ViewModels.Services;

/// <summary>Show modal user-facing dialogs. Owner window resolution is the impl's responsibility.</summary>
public interface IUserDialogs
{
    Task DisplayAlertAsync(string title, string message, string okLabel = "OK");
    Task<bool> DisplayConfirmAsync(string title, string message, string acceptLabel = "Yes", string cancelLabel = "No");
}

using GitWizardUI.ViewModels.Services;
using Microsoft.Maui.Controls;

namespace GitWizardUI.Services;

public sealed class MauiUserDialogs : IUserDialogs
{
    public async Task DisplayAlertAsync(string title, string message, string okLabel = "OK")
    {
        if (Application.Current?.Windows.Count > 0 && Application.Current.Windows[0].Page is { } page)
            await page.DisplayAlertAsync(title, message, okLabel);
    }

    public async Task<bool> DisplayConfirmAsync(string title, string message, string acceptLabel = "Yes", string cancelLabel = "No")
    {
        if (Application.Current?.Windows.Count > 0 && Application.Current.Windows[0].Page is { } page)
            return await page.DisplayAlertAsync(title, message, acceptLabel, cancelLabel);
        return false;
    }
}

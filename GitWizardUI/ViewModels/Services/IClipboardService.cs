namespace GitWizardUI.ViewModels.Services;

public interface IClipboardService
{
    Task SetPlainTextAsync(string text);
}

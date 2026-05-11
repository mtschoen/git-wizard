using GitWizardUI.ViewModels.Services;

namespace GitWizardUI.Services;

public class MauiClipboardService : IClipboardService
{
    readonly IUiDispatcher _ui;

    public MauiClipboardService(IUiDispatcher ui)
    {
        _ui = ui;
    }

    public Task SetPlainTextAsync(string text)
    {
        return _ui.InvokeAsync(async () =>
        {
            await Clipboard.Default.SetTextAsync(text);
        });
    }
}

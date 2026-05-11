using Avalonia.Controls;
using GitWizardUI.ViewModels.Services;

namespace GitWizardAvalonia.Services;

public class AvaloniaClipboardService : IClipboardService
{
    readonly dynamic? _clipboard;

    public AvaloniaClipboardService(TopLevel topLevel)
    {
        _clipboard = topLevel?.Clipboard;
    }

    public Task SetPlainTextAsync(string text)
    {
        if (_clipboard != null)
            return _clipboard.SetTextAsync(text);
        return Task.CompletedTask;
    }
}

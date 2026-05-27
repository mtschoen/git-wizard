using Avalonia.Controls;
using Avalonia.Input.Platform;
using GitWizardUI.ViewModels.Services;

namespace GitWizardUI.Services;

public class AvaloniaClipboardService : IClipboardService
{
    // Use the IClipboard interface type, NOT `dynamic`: Avalonia's concrete clipboard implements
    // IClipboard.SetTextAsync as an explicit interface member, which `dynamic` dispatch cannot see
    // (it throws RuntimeBinderException: "'object' does not contain a definition for 'SetTextAsync'").
    readonly IClipboard? _clipboard;

    public AvaloniaClipboardService(TopLevel topLevel)
    {
        _clipboard = topLevel.Clipboard;
    }

    public Task SetPlainTextAsync(string text)
        => _clipboard?.SetTextAsync(text) ?? Task.CompletedTask;
}

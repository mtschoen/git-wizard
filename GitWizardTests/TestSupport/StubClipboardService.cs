using GitWizardUI.ViewModels.Services;

namespace GitWizardTests;

public sealed class StubClipboardService : IClipboardService
{
    public List<string> Writes { get; } = [];

    public Task SetPlainTextAsync(string text)
    {
        Writes.Add(text);
        return Task.CompletedTask;
    }
}

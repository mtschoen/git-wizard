namespace GitWizardUI.ViewModels.Services;

public sealed class StubClipboardService : IClipboardService
{
    public List<string> Writes { get; } = new();

    public Task SetPlainTextAsync(string text)
    {
        Writes.Add(text);
        return Task.CompletedTask;
    }
}

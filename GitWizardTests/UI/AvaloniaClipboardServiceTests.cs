using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using GitWizardUI.Services;

namespace GitWizardTests;

public class AvaloniaClipboardServiceTests
{
    // Regression guard for the crash that shipped latent: AvaloniaClipboardService._clipboard was
    // typed `dynamic`, so SetTextAsync (an explicitly-implemented IClipboard member) threw
    // RuntimeBinderException at runtime and took down the whole app. The StubClipboardService used by
    // the view-model tests cannot catch that class of bug - this exercises the REAL Avalonia
    // IClipboard via a headless TopLevel, which is the exact path that broke.
    [AvaloniaTest]
    public async Task SetPlainTextAsync_WritesThroughToTheRealClipboard()
    {
        var window = new Window();
        window.Show();

        var service = new AvaloniaClipboardService(window);
        await service.SetPlainTextAsync("C:/projects/widget");

        Assert.That(window.Clipboard, Is.Not.Null, "Headless TopLevel must expose a clipboard.");
        var roundTripped = await window.Clipboard!.GetTextAsync();
        Assert.That(roundTripped, Is.EqualTo("C:/projects/widget"));
    }
}

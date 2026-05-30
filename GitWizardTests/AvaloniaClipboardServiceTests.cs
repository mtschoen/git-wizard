using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using GitWizardUI.Services;

[assembly: AvaloniaTestApplication(typeof(GitWizardTests.TestAppBuilder))]

namespace GitWizardTests;

/// <summary>
/// Headless Avalonia app used by the <c>[AvaloniaTestApplication]</c> attribute to give [AvaloniaTest]
/// methods a real UI thread + platform. Reuses the production <see cref="GitWizardUI.App"/>, which
/// only opens MainWindow under a classic-desktop lifetime — absent here — so no window/refresh runs.
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<GitWizardUI.App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public class AvaloniaClipboardServiceTests
{
    // Regression guard for the crash that shipped latent: AvaloniaClipboardService._clipboard was
    // typed `dynamic`, so SetTextAsync (an explicitly-implemented IClipboard member) threw
    // RuntimeBinderException at runtime and took down the whole app. The StubClipboardService used by
    // the view-model tests cannot catch that class of bug — this exercises the REAL Avalonia
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

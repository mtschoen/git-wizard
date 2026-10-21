using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(GitWizardTests.TestAppBuilder))]

namespace GitWizardTests;

/// <summary>
/// Headless Avalonia app used by the <c>[AvaloniaTestApplication]</c> attribute to give [AvaloniaTest]
/// methods a real UI thread + platform. Reuses the production <see cref="GitWizardUI.App"/>, which
/// only opens MainWindow under a classic-desktop lifetime - absent here - so no window/refresh runs.
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<GitWizardUI.App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

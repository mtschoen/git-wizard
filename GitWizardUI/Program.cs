using Avalonia;
using GitWizard;
using MFTLib;

namespace GitWizardUI;

static class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args) => Run(args,
        ElevatedEntryPoint.RunBrokerIfRequested, WindowsDefender.TryRunElevatedHelper,
        DesktopInstance.CreateName,
        arguments => BuildAvaloniaApp().StartWithClassicDesktopLifetime(arguments),
        StartupNotice.Show);

    internal static int Run(string[] arguments, Func<string[], bool> tryHandleBroker,
        Func<string[], int?> tryRunDefenderHelper, Func<string> getMutexName,
        Func<string[], int> startDesktop, Action<string> showNotice)
    {
        if (tryHandleBroker(arguments))
        {
            return 0;
        }

        if (tryRunDefenderHelper(arguments) is { } helperExitCode)
        {
            return helperExitCode;
        }

        LibGit2MemorySettings.Configure();
        return DesktopInstance.Run(getMutexName(), () =>
        {
            if (Array.IndexOf(arguments, "-v") >= 0)
            {
                GitWizardLog.VerboseMode = true;
            }

            return startDesktop(arguments);
        }, showNotice);
    }

    // Required by the Avalonia visual designer - do not remove.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}

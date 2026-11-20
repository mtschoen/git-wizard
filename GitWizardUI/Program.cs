using Avalonia;
using GitWizard;
using GitWizard.Watch.Linux;
using MFTLib;

namespace GitWizardUI;

static class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args) => Run(args,
        ElevatedEntryPoint.RunBrokerIfRequested, [WindowsDefender.TryRunElevatedHelper, LinuxWatchHelperHost.TryRun],
        DesktopInstance.CreateName,
        arguments => BuildAvaloniaApp().StartWithClassicDesktopLifetime(arguments),
        StartupNotice.Show);

    internal static int Run(string[] arguments, Func<string[], bool> tryHandleBroker,
        IReadOnlyList<Func<string[], int?>> tryRunHelpers, Func<string> getMutexName,
        Func<string[], int> startDesktop, Action<string> showNotice)
    {
        if (tryHandleBroker(arguments))
        {
            return 0;
        }

        // Child modes run in order (Defender, then the Linux watch helper); the first that claims
        // the arguments owns the process.
        foreach (var tryRunHelper in tryRunHelpers)
        {
            if (tryRunHelper(arguments) is { } helperExitCode)
            {
                return helperExitCode;
            }
        }

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

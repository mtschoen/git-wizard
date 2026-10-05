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
        ElevatedEntryPoint.TryHandle, WindowsDefender.RunDefenderCommands,
        DesktopInstance.CreateName,
        arguments => BuildAvaloniaApp().StartWithClassicDesktopLifetime(arguments),
        StartupNotice.Show);

    internal static int Run(string[] arguments, Func<string[], bool> tryHandleBroker,
        Func<bool> runDefender, Func<string> getMutexName,
        Func<string[], int> startDesktop, Action<string> showNotice)
    {
        if (tryHandleBroker(arguments))
        {
            return 0;
        }

        if (arguments.Contains("--elevated-defender"))
        {
            return runDefender() ? 0 : 1;
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

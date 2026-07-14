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
    public static void Main(string[] args)
    {
        // MFTLib's elevated journal-broker child mode: broker-backed repository discovery
        // relaunches THIS exe with --broker to run the elevated MFT scan over a pipe. Handle
        // it (and the Defender child mode below) before any Avalonia init so the elevated
        // child performs its single task and exits instead of opening a second GUI window.
        if (ElevatedEntryPoint.TryHandle(args, new DefaultElevatedEntryRunner()))
            return;

        if (TryHandleElevatedMode(args))
            return;

        if (Array.IndexOf(args, "-v") >= 0)
            GitWizardLog.VerboseMode = true;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    static bool TryHandleElevatedMode(string[] args)
    {
        if (!args.Contains("--elevated-defender"))
            return false;

        Environment.Exit(WindowsDefender.RunDefenderCommands() ? 0 : 1);
        return true;
    }

    // Required by the Avalonia visual designer - do not remove.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}

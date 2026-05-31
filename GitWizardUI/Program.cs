using Avalonia;
using GitWizard;

namespace GitWizardUI;

static class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Self-elevation child modes: the core (GitWizardApi.GetRepositoryPaths /
        // WindowsDefender) relaunches THIS exe with these flags. Handle
        // them before any Avalonia init so the elevated child performs its single
        // task and exits instead of opening a second GUI window.
        if (TryHandleElevatedMode(args))
            return;

        if (Array.IndexOf(args, "-v") >= 0)
            GitWizardLog.VerboseMode = true;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    static bool TryHandleElevatedMode(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--elevated-mft":
                    {
                        string? configPath = null;
                        string? outputPath = null;
                        for (var j = i + 1; j < args.Length; j++)
                        {
                            switch (args[j])
                            {
                                case "--config-path":
                                    if (j + 1 < args.Length) configPath = args[++j];
                                    break;
                                case "--output":
                                    if (j + 1 < args.Length) outputPath = args[++j];
                                    break;
                            }
                        }

                        if (configPath != null && outputPath != null)
                        {
                            GitWizardApi.RunElevatedMftScan(configPath, outputPath);
                            Environment.Exit(0);
                        }

                        Environment.Exit(1);
                        return true;
                    }
                case "--elevated-defender":
                    Environment.Exit(WindowsDefender.RunDefenderCommands() ? 0 : 1);
                    return true;
            }
        }

        return false;
    }

    // Required by the Avalonia visual designer - do not remove.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}

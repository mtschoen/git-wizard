using GitWizard;

namespace GitWizardUI;

public partial class App : Application
{
    public App()
    {
        // Handle elevated helper modes before initializing UI
        if (TryHandleElevatedMode())
            return;

        // TODO: Share code for parsing CLI arguments
        if (Environment.GetCommandLineArgs().Contains("-v"))
            GitWizardLog.VerboseMode = true;

        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        return new Window(new AppShell());
    }

    static bool TryHandleElevatedMode()
    {
        var args = Environment.GetCommandLineArgs();
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
                    else
                    {
                        Environment.Exit(1);
                    }

                    return true;
                }
                case "--elevated-defender":
                {
                    var success = WindowsDefenderException.RunDefenderCommands();
                    Environment.Exit(success ? 0 : 1);
                    return true;
                }
            }
        }

        return false;
    }
}
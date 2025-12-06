using GitWizard;

namespace GitWizardMAUI;

public partial class App : Application
{
	public App()
	{
        // TODO: Share code for parsing CLI arguments
        if (Environment.GetCommandLineArgs().Contains("-v"))
            GitWizardLog.VerboseMode = true;

        InitializeComponent();
	}

    protected override Window CreateWindow(IActivationState? activationState)
    {
        return new Window(new AppShell());
    }
}


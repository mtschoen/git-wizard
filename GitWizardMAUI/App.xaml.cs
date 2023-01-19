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

		MainPage = new AppShell();
	}
}


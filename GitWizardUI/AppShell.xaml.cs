namespace GitWizardUI;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();

        // Register routes for navigation
        Routing.RegisterRoute(nameof(SettingsPage), typeof(SettingsPage));
    }
}
using GitWizardUI.ViewModels;

namespace GitWizardUI;

public partial class SettingsPage : ContentPage
{
    public SettingsPage()
    {
        InitializeComponent();
        BindingContext = new SettingsViewModel();
    }
}

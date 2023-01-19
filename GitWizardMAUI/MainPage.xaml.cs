using GitWizard;

namespace GitWizardMAUI;

public partial class MainPage : ContentPage
{
	public MainPage()
	{
		InitializeComponent();
	}

    void SettingsMenuItem_Click(object sender, EventArgs eventArgs)
    {
        //_settingsWindow ??= new SettingsWindow();
        //_settingsWindow.Show();
    }

    void CheckWindowsDefenderMenuItem_Click(object sender, EventArgs eventArgs)
    {
        WindowsDefenderException.AddException();
    }

    void ClearCacheMenuItem_Click(object sender, EventArgs eventArgs)
    {
        GitWizardApi.ClearCache();
    }

    void DeleteAllLocalFilesMenuItem_Click(object sender, EventArgs eventArgs)
    {
        GitWizardApi.DeleteAllLocalFiles();
    }

    void RefreshButton_Click(object sender, EventArgs e)
    {
        //Refresh(false);
    }
}



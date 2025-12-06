using GitWizard;
using GitWizardUI.ViewModels;

namespace GitWizardUI;

public partial class MainPage : ContentPage
{
    private readonly MainViewModel _viewModel;

    public MainPage()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        BindingContext = _viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.RefreshAsync(background: true);
    }

    async void SettingsMenuItem_Click(object sender, EventArgs eventArgs)
    {
        await Shell.Current.GoToAsync(nameof(SettingsPage));
    }

    void CheckWindowsDefenderMenuItem_Click(object sender, EventArgs eventArgs)
    {
        WindowsDefenderException.AddException();
    }

    async void ClearCacheMenuItem_Click(object sender, EventArgs eventArgs)
    {
        GitWizardApi.ClearCache();
        await DisplayAlertAsync("Cache Cleared", "Repository cache has been cleared", "OK");
    }

    async void DeleteAllLocalFilesMenuItem_Click(object sender, EventArgs eventArgs)
    {
        GitWizardApi.DeleteAllLocalFiles();
        await DisplayAlertAsync("Files Deleted", "All local files have been deleted", "OK");
    }

    async void RefreshButton_Click(object sender, EventArgs e)
    {
        await _viewModel.RefreshAsync(background: false);
    }
}



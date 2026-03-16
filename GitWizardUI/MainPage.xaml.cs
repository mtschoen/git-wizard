using GitWizard;
using GitWizardUI.ViewModels;

namespace GitWizardUI;

public partial class MainPage : ContentPage
{
    private readonly MainViewModel _viewModel;
    private Button? _activeFilterButton;
    private Button? _activeGroupButton;

    public MainPage()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        _viewModel.ScrollToRequest = node =>
        {
            RepositoryList.ScrollTo(node, position: ScrollToPosition.MakeVisible, animate: false);
        };
        BindingContext = _viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await Task.Delay(500);
        await _viewModel.RefreshAsync(background: true);
    }

    async void SettingsMenuItem_Click(object sender, EventArgs eventArgs)
    {
        await Shell.Current.GoToAsync(nameof(SettingsPage));
    }

    async void CheckWindowsDefenderMenuItem_Click(object sender, EventArgs eventArgs)
    {
        var success = await Task.Run(() => WindowsDefenderException.AddExclusions());
        await DisplayAlertAsync(
            success ? "Defender Exclusions Added" : "Defender Setup Failed",
            success ? "Process exclusions for dotnet, git, git-lfs, and git-wizard have been added."
                    : "Failed to add Windows Defender exclusions. You may need to run as administrator.",
            "OK");
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

    async void FetchAndRefreshButton_Click(object sender, EventArgs e)
    {
        await _viewModel.RefreshAsync(background: false, fetchRemotes: true);
    }

    void FilterButton_Click(object sender, EventArgs e)
    {
        if (sender is not Button button)
            return;

        var filter = GetFilterType(button);

        // Toggle visual state
        if (_activeFilterButton == button)
        {
            _activeFilterButton.FontAttributes = FontAttributes.None;
            _activeFilterButton = null;
        }
        else
        {
            if (_activeFilterButton != null)
                _activeFilterButton.FontAttributes = FontAttributes.None;

            button.FontAttributes = FontAttributes.Bold;
            _activeFilterButton = button;
        }

        _viewModel.ToggleFilter(filter);
    }

    void GroupButton_Click(object sender, EventArgs e)
    {
        if (sender is not Button button)
            return;

        var mode = GetGroupMode(button);

        if (_activeGroupButton == button)
        {
            _activeGroupButton.FontAttributes = FontAttributes.None;
            _activeGroupButton = null;
        }
        else
        {
            if (_activeGroupButton != null)
                _activeGroupButton.FontAttributes = FontAttributes.None;

            button.FontAttributes = FontAttributes.Bold;
            _activeGroupButton = button;
        }

        _viewModel.ToggleGroupMode(mode);
    }

    GroupMode GetGroupMode(Button button)
    {
        if (button == GroupByDrive) return GroupMode.Drive;
        if (button == GroupByRemoteUrl) return GroupMode.RemoteUrl;
        return GroupMode.None;
    }

    void SortButton_Click(object sender, EventArgs e)
    {
        if (sender is not Button button)
            return;

        var mode = GetSortMode(button);

        // Highlight the active sort button
        SortByWorkingDirectory.FontAttributes = FontAttributes.None;
        SortByRecentlyUsed.FontAttributes = FontAttributes.None;
        SortByRemoteUrl.FontAttributes = FontAttributes.None;
        button.FontAttributes = FontAttributes.Bold;

        _viewModel.SetSortMode(mode);
    }

    SortMode GetSortMode(Button button)
    {
        if (button == SortByRecentlyUsed) return SortMode.RecentlyUsed;
        if (button == SortByRemoteUrl) return SortMode.RemoteUrl;
        return SortMode.WorkingDirectory;
    }

    FilterType GetFilterType(Button button)
    {
        if (button == FilterPendingChanges) return FilterType.PendingChanges;
        if (button == FilterSubmoduleCheckout) return FilterType.SubmoduleCheckout;
        if (button == FilterSubmoduleUninitialized) return FilterType.SubmoduleUninitialized;
        if (button == FilterSubmoduleConfigIssue) return FilterType.SubmoduleConfigIssue;
        if (button == FilterDetachedHead) return FilterType.DetachedHead;
        if (button == FilterMyRepositories) return FilterType.MyRepositories;
        return FilterType.None;
    }
}



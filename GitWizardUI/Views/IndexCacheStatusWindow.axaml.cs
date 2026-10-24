using Avalonia.Controls;
using Avalonia.Interactivity;
using GitWizard;
using GitWizardUI.Services;
using GitWizardUI.ViewModels;

namespace GitWizardUI.Views;

public partial class IndexCacheStatusWindow : Window
{
    readonly IndexCacheViewModel _viewModel;

    public IndexCacheStatusWindow() : this(new IndexCacheViewModel(
        new AvaloniaUiDispatcher(), new AvaloniaUserDialogs(), new PreparedDiscoveryIndexStore(),
        () => new MftIndexCacheInspection().Capture(GitWizardApi.GetLocalFilesPath()),
        () => MftIndexCacheMaintenance.Clear(GitWizardApi.GetLocalFilesPath())))
    { }

    public IndexCacheStatusWindow(IndexCacheViewModel model)
    {
        InitializeComponent();
        Icon = IconLoader.Load();
        _viewModel = model;
        DataContext = model;
    }

    void RefreshStatus_Click(object? sender, RoutedEventArgs arguments) =>
        _ = _viewModel.RefreshAsync().RunAndLogFaults("Inspecting the index cache");
}

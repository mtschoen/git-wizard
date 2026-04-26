using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using GitWizardAvalonia.Services;
using GitWizardUI.ViewModels;

namespace GitWizardAvalonia.Views;

public partial class MainWindow : Window
{
    readonly MainViewModel _viewModel;

  public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel(new AvaloniaUiDispatcher(), new AvaloniaUserDialogs());
        DataContext = _viewModel;
        if (!OperatingSystem.IsWindows())
            DefenderButton.IsVisible = false;
    }

    void Window_Loaded(object? sender, RoutedEventArgs e)
        => _viewModel.RefreshCommand?.Execute(null);

    void SettingsMenuItem_Click(object? sender, RoutedEventArgs e)
        => new SettingsWindow().ShowDialog(this);

    async void CheckWindowsDefenderMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        if (!OperatingSystem.IsWindows()) return;
        await Task.CompletedTask;
    }

    void ClearCacheMenuItem_Click(object? sender, RoutedEventArgs e)
        => _ = _viewModel.ClearCacheAsync();

    void DeleteAllLocalFilesMenuItem_Click(object? sender, RoutedEventArgs e)
        => _ = _viewModel.DeleteAllLocalFilesAsync();

    void FilterButton_Click(object? sender, RoutedEventArgs e)
        => _viewModel.ApplyFilter((sender as Button)?.Name ?? string.Empty);

    void GroupButton_Click(object? sender, RoutedEventArgs e)
        => _viewModel.ApplyGroup((sender as Button)?.Name ?? string.Empty);

    void SortButton_Click(object? sender, RoutedEventArgs e)
        => _viewModel.ApplySort((sender as Button)?.Name ?? string.Empty);

    void RefreshButton_Click(object? sender, RoutedEventArgs e)
        => _viewModel.RefreshCommand?.Execute(null);

    void FetchAndRefreshButton_Click(object? sender, RoutedEventArgs e)
        => _viewModel.FetchAndRefreshCommand?.Execute(null);

    void ForkButton_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is RepositoryNodeViewModel node)
            _viewModel.OpenInForkCommand?.Execute(node);
    }

    void DeepRefreshButton_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is RepositoryNodeViewModel node)
            _viewModel.DeepRefreshCommand?.Execute(node);
    }
}

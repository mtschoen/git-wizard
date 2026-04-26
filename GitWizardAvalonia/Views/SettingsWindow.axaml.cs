using Avalonia.Controls;
using Avalonia.Interactivity;
using GitWizardAvalonia.Services;
using GitWizardUI.ViewModels;

namespace GitWizardAvalonia.Views;

public partial class SettingsWindow : Window
{
    readonly SettingsViewModel _viewModel;

    public SettingsWindow()
    {
        InitializeComponent();
        _viewModel = new SettingsViewModel(new AvaloniaFolderPicker());
        DataContext = _viewModel;
    }

    async void AddSearchPath_Click(object? sender, RoutedEventArgs e)
        => await _viewModel.AddSearchPathAsync();

    void RemoveSearchPath_Click(object? sender, RoutedEventArgs e)
        => _viewModel.RemoveSelectedSearchPath();

    async void AddIgnoredPath_Click(object? sender, RoutedEventArgs e)
        => await _viewModel.AddIgnoredPathAsync();

    void RemoveIgnoredPath_Click(object? sender, RoutedEventArgs e)
        => _viewModel.RemoveSelectedIgnoredPath();
}

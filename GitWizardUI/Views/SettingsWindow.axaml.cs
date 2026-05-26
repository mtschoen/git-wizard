using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using GitWizardUI.Services;
using GitWizardUI.ViewModels;

namespace GitWizardUI.Views;

public partial class SettingsWindow : Window
{
    readonly SettingsViewModel _viewModel;

    public SettingsWindow()
    {
        InitializeComponent();
        Icon = IconLoader.Load();
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

    // The view models expose a custom ICommand (GitWizardUI.ViewModels.ICommand) that
    // does not satisfy Avalonia's Button.Command (System.Windows.Input.ICommand), so
    // invoke it from a Click handler like the rest of this app. The bound TextBox has
    // already written NewSearch/IgnoredPath by the time the click fires.
    void AddTypedSearchPath_Click(object? sender, RoutedEventArgs e)
        => _viewModel.AddSearchPathCommand.Execute(null);

    void AddTypedIgnoredPath_Click(object? sender, RoutedEventArgs e)
        => _viewModel.AddIgnoredPathCommand.Execute(null);

    // Enter in the typed-path boxes adds the path, matching the retired MAUI Entry's ReturnCommand.
    // Two-way binding has already written NewSearch/IgnoredPath by the time KeyDown fires.
    void SearchPath_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        _viewModel.AddSearchPathCommand.Execute(null);
        e.Handled = true;
    }

    void IgnoredPath_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        _viewModel.AddIgnoredPathCommand.Execute(null);
        e.Handled = true;
    }
}

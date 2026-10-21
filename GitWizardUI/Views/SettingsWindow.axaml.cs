using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using GitWizardUI.Services;
using GitWizardUI.ViewModels;

namespace GitWizardUI.Views;

public partial class SettingsWindow : Window
{
    readonly SettingsViewModel _viewModel;

    public SettingsWindow() : this(null) { }

    public SettingsWindow(MainViewModel? mainViewModel)
    {
        InitializeComponent();
        Icon = IconLoader.Load();
        _viewModel = new SettingsViewModel(new AvaloniaFolderPicker(), mainViewModel, new AvaloniaUiDispatcher());
        DataContext = _viewModel;
    }

    void EnlargeJournal_Click(object? sender, RoutedEventArgs eventArguments)
    {
        if (sender is Button { DataContext: JournalWarningViewModel warning })
        {
            _ = _viewModel.EnlargeJournalAsync(warning).RunAndLogFaults("Enlarging the change journal");
        }
    }

    void IgnoreJournalWarning_Click(object? sender, RoutedEventArgs eventArguments)
    {
        if (sender is Button { DataContext: JournalWarningViewModel warning })
        {
            _ = _viewModel.IgnoreJournalWarningAsync(warning).RunAndLogFaults("Ignoring the journal warning");
        }
    }

    async void AddSearchPath_Click(object? sender, RoutedEventArgs e)
        => await _viewModel.AddSearchPathAsync();

    void RemoveSearchPath_Click(object? sender, RoutedEventArgs e)
        => _viewModel.RemoveSelectedSearchPath();

    async void AddIgnoredPath_Click(object? sender, RoutedEventArgs e)
        => await _viewModel.AddIgnoredPathAsync();

    void RemoveIgnoredPath_Click(object? sender, RoutedEventArgs e)
        => _viewModel.RemoveSelectedIgnoredPath();

    // The bound TextBox has already written NewSearch/IgnoredPath by the time the click fires.
    void AddTypedSearchPath_Click(object? sender, RoutedEventArgs e)
        => _viewModel.AddSearchPath();

    void AddTypedIgnoredPath_Click(object? sender, RoutedEventArgs e)
        => _viewModel.AddIgnoredPath();

    // Enter in the typed-path boxes adds the path, same as the row's Add button.
    // Two-way binding has already written NewSearch/IgnoredPath by the time KeyDown fires.
    void SearchPath_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        _viewModel.AddSearchPath();
        e.Handled = true;
    }

    void IgnoredPath_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        _viewModel.AddIgnoredPath();
        e.Handled = true;
    }

    void ResetToDefaults_Click(object? sender, RoutedEventArgs e)
        => _viewModel.ResetToDefaults();
}

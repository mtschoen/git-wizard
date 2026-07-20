using Avalonia.Controls;
using Avalonia.Interactivity;
using GitWizardUI.Services;
using GitWizardUI.ViewModels;

namespace GitWizardUI.Views;

public partial class ErrorLogWindow : Window
{
    readonly ErrorLogViewModel _viewModel;

    // Parameterless overload so Avalonia's XAML previewer/design-time tooling can instantiate the
    // window (AVLN3001 otherwise); real callers use the injectable constructor below.
    public ErrorLogWindow() : this(new ErrorLogViewModel())
    {
    }

    public ErrorLogWindow(ErrorLogViewModel viewModel)
    {
        InitializeComponent();
        Icon = IconLoader.Load();
        _viewModel = viewModel;
        DataContext = _viewModel;
    }

    void Clear_Click(object? sender, RoutedEventArgs e)
        => _viewModel.Clear();
}

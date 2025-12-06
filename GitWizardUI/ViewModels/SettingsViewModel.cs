using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using GitWizard;
#if WINDOWS
using Microsoft.Maui.Controls;
using Microsoft.Maui.Platform;
#endif

namespace GitWizardUI.ViewModels;

public class SettingsViewModel : INotifyPropertyChanged
{
    private GitWizardConfiguration _configuration;

    public ObservableCollection<string> SearchPaths { get; } = new();
    public ObservableCollection<string> IgnoredPaths { get; } = new();

    private string _newSearchPath = string.Empty;
    public string NewSearchPath
    {
        get => _newSearchPath;
        set
        {
            _newSearchPath = value;
            OnPropertyChanged();
        }
    }

    private string _newIgnoredPath = string.Empty;
    public string NewIgnoredPath
    {
        get => _newIgnoredPath;
        set
        {
            _newIgnoredPath = value;
            OnPropertyChanged();
        }
    }

    public ICommand AddSearchPathCommand { get; }
    public ICommand RemoveSearchPathCommand { get; }
    public ICommand AddIgnoredPathCommand { get; }
    public ICommand RemoveIgnoredPathCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand BrowseSearchPathCommand { get; }
    public ICommand BrowseIgnoredPathCommand { get; }

    public SettingsViewModel()
    {
        _configuration = GitWizardConfiguration.GetGlobalConfiguration();

        // Load current configuration
        foreach (var path in _configuration.SearchPaths)
            SearchPaths.Add(path);

        foreach (var path in _configuration.IgnoredPaths)
            IgnoredPaths.Add(path);

        AddSearchPathCommand = new Command(AddSearchPath);
        RemoveSearchPathCommand = new Command<string>(RemoveSearchPath);
        AddIgnoredPathCommand = new Command(AddIgnoredPath);
        RemoveIgnoredPathCommand = new Command<string>(RemoveIgnoredPath);
        SaveCommand = new Command(Save);
        BrowseSearchPathCommand = new Command(async () => await BrowseSearchPath());
        BrowseIgnoredPathCommand = new Command(async () => await BrowseIgnoredPath());
    }

    private async Task BrowseSearchPath()
    {
        var folder = await PickFolderAsync();
        if (!string.IsNullOrEmpty(folder))
        {
            NewSearchPath = folder;
            AddSearchPath();
        }
    }

    private async Task BrowseIgnoredPath()
    {
        var folder = await PickFolderAsync();
        if (!string.IsNullOrEmpty(folder))
        {
            NewIgnoredPath = folder;
            AddIgnoredPath();
        }
    }

    private async Task<string?> PickFolderAsync()
    {
#if WINDOWS
        var folderPicker = new Windows.Storage.Pickers.FolderPicker();

        // Get the current window handle
        var hwnd = ((MauiWinUIWindow)Application.Current!.Windows[0].Handler!.PlatformView!).WindowHandle;
        WinRT.Interop.InitializeWithWindow.Initialize(folderPicker, hwnd);

        folderPicker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder;
        folderPicker.FileTypeFilter.Add("*");

        var folder = await folderPicker.PickSingleFolderAsync();
        return folder?.Path;
#else
        await Task.CompletedTask;
        return null;
#endif
    }

    private void AddSearchPath()
    {
        if (!string.IsNullOrWhiteSpace(NewSearchPath) && !SearchPaths.Contains(NewSearchPath))
        {
            SearchPaths.Add(NewSearchPath);
            NewSearchPath = string.Empty;
            SaveImmediate();
        }
    }

    private void RemoveSearchPath(string? path)
    {
        if (path != null)
        {
            SearchPaths.Remove(path);
            SaveImmediate();
        }
    }

    private void AddIgnoredPath()
    {
        if (!string.IsNullOrWhiteSpace(NewIgnoredPath) && !IgnoredPaths.Contains(NewIgnoredPath))
        {
            IgnoredPaths.Add(NewIgnoredPath);
            NewIgnoredPath = string.Empty;
            SaveImmediate();
        }
    }

    private void RemoveIgnoredPath(string? path)
    {
        if (path != null)
        {
            IgnoredPaths.Remove(path);
            SaveImmediate();
        }
    }

    public void Save()
    {
        _configuration.SearchPaths.Clear();
        foreach (var path in SearchPaths)
            _configuration.SearchPaths.Add(path);

        _configuration.IgnoredPaths.Clear();
        foreach (var path in IgnoredPaths)
            _configuration.IgnoredPaths.Add(path);

        GitWizardConfiguration.SaveGlobalConfiguration(_configuration);
    }

    private void SaveImmediate()
    {
        Save();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

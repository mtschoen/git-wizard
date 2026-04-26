using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using GitWizard;
using GitWizardUI.ViewModels.Services;

namespace GitWizardUI.ViewModels;

public class SettingsViewModel : INotifyPropertyChanged
{
    readonly IFolderPicker _folderPicker;
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

    private string? _selectedSearchPath;
    public string? SelectedSearchPath
    {
        get => _selectedSearchPath;
        set { _selectedSearchPath = value; OnPropertyChanged(); }
    }

    private string? _selectedIgnoredPath;
    public string? SelectedIgnoredPath
    {
        get => _selectedIgnoredPath;
        set { _selectedIgnoredPath = value; OnPropertyChanged(); }
    }

    public ICommand AddSearchPathCommand { get; }
    public ICommand RemoveSearchPathCommand { get; }
    public ICommand AddIgnoredPathCommand { get; }
    public ICommand RemoveIgnoredPathCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand BrowseSearchPathCommand { get; }
    public ICommand BrowseIgnoredPathCommand { get; }

    public SettingsViewModel(IFolderPicker folderPicker)
    {
        _folderPicker = folderPicker;
        _configuration = GitWizardConfiguration.GetGlobalConfiguration();

        // Load current configuration
        foreach (var path in _configuration.SearchPaths)
            SearchPaths.Add(path);

        foreach (var path in _configuration.IgnoredPaths)
            IgnoredPaths.Add(path);

        AddSearchPathCommand = new RelayCommand(AddSearchPath);
        RemoveSearchPathCommand = new RelayCommand<string>(RemoveSearchPath);
        AddIgnoredPathCommand = new RelayCommand(AddIgnoredPath);
        RemoveIgnoredPathCommand = new RelayCommand<string>(RemoveIgnoredPath);
        SaveCommand = new RelayCommand(Save);
        BrowseSearchPathCommand = new RelayCommand(async () => await BrowseSearchPath());
        BrowseIgnoredPathCommand = new RelayCommand(async () => await BrowseIgnoredPath());
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
        var path = await _folderPicker.PickFolderAsync();
        return path;
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

    public async Task AddSearchPathAsync()
    {
        var path = await _folderPicker.PickFolderAsync();
        if (path is null || string.IsNullOrWhiteSpace(path))
            return;
        if (!SearchPaths.Contains(path))
        {
            SearchPaths.Add(path);
            SaveImmediate();
        }
    }

    public void RemoveSelectedSearchPath()
    {
        if (SelectedSearchPath is not null)
        {
            SearchPaths.Remove(SelectedSearchPath);
            SaveImmediate();
        }
    }

    public async Task AddIgnoredPathAsync()
    {
        var path = await _folderPicker.PickFolderAsync();
        if (path is null || string.IsNullOrWhiteSpace(path))
            return;
        if (!IgnoredPaths.Contains(path))
        {
            IgnoredPaths.Add(path);
            SaveImmediate();
        }
    }

    public void RemoveSelectedIgnoredPath()
    {
        if (SelectedIgnoredPath is not null)
        {
            IgnoredPaths.Remove(SelectedIgnoredPath);
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

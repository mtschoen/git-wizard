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

    private string _forkPath;
    public string ForkPath
    {
        get => _forkPath;
        set
        {
            _forkPath = value;
            OnPropertyChanged();
            SaveImmediate();
        }
    }

    public ICommand AddSearchPathCommand { get; }
    public ICommand RemoveSearchPathCommand { get; }
    public ICommand AddIgnoredPathCommand { get; }
    public ICommand RemoveIgnoredPathCommand { get; }
    public ICommand SaveCommand { get; }

    public SettingsViewModel(IFolderPicker folderPicker)
    {
        _folderPicker = folderPicker;
        _configuration = GitWizardConfiguration.GetGlobalConfiguration();

        // Load current configuration
        foreach (var path in _configuration.SearchPaths)
            SearchPaths.Add(path);

        foreach (var path in _configuration.IgnoredPaths)
            IgnoredPaths.Add(path);

        // Load into the backing field, not the property: the setter calls SaveImmediate(), so
        // assigning ForkPath here would write config.json (a fire-and-forget async save) on every
        // construction — a redundant no-op write of just-loaded data that also races test teardown.
        _forkPath = _configuration.ForkPath ?? string.Empty;

        AddSearchPathCommand = new RelayCommand(AddSearchPath);
        RemoveSearchPathCommand = new RelayCommand<string>(RemoveSearchPath);
        AddIgnoredPathCommand = new RelayCommand(AddIgnoredPath);
        RemoveIgnoredPathCommand = new RelayCommand<string>(RemoveIgnoredPath);
        SaveCommand = new RelayCommand(Save);
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
            await SaveImmediateAsync();
        }
    }

    public void RemoveSelectedSearchPath()
    {
        if (SelectedSearchPath is not null)
        {
            SearchPaths.Remove(SelectedSearchPath);
            _ = SaveImmediateAsync();
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
            await SaveImmediateAsync();
        }
    }

    public void RemoveSelectedIgnoredPath()
    {
        if (SelectedIgnoredPath is not null)
        {
            IgnoredPaths.Remove(SelectedIgnoredPath);
            _ = SaveImmediateAsync();
        }
    }

    public Task SaveAsync()
    {
        _configuration.SearchPaths.Clear();
        foreach (var path in SearchPaths)
            _configuration.SearchPaths.Add(path);

        _configuration.IgnoredPaths.Clear();
        foreach (var path in IgnoredPaths)
            _configuration.IgnoredPaths.Add(path);

        return GitWizardConfiguration.SaveGlobalConfigurationAsync(_configuration);
    }

    public void Save()
    {
        _configuration.SearchPaths.Clear();
        foreach (var path in SearchPaths)
            _configuration.SearchPaths.Add(path);

        _configuration.IgnoredPaths.Clear();
        foreach (var path in IgnoredPaths)
            _configuration.IgnoredPaths.Add(path);

        _configuration.ForkPath = string.IsNullOrWhiteSpace(ForkPath) ? null : ForkPath;

        _ = SaveAsync();
    }

    private void SaveImmediate()
    {
        Save();
    }

    private Task SaveImmediateAsync()
    {
        return SaveAsync();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using GitWizard;
using GitWizardUI.ViewModels.Services;

namespace GitWizardUI.ViewModels;

public partial class SettingsViewModel : INotifyPropertyChanged
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
            Save();
        }
    }

    private bool _skipHiddenDirectories;
    public bool SkipHiddenDirectories
    {
        get => _skipHiddenDirectories;
        set
        {
            _skipHiddenDirectories = value;
            OnPropertyChanged();
            Save();
        }
    }

    private bool _includeTempDirectories;
    public bool IncludeTempDirectories
    {
        get => _includeTempDirectories;
        set
        {
            _includeTempDirectories = value;
            OnPropertyChanged();
            Save();
        }
    }


    public SettingsViewModel(IFolderPicker folderPicker, MainViewModel? mainViewModel, IUiDispatcher dispatcher)
    {
        _folderPicker = folderPicker;
        _mainViewModel = mainViewModel;
        _journalDispatcher = dispatcher;
        _configuration = GitWizardConfiguration.GetGlobalConfiguration();

        JournalWarnings = mainViewModel?.JournalWarnings ?? new();

        foreach (var path in _configuration.SearchPaths)
            SearchPaths.Add(path);

        foreach (var path in _configuration.IgnoredPaths)
            IgnoredPaths.Add(path);

        // Load into the backing field, not the property: the setter calls SaveInternal(), so
        // assigning ForkPath here would write config.json (a fire-and-forget async save) on every
        // construction - a redundant no-op write of just-loaded data that also races test teardown.
        _forkPath = _configuration.ForkPath ?? string.Empty;
        // null in config means "include" (the default), so show unchecked in UI.
        _skipHiddenDirectories = _configuration.SkipHiddenDirectories == true;
        // null in config means "excluded" (the default), so show unchecked in UI.
        _includeTempDirectories = _configuration.IncludeTempDirectories == true;
    }


    public void AddSearchPath()
    {
        if (!string.IsNullOrWhiteSpace(NewSearchPath) && !SearchPaths.Contains(NewSearchPath))
        {
            SearchPaths.Add(NewSearchPath);
            NewSearchPath = string.Empty;
            Save();
        }
    }

    public void AddIgnoredPath()
    {
        if (!string.IsNullOrWhiteSpace(NewIgnoredPath) && !IgnoredPaths.Contains(NewIgnoredPath))
        {
            IgnoredPaths.Add(NewIgnoredPath);
            NewIgnoredPath = string.Empty;
            Save();
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
            await SaveAsync();
        }
    }

    public void RemoveSelectedSearchPath()
    {
        if (SelectedSearchPath is not null)
        {
            SearchPaths.Remove(SelectedSearchPath);
            Save();
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
            await SaveAsync();
        }
    }

    public void RemoveSelectedIgnoredPath()
    {
        if (SelectedIgnoredPath is not null)
        {
            IgnoredPaths.Remove(SelectedIgnoredPath);
            Save();
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

        _configuration.ForkPath = string.IsNullOrWhiteSpace(ForkPath) ? null : ForkPath;
        _configuration.SkipHiddenDirectories = SkipHiddenDirectories;
        _configuration.IncludeTempDirectories = IncludeTempDirectories;

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
        _configuration.SkipHiddenDirectories = SkipHiddenDirectories;
        _configuration.IncludeTempDirectories = IncludeTempDirectories;

        GitWizardConfiguration.SaveGlobalConfiguration(_configuration);
    }

    /// <summary>
    /// Resets search paths, ignored paths, the fork path, and the hidden-directory skip flag to
    /// their first-run clean defaults (<see cref="GitWizardConfiguration.CreateDefaultConfiguration"/>)
    /// and saves immediately.
    /// </summary>
    public void ResetToDefaults()
    {
        var defaults = GitWizardConfiguration.CreateDefaultConfiguration();

        SearchPaths.Clear();
        foreach (var path in defaults.SearchPaths)
            SearchPaths.Add(path);

        IgnoredPaths.Clear();
        foreach (var path in defaults.IgnoredPaths)
            IgnoredPaths.Add(path);

        // Set the backing field directly rather than the ForkPath property: the property setter
        // calls Save() itself, which would save once here and again below.
        _forkPath = string.Empty;
        OnPropertyChanged(nameof(ForkPath));
        _skipHiddenDirectories = defaults.SkipHiddenDirectories == true;
        OnPropertyChanged(nameof(SkipHiddenDirectories));
        _includeTempDirectories = defaults.IncludeTempDirectories == true;
        OnPropertyChanged(nameof(IncludeTempDirectories));

        Save();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

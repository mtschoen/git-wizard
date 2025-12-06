using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using GitWizard;

namespace GitWizardUI.ViewModels;

public class MainViewModel : INotifyPropertyChanged, IUpdateHandler
{
    readonly ConcurrentDictionary<string, RepositoryNodeViewModel> _repositoryMap = new();
    readonly ConcurrentQueue<RepositoryUICommand> _uiCommands = new();
    readonly Stopwatch _stopwatch = new();

    string _headerText = "GitWizard";
    double _progressValue;
    bool _isProgressVisible;
    string _progressText = string.Empty;
    bool _isRefreshing;

    string? _progressDescription;
    int? _progressTotal;
    int? _progressCount;

    public ObservableCollection<RepositoryNodeViewModel> Repositories { get; } = new();

    public string HeaderText
    {
        get => _headerText;
        set
        {
            if (_headerText != value)
            {
                _headerText = value;
                OnPropertyChanged();
            }
        }
    }

    public double ProgressValue
    {
        get => _progressValue;
        set
        {
            if (Math.Abs(_progressValue - value) > 0.001)
            {
                _progressValue = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsProgressVisible
    {
        get => _isProgressVisible;
        set
        {
            if (_isProgressVisible != value)
            {
                _isProgressVisible = value;
                OnPropertyChanged();
            }
        }
    }

    public string ProgressText
    {
        get => _progressText;
        set
        {
            if (_progressText != value)
            {
                _progressText = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsRefreshing
    {
        get => _isRefreshing;
        set
        {
            if (_isRefreshing != value)
            {
                _isRefreshing = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanRefresh));
            }
        }
    }

    public bool CanRefresh => !IsRefreshing;

    public ICommand OpenInExplorerCommand { get; }
    public ICommand OpenInForkCommand { get; }

    public MainViewModel()
    {
        OpenInExplorerCommand = new Command<RepositoryNodeViewModel>(OpenInExplorer);
        OpenInForkCommand = new Command<RepositoryNodeViewModel>(OpenInFork);
        StartUIUpdateThread();
    }

    void OpenInExplorer(RepositoryNodeViewModel? node)
    {
        if (node == null || string.IsNullOrEmpty(node.WorkingDirectory))
            return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = node.WorkingDirectory,
                UseShellExecute = true,
                Verb = "open"
            });
        }
        catch (Exception ex)
        {
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                if (Application.Current?.Windows.Count > 0)
                {
                    await Application.Current.Windows[0].Page?.DisplayAlertAsync("Error",
                        $"Could not open folder: {ex.Message}", "OK")!;
                }
            });
        }
    }

    void OpenInFork(RepositoryNodeViewModel? node)
    {
        if (node == null || string.IsNullOrEmpty(node.WorkingDirectory))
            return;

        if (!Directory.Exists(node.WorkingDirectory))
        {
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                if (Application.Current?.Windows.Count > 0)
                {
                    await Application.Current.Windows[0].Page?.DisplayAlertAsync("Error",
                        "Invalid repository path", "OK")!;
                }
            });
            return;
        }

        // TODO: Make Fork.exe path configurable in settings
        var forkPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Fork", "Fork.exe");

        if (!File.Exists(forkPath))
        {
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                if (Application.Current?.Windows.Count > 0)
                {
                    await Application.Current.Windows[0].Page?.DisplayAlertAsync("Error",
                        $"Fork not found at: {forkPath}\n\nPlease ensure Fork is installed.", "OK")!;
                }
            });
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = forkPath,
                Arguments = $"\"{node.WorkingDirectory}\"",
                UseShellExecute = false,
                CreateNoWindow = false
            });
        }
        catch (Exception ex)
        {
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                if (Application.Current?.Windows.Count > 0)
                {
                    await Application.Current.Windows[0].Page?.DisplayAlertAsync("Error",
                        $"Could not launch Fork: {ex.Message}", "OK")!;
                }
            });
        }
    }

    void StartUIUpdateThread()
    {
        Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(500);

                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    while (_uiCommands.TryDequeue(out var command))
                    {
                        ProcessUICommand(command);
                    }

                    if (_progressCount.HasValue && _progressTotal.HasValue && _progressTotal.Value > 0)
                    {
                        ProgressValue = (double)_progressCount / _progressTotal.Value;
                        ProgressText = $"{_progressDescription} {_progressCount} / {_progressTotal}";
                        IsProgressVisible = true;

                        if (_progressTotal.Value == _progressCount)
                        {
                            IsProgressVisible = false;
                        }
                    }
                });
            }
        });
    }

    void ProcessUICommand(RepositoryUICommand command)
    {
        switch (command.Type)
        {
            case RepositoryUICommandType.RepositoryCreated:
                if (command.Repository != null)
                    AddRepository(command.Repository);
                break;
            case RepositoryUICommandType.SubmoduleCreated:
                if (command.ParentRepository != null && command.Repository != null)
                    AddSubmodule(command.ParentRepository, command.Repository);
                break;
            case RepositoryUICommandType.WorktreeCreated:
                if (command.Repository != null)
                    AddRepository(command.Repository);
                break;
            case RepositoryUICommandType.UninitializedSubmoduleCreated:
                if (command.ParentRepository != null && command.SubmodulePath != null)
                    AddUninitializedSubmodule(command.ParentRepository, command.SubmodulePath);
                break;
            case RepositoryUICommandType.RefreshCompleted:
                if (command.Repository != null)
                    UpdateCompletedRepository(command.Repository);
                break;
        }
    }

    void AddRepository(GitWizardRepository repository)
    {
        var path = repository.WorkingDirectory;
        if (string.IsNullOrEmpty(path))
            return;

        var node = new RepositoryNodeViewModel(repository);
        _repositoryMap[path] = node;
        Repositories.Add(node);
    }

    void AddSubmodule(GitWizardRepository parent, GitWizardRepository submodule)
    {
        var parentPath = parent.WorkingDirectory;
        if (string.IsNullOrEmpty(parentPath) || !_repositoryMap.TryGetValue(parentPath, out var parentNode))
            return;

        var submodulePath = submodule.WorkingDirectory;
        if (string.IsNullOrEmpty(submodulePath))
            return;

        var submoduleNode = new RepositoryNodeViewModel(submodule);
        _repositoryMap[submodulePath] = submoduleNode;
        parentNode.Children.Add(submoduleNode);
    }

    void AddUninitializedSubmodule(GitWizardRepository parent, string submodulePath)
    {
        // For now, we'll skip uninitialized submodules in the tree view
        // Could be added as a special node type in the future
    }

    void UpdateCompletedRepository(GitWizardRepository repository)
    {
        var path = repository.WorkingDirectory;
        if (string.IsNullOrEmpty(path) || !_repositoryMap.TryGetValue(path, out var node))
            return;

        node.Update();
    }

    public async Task RefreshAsync(bool background = false)
    {
        if (IsRefreshing)
            return;

        IsRefreshing = true;
        HeaderText = "Refreshing...";
        Repositories.Clear();
        _repositoryMap.Clear();

        await Task.Run(() =>
        {
            string[]? repositoryPaths = GitWizardApi.GetCachedRepositoryPaths();

            _stopwatch.Restart();
            var configuration = GitWizardConfiguration.GetGlobalConfiguration();
            var report = GitWizardReport.GenerateReport(configuration, repositoryPaths, this);
            _stopwatch.Stop();

            MainThread.BeginInvokeOnMainThread(() =>
            {
                HeaderText = $"Refresh completed in {(float)_stopwatch.ElapsedMilliseconds / 1000:F2} seconds";
            });

            if (repositoryPaths == null)
                GitWizardApi.SaveCachedRepositoryPaths(report.GetRepositoryPaths());

            IsRefreshing = false;
        });
    }

    // IUpdateHandler implementation
    public void SendUpdateMessage(string? message)
    {
        if (message != null)
        {
            MainThread.BeginInvokeOnMainThread(() => HeaderText = message);
        }
    }

    public void OnRepositoryCreated(GitWizardRepository repository)
    {
        _uiCommands.Enqueue(new RepositoryUICommand
        {
            Type = RepositoryUICommandType.RepositoryCreated,
            Repository = repository
        });
    }

    public void StartProgress(string description, int total)
    {
        _progressCount = 0;
        _progressDescription = description;
        _progressTotal = total;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            IsProgressVisible = true;
            ProgressValue = 0;
            ProgressText = $"{description} 0 / {total}";
        });
    }

    public void UpdateProgress(int count)
    {
        _progressCount = count;
    }

    public void OnSubmoduleCreated(GitWizardRepository parent, GitWizardRepository submodule)
    {
        _uiCommands.Enqueue(new RepositoryUICommand
        {
            Type = RepositoryUICommandType.SubmoduleCreated,
            ParentRepository = parent,
            Repository = submodule
        });
    }

    public void OnWorktreeCreated(GitWizardRepository worktree)
    {
        _uiCommands.Enqueue(new RepositoryUICommand
        {
            Type = RepositoryUICommandType.WorktreeCreated,
            Repository = worktree
        });
    }

    public void OnUninitializedSubmoduleCreated(GitWizardRepository parent, string submodulePath)
    {
        _uiCommands.Enqueue(new RepositoryUICommand
        {
            Type = RepositoryUICommandType.UninitializedSubmoduleCreated,
            ParentRepository = parent,
            SubmodulePath = submodulePath
        });
    }

    public void OnRepositoryRefreshCompleted(GitWizardRepository repository)
    {
        _uiCommands.Enqueue(new RepositoryUICommand
        {
            Type = RepositoryUICommandType.RefreshCompleted,
            Repository = repository
        });
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    enum RepositoryUICommandType
    {
        RepositoryCreated,
        SubmoduleCreated,
        WorktreeCreated,
        UninitializedSubmoduleCreated,
        RefreshCompleted
    }

    struct RepositoryUICommand
    {
        public RepositoryUICommandType Type;
        public GitWizardRepository? Repository;
        public GitWizardRepository? ParentRepository;
        public string? SubmodulePath;
    }
}

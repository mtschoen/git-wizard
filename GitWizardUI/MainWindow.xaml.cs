using GitWizard;
using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace GitWizardUI
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : IUpdateHandler
    {
        const int UIRefreshDelayMilliseconds = 500;
        const string ProgressBarFormatString = "{0} {1} / {2}";
        const int BackgroundThreadPoolMultiplier = 1;
        const int ForegroundThreadPoolMultiplier = 100;
        const int CompletionThreadCount = 1000;

        readonly Stopwatch _stopwatch = new();
        readonly ConcurrentQueue<RepositoryUICommand> _uiCommands = new();
        readonly GridLength _progressRowStartHeight;
        readonly ConcurrentDictionary<string, GitWizardTreeViewItem> _treeViewItemMap = new();

        string? _lastMessage;
        GitWizardReport? _report;
        string? _progressDescription;
        int? _progressTotal;
        int? _progressCount;
        SettingsWindow? _settingsWindow;
        FilterType _currentFilter = FilterType.None;

        enum FilterType
        {
            None,
            PendingChanges,
            LocalOnlyCommits,
            SubmoduleCheckout,
            SubmoduleUninitialized,
            SubmoduleConfig,
            DetachedHead
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
            public Repository? Repository;
            public Repository? ParentRepository;
            public string? SubmodulePath;
        }

        public MainWindow()
        {
            InitializeComponent();
            _progressRowStartHeight = ProgressBarRow.Height;
            ProgressBarRow.Height = new GridLength(0);
            TreeView.Items.IsLiveSorting = true;
            TreeView.Items.SortDescriptions.Add(new SortDescription("SortingIndex", ListSortDirection.Ascending));

            new Thread(() =>
            {
                while (true)
                {
                    Dispatcher.Invoke(() =>
                    {
                        Header.Text = _lastMessage;
                        Header.InvalidateVisual();

                        bool itemsChanged = false;

                        while (_uiCommands.TryDequeue(out var command))
                        {
                            ProcessUICommand(command);
                            itemsChanged = true;
                        }

                        if (itemsChanged && _currentFilter != FilterType.None)
                        {
                            ApplyFilter();
                        }

                        if (_progressCount.HasValue && _progressTotal.HasValue)
                        {
                            if (_progressTotal.Value > 0)
                                ProgressBar.Value = (double)_progressCount / _progressTotal.Value;

                            ProgressBarLabel.Content = string.Format(ProgressBarFormatString, _progressDescription, _progressCount, _progressTotal);
                            if (_progressTotal.Value == _progressCount)
                                ProgressBarRow.Height = new GridLength(0);
                        }

                        TreeView.Items.Refresh();
                    });

                    Thread.Sleep(UIRefreshDelayMilliseconds);
                }

                // ReSharper disable once FunctionNeverReturns
            }).Start();
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

        void AddRepository(Repository repository)
        {
            var path = repository.WorkingDirectory;
            if (string.IsNullOrEmpty(path))
                throw new InvalidOperationException("Cannot add repository to UI with null working directory");

            var item = new GitWizardTreeViewItem(repository);
            _treeViewItemMap[path] = item;
            TreeView.Items.Add(item);
        }

        void AddSubmodule(Repository parent, Repository submodule)
        {
            var path = parent.WorkingDirectory;
            if (string.IsNullOrEmpty(path))
                return;  // Parent not ready yet

            if (!_treeViewItemMap.TryGetValue(path, out var item))
                return;  // Parent not added to UI yet

            path = submodule.WorkingDirectory;
            if (string.IsNullOrEmpty(path))
                return;

            var submoduleItem = new GitWizardTreeViewItem(submodule);
            _treeViewItemMap[path] = submoduleItem;
            item.Items.Add(submoduleItem);
        }

        void AddUninitializedSubmodule(Repository parent, string submodulePath)
        {
            var path = parent.WorkingDirectory;
            if (string.IsNullOrEmpty(path))
                return;  // Parent not ready yet

            if (!_treeViewItemMap.TryGetValue(path, out var item))
                return;  // Parent not added to UI yet

            item.Items.Add(new TextBlock { Text = $"{submodulePath}: Uninitialized" });
        }

        void UpdateCompletedRepository(Repository repository)
        {
            var path = repository.WorkingDirectory;
            if (string.IsNullOrEmpty(path))
                return;  // Repository not ready yet

            if (!_treeViewItemMap.TryGetValue(path, out var item))
                return;  // Repository not added to UI yet (created command not processed)

            item.Update();
        }

        void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            Refresh(false);
        }

        void Refresh(bool background)
        {
            Header.Text = "Refreshing...";
            var modifiers = Keyboard.Modifiers;
            _report = null;
            RefreshButton.IsEnabled = false;
            ClearCacheMenuItem.IsEnabled = false;
            DeleteAllLocalFilesMenuItem.IsEnabled = false;

            TreeView.Items.Clear();

            var multiplier = background ? BackgroundThreadPoolMultiplier : ForegroundThreadPoolMultiplier;
            ThreadPool.SetMaxThreads(Environment.ProcessorCount * multiplier, CompletionThreadCount);

            Task.Run(() =>
            {
                string[]? repositoryPaths = null;
                if ((modifiers & ModifierKeys.Shift) == 0)
                    repositoryPaths = GitWizardApi.GetCachedRepositoryPaths();

                _stopwatch.Restart();
                var configuration = GitWizardConfiguration.GetGlobalConfiguration();
                _report = GitWizardReport.GenerateReport(configuration, repositoryPaths, this);

                _stopwatch.Stop();
                _lastMessage = $"Refresh completed in {(float)_stopwatch.ElapsedMilliseconds / 1000} seconds";

                if (repositoryPaths == null)
                    GitWizardApi.SaveCachedRepositoryPaths(_report.GetRepositoryPaths());

                Dispatcher.Invoke(() =>
                {
                    RefreshButton.IsEnabled = true;
                    ClearCacheMenuItem.IsEnabled = true;
                    DeleteAllLocalFilesMenuItem.IsEnabled = true;
                });
            });
        }

        void Window_Loaded(object sender, RoutedEventArgs e)
        {
            Refresh(true);
        }

        void SettingsMenuItem_Click(object sender, RoutedEventArgs eventArgs)
        {
            _settingsWindow ??= new SettingsWindow();
            _settingsWindow.WindowClosed += () => _settingsWindow = null;
            _settingsWindow.Show();
        }

        void CheckWindowsDefenderMenuItem_Click(object sender, RoutedEventArgs eventArgs)
        {
            WindowsDefenderException.AddException();
        }

        void ClearCacheMenuItem_Click(object sender, RoutedEventArgs eventArgs)
        {
            GitWizardApi.ClearCache();
        }

        void DeleteAllLocalFilesMenuItem_Click(object sender, RoutedEventArgs eventArgs)
        {
            GitWizardApi.DeleteAllLocalFiles();
        }

        public void SendUpdateMessage(string? message)
        {
            if (message == null)
            {
                GitWizardLog.LogException(new ArgumentException("Tried to log a null message", nameof(message)));
                return;
            }

            _lastMessage = message;
        }

        public void OnRepositoryCreated(Repository repository)
        {
            if (string.IsNullOrEmpty(repository.WorkingDirectory))
                throw new InvalidOperationException("Repository WorkingDirectory is null or empty.");

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
            Dispatcher.Invoke(() =>
            {
                ProgressBarRow.Height = _progressRowStartHeight;
                ProgressBar.Value = 0;
                ProgressBarLabel.Content = string.Format(ProgressBarFormatString, description, 0, total);
            });
        }

        public void UpdateProgress(int count)
        {
            if (!_progressTotal.HasValue)
                throw new InvalidOperationException("Cannot update ProgressBar without first calling StarProgress");

            _progressCount = count;
        }

        public void OnSubmoduleCreated(Repository parent, Repository submodule)
        {
            if (string.IsNullOrEmpty(parent.WorkingDirectory))
                throw new InvalidOperationException("Parent repository WorkingDirectory is null or empty.");

            if (string.IsNullOrEmpty(submodule.WorkingDirectory))
                throw new InvalidOperationException("Submodule WorkingDirectory is null or empty.");

            _uiCommands.Enqueue(new RepositoryUICommand
            {
                Type = RepositoryUICommandType.SubmoduleCreated,
                ParentRepository = parent,
                Repository = submodule
            });
        }

        public void OnWorktreeCreated(Repository worktree)
        {
            if (string.IsNullOrEmpty(worktree.WorkingDirectory))
                throw new InvalidOperationException("Wortkree WorkingDirectory is null or empty.");

            _uiCommands.Enqueue(new RepositoryUICommand
            {
                Type = RepositoryUICommandType.WorktreeCreated,
                Repository = worktree
            });
        }

        public void OnUninitializedSubmoduleCreated(Repository parent, string submodulePath)
        {
            if (string.IsNullOrEmpty(parent.WorkingDirectory))
                throw new InvalidOperationException("Parent repository WorkingDirectory is null or empty.");

            _uiCommands.Enqueue(new RepositoryUICommand
            {
                Type = RepositoryUICommandType.UninitializedSubmoduleCreated,
                ParentRepository = parent,
                SubmodulePath = submodulePath
            });
        }

        public void OnRepositoryRefreshCompleted(Repository repository)
        {
            if (string.IsNullOrEmpty(repository.WorkingDirectory))
                throw new InvalidOperationException("Repository WorkingDirectory is null or empty.");

            _uiCommands.Enqueue(new RepositoryUICommand
            {
                Type = RepositoryUICommandType.RefreshCompleted,
                Repository = repository
            });
        }

        void FilterButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button)
                return;

            _currentFilter = button.Name switch
            {
                nameof(FilterNone) => FilterType.None,
                nameof(FilterPendingChanges) => FilterType.PendingChanges,
                nameof(FilterLocalOnlyCommits) => FilterType.LocalOnlyCommits,
                nameof(FilterSubmoduleCheckout) => FilterType.SubmoduleCheckout,
                nameof(FilterSubmoduleUninitialized) => FilterType.SubmoduleUninitialized,
                nameof(FilterSubmoduleConfig) => FilterType.SubmoduleConfig,
                nameof(FilterDetachedHead) => FilterType.DetachedHead,
                _ => FilterType.None
            };

            ApplyFilter();
        }

        void ApplyFilter()
        {
            foreach (var item in TreeView.Items)
            {
                if (item is GitWizardTreeViewItem treeViewItem)
                {
                    treeViewItem.Visibility = ShouldShowItem(treeViewItem) ? Visibility.Visible : Visibility.Collapsed;
                }
            }
        }

        bool ShouldShowItem(GitWizardTreeViewItem item)
        {
            var repo = item.Repository;

            return _currentFilter switch
            {
                FilterType.None => true,
                FilterType.PendingChanges => repo.HasPendingChanges,
                FilterType.LocalOnlyCommits => repo.LocalOnlyCommits,
                FilterType.DetachedHead => repo.IsDetachedHead,
                FilterType.SubmoduleCheckout => HasSubmoduleCheckoutIssues(item),
                FilterType.SubmoduleUninitialized => HasUninitializedSubmodules(item),
                FilterType.SubmoduleConfig => HasSubmoduleConfigIssues(item),
                _ => true
            };
        }

        bool HasSubmoduleCheckoutIssues(GitWizardTreeViewItem item)
        {
            // Check if any submodules have pending changes or detached heads
            if (item.Repository.Submodules == null)
                return false;

            foreach (var submodule in item.Repository.Submodules.Values)
            {
                if (submodule != null && (submodule.HasPendingChanges || submodule.IsDetachedHead))
                    return true;
            }

            return false;
        }

        bool HasUninitializedSubmodules(GitWizardTreeViewItem item)
        {
            // Check if there are any uninitialized submodules in the tree view item children
            foreach (var child in item.Items)
            {
                if (child is TextBlock textBlock && textBlock.Text.Contains("Uninitialized"))
                    return true;
            }

            return false;
        }

        bool HasSubmoduleConfigIssues(GitWizardTreeViewItem item)
        {
            // This would require more sophisticated checking of .gitmodules vs index
            // For now, return false as this feature isn't fully implemented
            return false;
        }
    }
}

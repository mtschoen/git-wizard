using GitWizard;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LibGit2Sharp;

namespace GitWizardUI
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : IUpdateHandler
    {
        struct RefreshMessage
        {
            public enum MessageType
            {
                Invalid,
                CreatedRepository,
                CreatedSubmodule,
                CreatedWorktree,
                CreatedUninitializedSubmodule,
                CompletedRepository
            }

            public MessageType Type;

            public Repository Repository;
            public Repository Submodule;
            public string SubmodulePath;
        }

        enum FilterType
        {
            PendingChanges
        }

        const int UIRefreshDelayMilliseconds = 500;
        const string ProgressBarFormatString = "{0} {1} / {2}";
        const int BackgroundThreadPoolMultiplier = 1;
        const int ForegroundThreadPoolMultiplier = 100;
        const int CompletionThreadCount = 1000;

        readonly Stopwatch _stopwatch = new();
        readonly ConcurrentQueue<RefreshMessage> _refreshMessages = new();
        readonly GridLength _progressRowStartHeight;
        readonly ConcurrentDictionary<string, GitWizardTreeViewItem> _treeViewItemMap = new();

        string? _lastMessage;
        GitWizardReport? _report;
        string? _progressDescription;
        int? _progressTotal;
        int? _progressCount;
        SettingsWindow? _settingsWindow;
        readonly HashSet<FilterType> _filterTypes = new();

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

                        while (_refreshMessages.TryDequeue(out var message))
                        {
                            var repository = message.Repository;
                            switch (message.Type)
                            {
                                case RefreshMessage.MessageType.Invalid:
                                    throw new ArgumentException("Cannot process invalid message");
                                case RefreshMessage.MessageType.CreatedRepository:
                                    AddRepository(repository);
                                    break;
                                case RefreshMessage.MessageType.CreatedSubmodule:
                                    AddSubmodule(repository, message.Submodule);
                                    break;
                                case RefreshMessage.MessageType.CreatedWorktree:
                                    AddRepository(repository);
                                    break;
                                case RefreshMessage.MessageType.CreatedUninitializedSubmodule:
                                    AddUninitializedSubmodule(repository, message.SubmodulePath);
                                    break;
                                case RefreshMessage.MessageType.CompletedRepository:
                                    UpdateCompletedRepository(repository);
                                    break;
                                default:
                                    throw new ArgumentOutOfRangeException();
                            }
                        }

                        if (_progressCount.HasValue && _progressTotal.HasValue)
                        {
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
                throw new InvalidOperationException("Cannot add submodule to UI under parent with null working directory");

            if (!_treeViewItemMap.TryGetValue(path, out var item))
                throw new InvalidOperationException("Cannot find tree view item to add submodule");

            path = submodule.WorkingDirectory;
            if (string.IsNullOrEmpty(path))
                throw new InvalidOperationException("Cannot add repository to UI with null working directory");

            var submoduleItem = new GitWizardTreeViewItem(submodule);
            _treeViewItemMap[path] = submoduleItem;
            item.Items.Add(submoduleItem);
        }

        void AddUninitializedSubmodule(Repository parent, string submodulePath)
        {
            var path = parent.WorkingDirectory;
            if (string.IsNullOrEmpty(path))
                throw new InvalidOperationException("Cannot add submodule to UI under parent with null working directory");

            if (!_treeViewItemMap.TryGetValue(path, out var item))
                throw new InvalidOperationException("Cannot find tree view item to add submodule");


            item.Items.Add(new TextBlock { Text = $"{submodulePath}: Uninitialized" });
        }

        void UpdateCompletedRepository(Repository repository)
        {
            var path = repository.WorkingDirectory;
            if (string.IsNullOrEmpty(path))
                throw new InvalidOperationException("Cannot update UI for repository with null working directory");

            if (!_treeViewItemMap.TryGetValue(path, out var item))
                throw new InvalidOperationException($"Cannot find tree view item for repository at path {path}");

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
            _refreshMessages.Enqueue(new RefreshMessage
            {
                Type = RefreshMessage.MessageType.CreatedRepository,
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
            _refreshMessages.Enqueue(new RefreshMessage
            {
                Type = RefreshMessage.MessageType.CreatedSubmodule,
                Repository = parent,
                Submodule =  submodule
            });
        }

        public void OnWorktreeCreated(Repository worktree)
        {
            _refreshMessages.Enqueue(new RefreshMessage
            {
                Type = RefreshMessage.MessageType.CreatedWorktree,
                Repository = worktree
            });
        }

        public void OnUninitializedSubmoduleCreated(Repository parent, string submodulePath)
        {
            _refreshMessages.Enqueue(new RefreshMessage
            {
                Type = RefreshMessage.MessageType.CreatedUninitializedSubmodule,
                Repository = parent,
                SubmodulePath = submodulePath
            });
        }

        public void OnRepositoryRefreshCompleted(Repository repository)
        {
            _refreshMessages.Enqueue(new RefreshMessage
            {
                Type = RefreshMessage.MessageType.CompletedRepository,
                Repository = repository
            });
        }

        void PendingChangesFilterButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleFilter(FilterType.PendingChanges);
            ((Button)sender).IsPressed = HasFilter(FilterType.PendingChanges);
        }

        bool HasFilter(FilterType type)
        {
            return _filterTypes.Contains(type);
        }

        void ToggleFilter(FilterType type)
        {
            if (_filterTypes.Remove(type))
            {
                RefreshFilters();
                return;
            }

            _filterTypes.Add(type);
            RefreshFilters();
        }

        void RefreshFilters()
        {
            var items = TreeView.Items;
            if (_filterTypes.Count == 0)
            {
                items.IsLiveFiltering = false;
                items.Filter = null;
                items.Refresh();
                return;
            }

            items.IsLiveFiltering = true;
            items.Filter = TreeViewItemsFilter;
            items.Refresh();
        }

        bool TreeViewItemsFilter(object obj)
        {
            var item = (GitWizardTreeViewItem)obj;
            foreach (var filterType in _filterTypes)
            {
                switch (filterType)
                {
                    case FilterType.PendingChanges:
                        if (item.Repository.IsDirty)
                            return true;

                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
        }
    }
}

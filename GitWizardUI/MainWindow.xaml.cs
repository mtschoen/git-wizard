using GitWizard;
using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
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

        readonly GitWizardConfiguration _configuration;
        readonly Stopwatch _stopwatch = new();
        readonly ConcurrentQueue<Repository> _createdRepositories = new();
        readonly ConcurrentQueue<(Repository, Repository)> _createdSubmodules = new();
        readonly ConcurrentQueue<(Repository, string)> _createdUninitializedSubmodules = new();
        readonly ConcurrentQueue<Repository> _completedRepositories = new();
        readonly GridLength _progressRowStartHeight;
        readonly ConcurrentDictionary<string, GitWizardTreeViewItem> _treeViewItemMap = new();

        string? _lastMessage;
        GitWizardReport? _report;
        string? _progressDescription;
        int? _progressTotal;
        int? _progressCount;

        public MainWindow()
        {
            InitializeComponent();
            _configuration = GitWizardConfiguration.GetGlobalConfiguration();
            SearchList.ItemsSource = _configuration.SearchPaths;
            IgnoredList.ItemsSource = _configuration.IgnoredPaths;
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

                        var items = TreeView.Items;
                        while (_createdRepositories.TryDequeue(out var repository))
                        {
                            var path = repository.WorkingDirectory;
                            if (string.IsNullOrEmpty(path))
                                throw new InvalidOperationException("Cannot add repository to UI with null working directory");

                            var item = new GitWizardTreeViewItem(repository);
                            _treeViewItemMap[path] = item;
                            items.Add(item);
                        }

                        while (_createdSubmodules.TryDequeue(out var update))
                        {
                            var (parent, submodule) = update;
                            AddSubmodule(parent, submodule);
                        }

                        while (_createdUninitializedSubmodules.TryDequeue(out var update))
                        {
                            var (parent, submodulePath) = update;
                            AddUninitializedSubmodule(parent, submodulePath);
                        }

                        while (_completedRepositories.TryDequeue(out var repository))
                        {
                            UpdateCompletedRepository(repository);
                        }

                        if (_progressCount.HasValue && _progressTotal.HasValue)
                        {
                            ProgressBar.Value = (double)_progressCount / _progressTotal.Value;
                            ProgressBarLabel.Content = string.Format(ProgressBarFormatString, _progressDescription, _progressCount, _progressTotal);
                            if (_progressTotal.Value == _progressCount)
                                ProgressBarRow.Height = new GridLength(0);
                        }

                        items.Refresh();
                    });

                    Thread.Sleep(UIRefreshDelayMilliseconds);
                }

                // ReSharper disable once FunctionNeverReturns
            }).Start();
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
                throw new InvalidOperationException("Cannot add submodule to UI under parent with null working directory");

            if (!_treeViewItemMap.TryGetValue(path, out var item))
                return;

            item.Update();
        }

        void BrowseSearchPathButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new FolderBrowserDialog();
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                var selectedPath = dialog.SelectedPath;
                SearchPathTextBox.Text = selectedPath;
                AddSearchPath(selectedPath);
            }
        }

        void AddSearchPathButton_Click(object sender, RoutedEventArgs e)
        {
            AddSearchPath(SearchPathTextBox.Text);
        }

        void AddSearchPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            _configuration.SearchPaths.Add(path);
            _configuration.Save(GitWizardConfiguration.GetGlobalConfigurationPath());
            SearchList.Items.Refresh();
        }

        void BrowseIgnoredPathButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new FolderBrowserDialog();
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                var selectedPath = dialog.SelectedPath;
                IgnoredPathTextBox.Text = selectedPath;
                AddIgnoredPath(selectedPath);
            }
        }

        void AddIgnoredPathButton_Click(object sender, RoutedEventArgs e)
        {
            AddIgnoredPath(IgnoredPathTextBox.Text);
        }

        void AddIgnoredPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            _configuration.IgnoredPaths.Add(path);
            _configuration.Save(GitWizardConfiguration.GetGlobalConfigurationPath());
            IgnoredList.Items.Refresh();
        }
        void IgnoredList_KeyUp(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != Key.Delete)
                return;

            _configuration.IgnoredPaths.Remove((string)IgnoredList.SelectedValue);
            IgnoredList.Items.Refresh();
            _configuration.Save(GitWizardConfiguration.GetGlobalConfigurationPath());
        }

        void SearchList_KeyUp(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != Key.Delete)
                return;

            _configuration.SearchPaths.Remove((string)SearchList.SelectedValue);
            _configuration.Save(GitWizardConfiguration.GetGlobalConfigurationPath());
            SearchList.Items.Refresh();
        }

        void IgnoredPathTextBox_KeyUp(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
                return;

            AddIgnoredPath(IgnoredPathTextBox.Text);
        }

        void SearchPathTextBox_KeyUp(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
                return;

            AddSearchPath(SearchPathTextBox.Text);
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
                _report = GitWizardReport.GenerateReport(_configuration, repositoryPaths, this);

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

        void CheckWindowsDefenderMenuItem_Click(object sender, RoutedEventArgs e)
        {
            WindowsDefenderException.AddException();
        }

        void ClearCacheMenuItem_Click(object sender, RoutedEventArgs e)
        {
            GitWizardApi.ClearCache();
        }

        void DeleteAllLocalFilesMenuItem_Click(object sender, RoutedEventArgs e)
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
            _createdRepositories.Enqueue(repository);
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
            _createdSubmodules.Enqueue((parent, submodule));
        }

        public void OnUninitializedSubmoduleCreated(Repository parent, string submodulePath)
        {
            _createdUninitializedSubmodules.Enqueue((parent, submodulePath));
        }

        public void OnRepositoryRefreshCompleted(Repository repository)
        {
            _completedRepositories.Enqueue(repository);
        }
    }
}

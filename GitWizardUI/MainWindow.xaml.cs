using GitWizard;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Input;
using CheckBox = System.Windows.Controls.CheckBox;
using Orientation = System.Windows.Controls.Orientation;

namespace GitWizardUI
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : IUpdateHandler
    {
        const int UIRefreshDelayMilliseconds = 500;
        const string ProgressBarFormatString = "{0} {1} / {2}";

        readonly GitWizardConfiguration _configuration;
        readonly Stopwatch _stopwatch = new();
        readonly ConcurrentQueue<Repository> _createdRepositories = new();
        readonly GridLength _progressRowStartHeight;

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
                            items.Add(CreateTreeViewItem(repository));
                        }

                        if (_progressCount.HasValue && _progressTotal.HasValue)
                        {

                            ProgressBar.Value = (double)_progressCount / _progressTotal.Value;
                            ProgressBarLabel.Content = string.Format(ProgressBarFormatString, _progressDescription, _progressCount, _progressTotal);
                            if (_progressTotal.Value == _progressCount)
                                ProgressBarRow.Height = new GridLength(0);
                        }
                    });

                    Thread.Sleep(UIRefreshDelayMilliseconds);
                }

                // ReSharper disable once FunctionNeverReturns
            }).Start();
        }

        static TreeViewItem CreateTreeViewItem(Repository repository)
        {
            var item = new TreeViewItem();
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            item.Header = panel;
            panel.Children.Add(new TextBlock { Text = repository.WorkingDirectory });
            panel.Children.Add(new CheckBox { IsChecked = !repository.IsRefreshing, IsEnabled = false });

            if (repository.Submodules != null)
            {
                var items = item.Items;
                foreach (var kvp in repository.Submodules)
                {
                    var submodule = kvp.Value;
                    if (submodule == null)
                    {
                        items.Add(new TextBlock {Text = $"{kvp.Key}: Uninitialized"});
                        continue;
                    }

                    items.Add(CreateTreeViewItem(submodule));
                }
            }
            
            return item;
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
            SearchList.Items.Refresh();
            _configuration.Save(GitWizardConfiguration.GetGlobalConfigurationPath());
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
            DoRefresh();
        }

        void DoRefresh()
        {
            Header.Text = "Refreshing...";
            var modifiers = Keyboard.Modifiers;
            _report = null;
            RefreshButton.IsEnabled = false;

            TreeView.Items.Clear();

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

                Dispatcher.Invoke(() => { RefreshButton.IsEnabled = true; });
            });
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

        void Window_Loaded(object sender, RoutedEventArgs e)
        {
            DoRefresh();
        }
    }
}

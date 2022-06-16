using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using GitWizard;
using System.Windows.Input;
using CheckBox = System.Windows.Controls.CheckBox;
using Orientation = System.Windows.Controls.Orientation;

namespace GitWizardUI
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow
    {
        const int UIRefreshDelayMilliseconds = 500;
        readonly GitWizardConfiguration _configuration;
        string? _lastMessage;
        readonly Stack<Task> _backgroundTasks = new();
        readonly Stopwatch _stopwatch = new();
        GitWizardReport? _report;

        public MainWindow()
        {
            InitializeComponent();
            _configuration = GitWizardConfiguration.GetGlobalConfiguration();
            SearchList.ItemsSource = _configuration.SearchPaths;
            IgnoredList.ItemsSource = _configuration.IgnoredPaths;

            new Thread(() =>
            {
                while (true)
                {
                    Dispatcher.Invoke(() =>
                    {
                        Header.Text = _lastMessage;
                        Header.InvalidateVisual();

                        if (_report != null)
                        {
                            var items = TreeView.Items;
                            items.Clear();

                            Dictionary<string, GitWizardReport.Repository> repositories;
                            var reportRepositories = _report.Repositories;
                            lock (reportRepositories)
                            {
                                repositories =
                                    new Dictionary<string, GitWizardReport.Repository>(reportRepositories);
                            }

                            foreach (var kvp in repositories)
                            {
                                items.Add(CreateTreeViewItem(kvp.Value));
                            }
                        }
                    });

                    Thread.Sleep(UIRefreshDelayMilliseconds);
                }
            }).Start();
        }

        TreeViewItem CreateTreeViewItem(GitWizardReport.Repository repository)
        {
            var item = new TreeViewItem();
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            item.Header = panel;
            panel.Children.Add(new TextBlock { Text = repository.WorkingDirectory });
            panel.Children.Add(new CheckBox
                { IsChecked = !repository.IsRefreshing, IsEnabled = false });

            if (repository.Submodules != null)
            {
                var items = item.Items;
                foreach (var kvp in repository.Submodules)
                {
                    items.Add(CreateTreeViewItem(kvp.Value));
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
            Header.Text = "Refreshing...";
            var modifiers = Keyboard.Modifiers;
            _report = null;
            RefreshButton.IsEnabled = false;
            Task.Run(() =>
            {
                string[]? repositoryPaths = null;
                if ((modifiers & ModifierKeys.Shift) == 0)
                    repositoryPaths = GitWizardApi.GetCachedRepositoryPaths();

                _stopwatch.Restart();
                _report = GitWizardReport.GenerateReport(_configuration, repositoryPaths,
                    message => _lastMessage = message);

                _stopwatch.Stop();
                _lastMessage = $"Refresh completed in {(float)_stopwatch.ElapsedMilliseconds / 1000} seconds";

                if (repositoryPaths == null)
                    GitWizardApi.SaveCachedRepositoryPaths(_report.GetRepositoryPaths());

                Dispatcher.Invoke(() =>
                {
                    RefreshButton.IsEnabled = true;
                });
            });
        }
    }
}

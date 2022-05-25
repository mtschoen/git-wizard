using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using GitWizard;
using System.Windows.Input;

namespace GitWizardUI
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow
    {
        readonly GitWizardConfiguration _configuration;

        readonly Stopwatch _stopwatch = new();

        public MainWindow()
        {
            InitializeComponent();
            _configuration = GitWizardConfiguration.GetGlobalConfiguration();
            SearchList.ItemsSource = _configuration.SearchPaths;
            IgnoredList.ItemsSource = _configuration.IgnoredPaths;
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
            Task.Run(() =>
            {
                string[]? repositoryPaths = null;
                if ((modifiers & ModifierKeys.Shift) == 0)
                    repositoryPaths = GitWizardApi.GetCachedRepositoryPaths();

                _stopwatch.Restart();
                var report = GitWizardReport.GenerateReport(_configuration, repositoryPaths, path =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        Header.Text = path;
                        Header.InvalidateVisual();
                    });
                });

                _stopwatch.Stop();
                Dispatcher.Invoke(() =>
                {
                    Header.Text = $"Refresh completed in {(float)_stopwatch.ElapsedMilliseconds / 1000} seconds";
                });

                if (repositoryPaths == null)
                    GitWizardApi.SaveCachedRepositoryPaths(report.GetRepositoryPaths());
            });
        }
    }
}

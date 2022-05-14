using System.Windows;
using System.Windows.Forms;
using GitWizard;
using System.Windows.Input;

namespace GitWizardUI
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        GitWizardConfig config;

        public MainWindow()
        {
            InitializeComponent();
            config = GitWizardConfig.GetGlobalConfig();
            SearchList.ItemsSource = config.SearchPaths;
            IgnoredList.ItemsSource = config.IgnoredPaths;
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

            config.SearchPaths.Add(path);
            config.Save(GitWizardConfig.GetGlobalConfigPath());
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

            config.IgnoredPaths.Add(path);
            config.Save(GitWizardConfig.GetGlobalConfigPath());
            IgnoredList.Items.Refresh();
        }

        void IgnoredList_KeyUp(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != Key.Delete)
                return;

            config.IgnoredPaths.Remove((string)IgnoredList.SelectedValue);
            IgnoredList.Items.Refresh();
            config.Save(GitWizardConfig.GetGlobalConfigPath());
        }

        void SearchList_KeyUp(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != Key.Delete)
                return;

            config.SearchPaths.Remove((string)SearchList.SelectedValue);
            SearchList.Items.Refresh();
            config.Save(GitWizardConfig.GetGlobalConfigPath());
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
            var report = GitWizardReport.GenerateReport(config, (path) =>
            {
                Dispatcher.Invoke(() =>
                {
                    Header.Text = path;
                    Header.InvalidateVisual();
                });
            });
        }
    }
}

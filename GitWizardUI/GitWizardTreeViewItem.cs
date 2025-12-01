using GitWizard;
using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace GitWizardUI
{
    class GitWizardTreeViewItem : TreeViewItem
    {
        public readonly Repository Repository;
        readonly CheckBox _checkBox;
        readonly TextBlock _textBlock;
        readonly Button _forkButton;

        public string? SortingIndex => Repository.WorkingDirectory;

        public GitWizardTreeViewItem(Repository repository)
        {
            Repository = repository;
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            Header = panel;
            _checkBox = new CheckBox { IsEnabled = false };
            panel.Children.Add(_checkBox);
            _textBlock = new TextBlock { Margin = new Thickness(5, 0, 10, 0) };
            panel.Children.Add(_textBlock);

            _forkButton = new Button
            {
                Content = "Open in Fork",
                Padding = new Thickness(5, 0, 5, 0),
                Margin = new Thickness(0, 0, 5, 0)
            };
            _forkButton.Click += ForkButton_Click;
            panel.Children.Add(_forkButton);

            // Update to sync with current repository state
            Update();
        }

        void ForkButton_Click(object sender, RoutedEventArgs e)
        {
            var workingDirectory = Repository.WorkingDirectory;
            if (string.IsNullOrEmpty(workingDirectory) || !Directory.Exists(workingDirectory))
            {
                MessageBox.Show("Invalid repository path", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // TODO: Make Fork.exe path configurable in settings
            var forkPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Fork", "Fork.exe");

            if (!File.Exists(forkPath))
            {
                MessageBox.Show($"Fork not found at: {forkPath}\n\nPlease ensure Fork is installed.",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = forkPath,
                    Arguments = $"\"{workingDirectory}\"",
                    UseShellExecute = false,
                    CreateNoWindow = false
                };

                Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not launch Fork: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public void Update()
        {
            _checkBox.IsChecked = !Repository.IsRefreshing;
            UpdateLabel();
        }

        void UpdateLabel()
        {
            var pendingChanges = Repository.HasPendingChanges;
            var localOnlyCommits = Repository.LocalOnlyCommits;
            string label = Repository.WorkingDirectory ?? "Invalid";
            if (pendingChanges)
            {
                label += $" * ({Repository.NumberOfPendingChanges})";
            }

            if (localOnlyCommits)
            {
                label += " \u2191"; // Up arrow symbol to indicate unpushed/untracked changes
            }

            _textBlock.Text = label;
        }
    }
}

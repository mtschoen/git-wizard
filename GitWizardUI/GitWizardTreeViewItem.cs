using GitWizard;
using System.Windows.Controls;

namespace GitWizardUI
{
    class GitWizardTreeViewItem : TreeViewItem
    {
        public readonly Repository Repository;
        readonly CheckBox _checkBox;
        readonly TextBlock _textBlock;

        public string? SortingIndex => Repository.WorkingDirectory;

        public GitWizardTreeViewItem(Repository repository)
        {
            Repository = repository;
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            Header = panel;
            _checkBox = new CheckBox { IsChecked = !repository.IsRefreshing, IsEnabled = false };
            panel.Children.Add(_checkBox);
            _textBlock = new TextBlock();
            UpdateLabel();
            panel.Children.Add(_textBlock);
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
                label += $" * ({Repository.NumberOfPendingChanges}";
            }

            if (localOnlyCommits)
            {
                label += " u";
            }

            _textBlock.Text = label;
        }
    }
}

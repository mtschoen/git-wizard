using GitWizard;
using System.Windows.Controls;

namespace GitWizardUI
{
    class GitWizardTreeViewItem : TreeViewItem
    {
        public readonly Repository Repository;
        readonly CheckBox _checkBox;

        public string? SortingIndex => Repository.WorkingDirectory;

        public GitWizardTreeViewItem(Repository repository)
        {
            Repository = repository;
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            Header = panel;
            _checkBox = new CheckBox { IsChecked = !repository.IsRefreshing, IsEnabled = false };
            panel.Children.Add(_checkBox);
            panel.Children.Add(new TextBlock { Text = repository.WorkingDirectory });
        }

        public void Update()
        {
            _checkBox.IsChecked = !Repository.IsRefreshing;
        }
    }
}

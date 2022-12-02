using GitWizard;
using System.Windows.Controls;

namespace GitWizardUI
{
    class GitWizardTreeViewItem : TreeViewItem
    {
        public readonly Repository Repository;
        readonly CheckBox _checkBox;

        public GitWizardTreeViewItem(Repository repository)
        {
            Repository = repository;
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            Header = panel;
            panel.Children.Add(new TextBlock { Text = repository.WorkingDirectory });
            _checkBox = new CheckBox { IsChecked = !repository.IsRefreshing, IsEnabled = false };
            panel.Children.Add(_checkBox);
        }

        public void Update()
        {
            if (Repository.IsRefreshing)
                GitWizardLog.Log(Repository.WorkingDirectory + " is still refreshing??");

            _checkBox.IsChecked = !Repository.IsRefreshing;
        }
    }
}

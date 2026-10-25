using System.Collections.ObjectModel;
using System.Collections.Specialized;
using GitWizardUI.ViewModels;

namespace GitWizardTests;

// Counts Add notifications that land before the tail: an ObservableCollection shifts every following
// element on such an insert, while tail appends never do.
sealed class PositionalInsertRecorder
{
    public int Count { get; private set; }

    public PositionalInsertRecorder(ObservableCollection<RepositoryNodeViewModel> nodes) =>
        nodes.CollectionChanged += (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Add
                && args.NewStartingIndex + (args.NewItems?.Count ?? 0) != nodes.Count)
            {
                Count++;
            }
        };
}

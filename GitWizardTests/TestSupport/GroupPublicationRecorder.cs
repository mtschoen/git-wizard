using System.Collections.ObjectModel;
using System.Collections.Specialized;
using GitWizardUI.ViewModels;

namespace GitWizardTests;

// Observes a displayed collection that receives group headers: records how many children each header
// holds when it is published and every later change to a published header's Children.
sealed class GroupPublicationRecorder
{
    public List<int> PublishedHeaderChildCounts { get; } = [];
    public int ChildMutationsAfterPublication { get; private set; }
    public int PositionalInserts { get; private set; }

    public GroupPublicationRecorder(ObservableCollection<RepositoryNodeViewModel> displayed) =>
        displayed.CollectionChanged += (_, args) =>
        {
            if (args.Action != NotifyCollectionChangedAction.Add || args.NewItems == null)
            {
                return;
            }

            if (args.NewStartingIndex + args.NewItems.Count != displayed.Count)
            {
                PositionalInserts++;
            }

            foreach (RepositoryNodeViewModel node in args.NewItems)
            {
                if (!node.IsGroupHeader)
                {
                    continue;
                }

                PublishedHeaderChildCounts.Add(node.Children.Count);
                node.Children.CollectionChanged += (_, _) => ChildMutationsAfterPublication++;
            }
        };
}

using GitWizardUI.ViewModels;

namespace GitWizardTests;

// Observes every replacement of MainViewModel.Repositories: the row count each replacement holds
// when published and any change made to a replacement afterwards.
sealed class CollectionReplacementRecorder
{
    public List<int> ReplacedCollectionRowCounts { get; } = [];
    public int MutationsAfterReplacement { get; private set; }

    public CollectionReplacementRecorder(MainViewModel viewModel) =>
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(MainViewModel.Repositories))
                return;
            var published = viewModel.Repositories;
            ReplacedCollectionRowCounts.Add(published.Count);
            published.CollectionChanged += (_, _) => MutationsAfterReplacement++;
        };
}

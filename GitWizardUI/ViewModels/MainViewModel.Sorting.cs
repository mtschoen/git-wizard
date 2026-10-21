using System.Collections.ObjectModel;

namespace GitWizardUI.ViewModels;

public partial class MainViewModel
{
    // Working Directory sort must order by path explicitly. Discovered repository paths are
    // cached lowercase while paths created during a Live watch arrive with OS-native
    // drive-letter/segment case (issue #240's evidence), so the comparison must be
    // case-insensitive on Windows to avoid interleaving/splitting what is the same path.
    // Non-Windows platforms keep ordinal comparison, since their filesystems are case-sensitive.
    static readonly StringComparer WorkingDirectoryComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    int CompareWorkingDirectory(string a, string b) => WorkingDirectoryComparer.Compare(a, b);

    // Inserts node at its sorted position when SortMode.WorkingDirectory is active (so a single
    // Live arrival lands in path order without a full rebuild); otherwise appends, matching
    // every other sort mode's arrival-order behavior between rebuilds. Returns the index actually
    // inserted at so a caller that must also mirror the insertion into an expanded group's flat
    // display rows can locate it without a second scan.
    //
    // This is the per-row path: it costs one binary search (O(log n) comparisons) plus the
    // underlying ObservableCollection's O(n) shift, which is fine for rare single arrivals (an
    // isolated Live-watch event outside a refresh). During a refresh, arrivals are appended in
    // arrival order (O(1) per row with zero positional inserts) and the entire collection is sorted
    // once when the refresh completes (CompleteRefresh / ApplyFilterAndGrouping).
    int InsertRepositoryNode(ObservableCollection<RepositoryNodeViewModel> nodes, RepositoryNodeViewModel node)
    {
        var index = _activeSortMode == SortMode.WorkingDirectory
            ? FindSortedInsertionIndex(nodes, node.WorkingDirectory)
            : nodes.Count;

        nodes.Insert(index, node);
        return index;
    }

    // Binary search for the upper-bound insertion index: the first position whose existing entry
    // sorts strictly after workingDirectory. Equal-key entries (arrival order between repositories
    // that briefly share a display path) end up before the new node, matching the linear scan this
    // replaced. startIndex bounds the search from below when merging a pre-sorted batch.
    int FindSortedInsertionIndex(ObservableCollection<RepositoryNodeViewModel> nodes, string workingDirectory, int startIndex = 0)
    {
        var low = Math.Clamp(startIndex, 0, nodes.Count);
        var high = nodes.Count;
        while (low < high)
        {
            var mid = low + (high - low) / 2;
            if (CompareWorkingDirectory(nodes[mid].WorkingDirectory, workingDirectory) <= 0)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    // Sorts a whole batch once for a bulk-append caller (PrePopulateFromReport), instead of
    // inserting each row with InsertRepositoryNode. Only WorkingDirectory sort actually reorders
    // the batch; every other mode's between-rebuild contract is arrival order, matching
    // InsertRepositoryNode's own append fallback. OrderBy is a stable sort, so arrival order is
    // preserved among any equal-path entries, same as the per-row path.
    List<RepositoryNodeViewModel> SortBatchForDisplay(List<RepositoryNodeViewModel> batch) =>
        _activeSortMode == SortMode.WorkingDirectory
            ? batch.OrderBy(node => node.WorkingDirectory, Comparer<string>.Create(CompareWorkingDirectory)).ToList()
            : batch;

    // Merges a batch of nodes into an already populated collection in sorted order. Used outside
    // a refresh when merging pre-sorted batches (e.g. grouped pre-population or test callers).
    // During an active refresh, drains append rows in arrival order without positional inserts,
    // deferring sorting to the single end-of-refresh collection rebuild.
    void MergeBatchIntoDisplay(ObservableCollection<RepositoryNodeViewModel> nodes, List<RepositoryNodeViewModel> batch)
    {
        if (batch.Count == 0)
        {
            return;
        }

        if (nodes.Count == 0)
        {
            foreach (var node in SortBatchForDisplay(batch))
            {
                nodes.Add(node);
            }

            return;
        }

        if (_activeSortMode != SortMode.WorkingDirectory)
        {
            foreach (var node in batch)
            {
                nodes.Add(node);
            }

            return;
        }

        var sortedBatch = SortBatchForDisplay(batch);
        var searchStart = 0;
        foreach (var node in sortedBatch)
        {
            var index = FindSortedInsertionIndex(nodes, node.WorkingDirectory, searchStart);
            if (index == nodes.Count)
            {
                nodes.Add(node);
            }
            else
            {
                nodes.Insert(index, node);
            }

            searchStart = index + 1;
        }
    }
}

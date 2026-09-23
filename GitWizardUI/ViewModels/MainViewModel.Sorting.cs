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

    int _workingDirectoryComparisonCount;
    int _positionalInsertCount;

    // Test-only instrumentation seams (pr-crew review of PR #241/#243): count every
    // WorkingDirectory comparison and every positional (non-tail) collection insert, whichever
    // path performs them. Production code never reads either; they exist so a test can assert a
    // batch actually took a bulk path (one sort/merge plus tail-only appends) rather than the old
    // per-row scan-and-shift, without asserting on wall-clock time or subscribing to
    // CollectionChanged on a header that does not exist yet.
    internal int WorkingDirectoryComparisonCount => _workingDirectoryComparisonCount;
    internal int PositionalInsertCount => _positionalInsertCount;

    int CompareWorkingDirectory(string a, string b)
    {
        _workingDirectoryComparisonCount++;
        return WorkingDirectoryComparer.Compare(a, b);
    }

    // Inserts node into an ObservableCollection at index, counting it as a "positional" insert
    // (round-2 pr-crew review of PR #241/#243) whenever index is not the tail: an
    // ObservableCollection is array-backed, so a mid-list Insert shifts every following element,
    // while inserting at Count never does (it just appends). The counter distinguishes the two so
    // a test can see the shift cost a comparison count alone cannot.
    void InsertPositional(ObservableCollection<RepositoryNodeViewModel> nodes, int index, RepositoryNodeViewModel node)
    {
        if (index != nodes.Count)
            _positionalInsertCount++;
        nodes.Insert(index, node);
    }

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

        InsertPositional(nodes, index, node);
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
                low = mid + 1;
            else
                high = mid;
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
            return;

        if (nodes.Count == 0)
        {
            foreach (var node in SortBatchForDisplay(batch))
                nodes.Add(node);
            return;
        }

        if (_activeSortMode != SortMode.WorkingDirectory)
        {
            foreach (var node in batch)
                nodes.Add(node);
            return;
        }

        var sortedBatch = SortBatchForDisplay(batch);
        var searchStart = 0;
        foreach (var node in sortedBatch)
        {
            var index = FindSortedInsertionIndex(nodes, node.WorkingDirectory, searchStart);
            if (index == nodes.Count)
                nodes.Add(node);
            else
                InsertPositional(nodes, index, node);
            searchStart = index + 1;
        }
    }
}

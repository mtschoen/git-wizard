namespace GitWizard.Discovery;

/// <summary>
/// What a candidate selection observed about itself: how many whole-index scans it started, and how many
/// indexed candidates it could not turn into a path because their parent chain is longer than the index can name.
/// </summary>
sealed class CandidateSelectionDiagnostics
{
    int _indexScanCount;
    int _unresolvedPathCount;

    public int IndexScanCount => Volatile.Read(ref _indexScanCount);

    public int UnresolvedPathCount => Volatile.Read(ref _unresolvedPathCount);

    internal void RecordIndexScan() => Interlocked.Increment(ref _indexScanCount);

    internal void RecordUnresolvedPath() => Interlocked.Increment(ref _unresolvedPathCount);
}

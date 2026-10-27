using MFTLib.Index;

namespace GitWizard.Discovery;

/// <summary>
/// The directories holding both an <c>objects</c> and a <c>refs</c> child directory, found with one index
/// scan per name instead of one whole-index child scan per candidate.
/// </summary>
sealed class BareLayoutIndex
{
    readonly HashSet<FileEntry> _directories;

    BareLayoutIndex(HashSet<FileEntry> directories) => _directories = directories;

    public bool Contains(FileEntry directory) => _directories.Contains(directory);

    public static BareLayoutIndex Build(
        FileIndexSet index, CandidateSelectionDiagnostics? diagnostics, CancellationToken cancellationToken)
    {
        var withObjects = ParentsOfDirectoriesNamed(index, "objects", diagnostics, cancellationToken);
        var withRefs = ParentsOfDirectoriesNamed(index, "refs", diagnostics, cancellationToken);
        withObjects.IntersectWith(withRefs);
        return new BareLayoutIndex(withObjects);
    }

    static HashSet<FileEntry> ParentsOfDirectoriesNamed(
        FileIndexSet index, string name, CandidateSelectionDiagnostics? diagnostics, CancellationToken cancellationToken)
    {
        var parents = new HashSet<FileEntry>();
        foreach (var entry in GitWizardApi.ScanByName(index, name, diagnostics, cancellationToken))
        {
            try
            {
                if (!entry.IsDeleted && entry.IsDirectory && entry.Parent is { } parent)
                {
                    parents.Add(parent);
                }
            }
            catch (InvalidOperationException)
            {
                // A newer file reused this row slot between the scan and the read, so the entry is gone.
            }
        }

        return parents;
    }
}

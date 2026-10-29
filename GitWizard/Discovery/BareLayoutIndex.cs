using MFTLib.Index;

namespace GitWizard.Discovery;

/// <summary>
/// The directories holding both an <c>objects</c> and a <c>refs</c> child directory, built from the entries
/// the shared discovery scan found for those two names.
/// </summary>
sealed class BareLayoutIndex
{
    readonly HashSet<FileEntry> _directories;

    BareLayoutIndex(HashSet<FileEntry> directories) => _directories = directories;

    public bool Contains(FileEntry directory) => _directories.Contains(directory);

    public static BareLayoutIndex Build(IReadOnlyList<FileEntry> objects, IReadOnlyList<FileEntry> refs)
    {
        var withObjects = ParentsOfDirectories(objects);
        var withRefs = ParentsOfDirectories(refs);
        withObjects.IntersectWith(withRefs);
        return new BareLayoutIndex(withObjects);
    }

    static HashSet<FileEntry> ParentsOfDirectories(IReadOnlyList<FileEntry> entries)
    {
        var parents = new HashSet<FileEntry>();
        foreach (var entry in entries)
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

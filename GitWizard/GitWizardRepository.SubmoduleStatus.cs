using LibGit2Sharp;

namespace GitWizard;

public partial class GitWizardRepository
{
    const FileStatus StagedSubmoduleChanges = FileStatus.NewInIndex | FileStatus.ModifiedInIndex |
        FileStatus.DeletedFromIndex | FileStatus.RenamedInIndex;

    /// <summary>
    /// The parent status excludes submodule working trees, so a modified submodule is derived from
    /// the child's own refresh instead. Each submodule that has pending changes of its own, or whose
    /// checked-out commit differs from the recorded gitlink, counts as one modified entry on the
    /// parent, the way libgit2 reports a submodule with new commits or modified content. An
    /// uninitialized or missing submodule contributes no working-tree changes. Must run after the
    /// parent's own status step. Cached children only contribute after a successful refresh this pass.
    /// </summary>
    void AddModifiedSubmodulesToPendingChanges(HashSet<string> refreshedKeys)
    {
        var modifiedKeys = new HashSet<string>(StringComparer.Ordinal);
        if (Submodules != null)
        {
            lock (Submodules)
            {
                foreach (var (key, child) in Submodules)
                {
                    if (refreshedKeys.Contains(key) && child is { HasPendingChanges: true })
                    {
                        modifiedKeys.Add(key);
                    }
                }
            }
        }

        var workingDirectory = WorkingDirectory ?? string.Empty;
        foreach (var (path, health) in SubmoduleHealth)
        {
            if (health.Status == SubmoduleHealthStatus.WrongRef && refreshedKeys.Contains(SubmoduleKey(workingDirectory, path)))
            {
                modifiedKeys.Add(SubmoduleKey(workingDirectory, path));
            }
        }

        if (modifiedKeys.Count == 0)
        {
            return;
        }

        NumberOfPendingChanges = (HasPendingChanges ? NumberOfPendingChanges : 0) + modifiedKeys.Count;
        HasPendingChanges = true;
    }

    /// <summary>
    /// Excluding submodules also hides staged gitlink changes. Restore those by direct lookups at
    /// declared paths, without enumerating the index or asking children for working-tree status.
    /// </summary>
    void AddStagedSubmoduleChangesToPendingChanges(Repository repository,
        Func<string, bool> isUntrackedPlaceholder, Func<string, bool> hasStagedEntry)
    {
        var head = repository.Head.Tip;
        foreach (var path in ParseGitmodules(WorkingDirectory ?? string.Empty).Values.Distinct(StringComparer.Ordinal))
        {
            var headEntry = head?[path];
            var indexEntry = repository.Index[path];
            var headCommit = headEntry?.Mode == Mode.GitLink ? headEntry.Target.Id : null;
            var indexCommit = indexEntry?.Mode == Mode.GitLink ? indexEntry.Id : null;
            if (Equals(headCommit, indexCommit))
            {
                continue;
            }

            // Once its gitlink is removed, an initialized child appears as an untracked directory.
            // Its own refresh supplies any child changes, so this placeholder adds no separate change.
            if (indexCommit == null && isUntrackedPlaceholder(path))
            {
                NumberOfPendingChanges--;
            }

            // libgit2 can still expose a staged entry; CLI status with submodules ignored hides it.
            // Do not count a staged change already present in the source's status groups twice.
            if (hasStagedEntry(path))
            {
                continue;
            }

            NumberOfPendingChanges++;
            HasPendingChanges = true;
        }
    }
}

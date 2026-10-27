using LibGit2Sharp;

namespace GitWizard;

/// <summary>
/// The tier that proved a branch or stash is already contained in the base branch. A null
/// MergedBy means no tier did.
/// </summary>
public static class MergedBy
{
    /// <summary>The branch tip is an ancestor of the base (no commits ahead).</summary>
    public const string Ancestor = "ancestor";

    /// <summary>Every branch commit has a patch-equivalent commit on the base (rebase merge).</summary>
    public const string PatchId = "patch-id";

    /// <summary>The branch's combined diff matches one base commit's patch (squash merge).</summary>
    public const string Squash = "squash";

    /// <summary>Every path the branch or stash changes is byte-identical on the base.</summary>
    public const string Content = "content";
}

/// <summary>
/// Tiered "is this branch's work already in the base?" detection, cheapest tier first. Catches
/// ancestry merges, rebase merges and squash merges, which ancestry alone misses.
/// </summary>
public static class MergeDetector
{
    static readonly Signature SyntheticSignature = new("git-wizard", "git-wizard@localhost", DateTimeOffset.UnixEpoch);

    /// <summary>
    /// Return the first <see cref="MergedBy"/> tier proving <paramref name="branchTip"/>'s work is
    /// in <paramref name="baseTip"/>, or null when none does. A git CLI failure in a tier is
    /// logged and counts as "not merged by that tier", never as merged.
    /// </summary>
    public static string? Detect(Repository repository, Commit branchTip, Commit baseTip)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(branchTip);
        ArgumentNullException.ThrowIfNull(baseTip);

        var mergeBase = repository.ObjectDatabase.FindMergeBase(branchTip, baseTip);
        if (mergeBase?.Sha == branchTip.Sha)
        {
            return MergedBy.Ancestor;
        }

        if (AllCommitsHavePatchEquivalent(repository, baseTip, branchTip))
        {
            return MergedBy.PatchId;
        }

        if (mergeBase == null)
        {
            return null;
        }

        if (IsSquashMerged(repository, baseTip, branchTip, mergeBase))
        {
            return MergedBy.Squash;
        }

        return IsContentMerged(repository, branchTip, baseTip, mergeBase) ? MergedBy.Content : null;
    }

    /// <summary>
    /// The in-process tiers only: <see cref="MergedBy.Ancestor"/>, then (for a branch ahead of
    /// the base) <see cref="MergedBy.Content"/>. Skips the git cherry tiers, which can take tens of
    /// seconds per branch on a large repository, so a rebase or squash merge whose paths later
    /// changed on the base reports null here while <see cref="Detect"/> finds it.
    /// </summary>
    public static string? DetectCheap(Repository repository, Commit branchTip, Commit baseTip)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(branchTip);
        ArgumentNullException.ThrowIfNull(baseTip);

        var mergeBase = repository.ObjectDatabase.FindMergeBase(branchTip, baseTip);
        if (mergeBase == null)
        {
            return null;
        }

        if (mergeBase.Sha == branchTip.Sha)
        {
            return MergedBy.Ancestor;
        }

        return IsContentMerged(repository, branchTip, baseTip, mergeBase) ? MergedBy.Content : null;
    }

    static bool IsContentMerged(Repository repository, Commit branchTip, Commit baseTip, Commit mergeBase)
    {
        var changedPaths = ChangedPaths(repository, mergeBase.Tree, branchTip.Tree);
        return PathsMatch(changedPaths, branchTip.Tree, baseTip.Tree);
    }

    /// <summary>
    /// True when every path in <paramref name="paths"/> has the same mode and object SHA (or is
    /// absent in both) in <paramref name="expected"/> and <paramref name="actual"/>.
    /// </summary>
    internal static bool PathsMatch(IEnumerable<string> paths, Tree expected, Tree actual) =>
        paths.All(path => EntriesMatch(expected[path], actual[path]));

    static bool EntriesMatch(TreeEntry? expected, TreeEntry? actual)
    {
        if (expected == null || actual == null)
        {
            return expected == null && actual == null;
        }

        return expected.Mode == actual.Mode
            && string.Equals(expected.Target.Sha, actual.Target.Sha, StringComparison.Ordinal);
    }

    // Rename detection is quadratic in the diff size (minutes per branch on a monorepo whose
    // branch has drifted from trunk) and adds nothing here: a rename surfaces as a delete plus an
    // add, so both paths are still compared.
    static readonly CompareOptions PathsOnly = new() { Similarity = SimilarityOptions.None };

    /// <summary>
    /// Every path added, deleted or modified between two trees (a rename reports both paths).
    /// </summary>
    internal static HashSet<string> ChangedPaths(Repository repository, Tree oldTree, Tree newTree)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var change in repository.Diff.Compare<TreeChanges>(oldTree, newTree, PathsOnly))
        {
            paths.Add(change.Path);
            paths.Add(change.OldPath);
        }

        return paths;
    }

    static bool AllCommitsHavePatchEquivalent(Repository repository, Commit baseTip, Commit tip)
    {
        // git cherry silently ignores merge commits, so any merge commit in baseTip..tip must fall
        // through to the squash and content tiers rather than being treated as patch-equivalent.
        var merges = GitCli.Run(repository, "rev-list", "--merges", $"{baseTip.Sha}..{tip.Sha}");
        if (!merges.Succeeded || !string.IsNullOrWhiteSpace(merges.Output))
        {
            return false;
        }

        var result = GitCli.Run(repository, "cherry", baseTip.Sha, tip.Sha);
        if (!result.Succeeded)
        {
            return false;
        }

        var lines = CherryLines(result.Output);
        return lines.Length > 0 && !lines.Any(line => line.StartsWith('+'));
    }

    // git-delete-squashed technique: a synthetic commit whose tree is the branch tip and whose
    // only parent is the merge base carries the branch's whole change as one patch, which git
    // cherry then matches against a squash commit on the base. Creating it writes a dangling
    // commit object into the repository; nothing references it and git gc removes it. It is
    // written through LibGit2Sharp with a fixed signature so it works without a configured
    // user.name/user.email (git commit-tree refuses to run without one).
    static bool IsSquashMerged(Repository repository, Commit baseTip, Commit branchTip, Commit mergeBase)
    {
        var synthetic = repository.ObjectDatabase.CreateCommit(
            SyntheticSignature, SyntheticSignature, "_", branchTip.Tree, [mergeBase], prettifyMessage: false);
        var result = GitCli.Run(repository, "cherry", baseTip.Sha, synthetic.Sha);
        return result.Succeeded && CherryLines(result.Output).FirstOrDefault()?.StartsWith('-') == true;
    }

    static string[] CherryLines(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

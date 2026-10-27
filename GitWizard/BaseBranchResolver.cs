using LibGit2Sharp;

namespace GitWizard;

/// <summary>
/// Chooses the branch that merge detection compares against. Remote-tracking defaults win over
/// local branches so a stale local <c>main</c> is never the comparison point while
/// <c>origin/main</c> exists.
/// </summary>
public static class BaseBranchResolver
{
    static readonly string[] DefaultNames = ["main", "master", "develop"];

    /// <summary>
    /// Resolve <paramref name="explicitName"/> when given (local branch, then
    /// <c>origin/&lt;name&gt;</c>, then any remote's <c>&lt;name&gt;</c>). Otherwise prefer the
    /// target of <c>refs/remotes/origin/HEAD</c>, then <c>origin/main</c>, <c>origin/master</c>,
    /// <c>origin/develop</c>, then local main/master/develop, then the checked-out branch.
    /// Returns null for a detached HEAD with none of those.
    /// </summary>
    public static Branch? Resolve(Repository repository, string? explicitName = null)
    {
        ArgumentNullException.ThrowIfNull(repository);

        if (!string.IsNullOrEmpty(explicitName) && ResolveExplicit(repository, explicitName) is { } named)
        {
            return named;
        }

        return ResolveRemoteDefault(repository) ?? ResolveLocalDefault(repository);
    }

    /// <summary>
    /// True when <paramref name="branch"/> is the base itself or the local branch that shares the
    /// base's short name (local <c>main</c> for base <c>origin/main</c>); such
    /// branches are never reported as merged work or pruned.
    /// </summary>
    public static bool IsBaseOrCounterpart(Branch branch, Branch? baseBranch)
    {
        ArgumentNullException.ThrowIfNull(branch);
        if (baseBranch == null)
        {
            return false;
        }

        if (string.Equals(branch.CanonicalName, baseBranch.CanonicalName, StringComparison.Ordinal))
        {
            return true;
        }

        if (!baseBranch.IsRemote)
        {
            return false;
        }

        return string.Equals(branch.FriendlyName, ShortNameOfRemoteBranch(baseBranch), StringComparison.Ordinal);
    }

    internal static Branch? ResolveExplicit(Repository repository, string name)
    {
        return repository.Branches[name]
            ?? repository.Branches[$"origin/{name}"]
            ?? repository.Branches.FirstOrDefault(branch =>
                branch.IsRemote && branch.FriendlyName.EndsWith($"/{name}", StringComparison.OrdinalIgnoreCase));
    }

    static Branch? ResolveRemoteDefault(Repository repository)
    {
        if (repository.Refs["refs/remotes/origin/HEAD"] is SymbolicReference { Target: { } target }
            && repository.Branches[target.CanonicalName] is { Tip: not null } headTarget)
        {
            return headTarget;
        }

        foreach (var name in DefaultNames)
        {
            if (repository.Branches[$"origin/{name}"] is { IsRemote: true, Tip: not null } candidate)
            {
                return candidate;
            }
        }

        return null;
    }

    static Branch? ResolveLocalDefault(Repository repository)
    {
        foreach (var name in DefaultNames)
        {
            if (repository.Branches[name] is { IsRemote: false } candidate)
            {
                return candidate;
            }
        }

        return repository.Head.Reference is SymbolicReference ? repository.Head : null;
    }

    static string ShortNameOfRemoteBranch(Branch remoteBranch)
    {
        var prefix = remoteBranch.RemoteName + "/";
        return remoteBranch.FriendlyName.StartsWith(prefix, StringComparison.Ordinal)
            ? remoteBranch.FriendlyName[prefix.Length..]
            : remoteBranch.FriendlyName;
    }
}

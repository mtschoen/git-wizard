using LibGit2Sharp;

namespace GitWizardTests.TestSupport;

/// <summary>
/// Throwaway repository driven through the git CLI, for merge-detection and prune scenarios
/// (squash/rebase merges, stashes, worktrees, a bare origin). Starts on <c>main</c> with one
/// commit of <c>base.txt</c>. Disposing deletes it and every directory it created.
/// </summary>
internal sealed class GitScenarioRepo : IDisposable
{
    readonly List<TempDirectory> _directories = new();

    public string Path { get; }

    GitScenarioRepo()
    {
        var work = new TempDirectory("gw-scenario-");
        _directories.Add(work);
        Path = work.Path;
    }

    public static GitScenarioRepo Create()
    {
        var scenario = new GitScenarioRepo();
        try
        {
            scenario.Git("init", "-q", "-b", "main");
            scenario.Git("config", "user.email", "test@example.com");
            scenario.Git("config", "user.name", "Test");
            scenario.Git("config", "commit.gpgsign", "false");
            scenario.Commit("base.txt", "base", "initial");
            return scenario;
        }
        catch
        {
            scenario.Dispose();
            throw;
        }
    }

    public void Git(params string[] arguments) => TempRepoFixture.RunGit(Path, arguments);

    public void Write(string fileName, string content)
    {
        var filePath = System.IO.Path.Combine(Path, fileName);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(filePath)!);
        File.WriteAllText(filePath, content);
    }

    public void Commit(string fileName, string content, string message)
    {
        Write(fileName, content);
        Git("add", fileName);
        Git("commit", "-q", "-m", message);
    }

    public void NewBranch(string name) => Git("checkout", "-q", "-b", name);

    public void Checkout(string name) => Git("checkout", "-q", name);

    /// <summary>
    /// Squash-merge <paramref name="branch"/> into the checked-out branch as one commit.
    /// </summary>
    public void SquashMerge(string branch)
    {
        Git("merge", "-q", "--squash", branch);
        Git("commit", "-q", "-m", $"squash {branch}");
    }

    /// <summary>
    /// Create a bare origin, push <c>main</c> to it with upstream tracking, and return its path.
    /// </summary>
    public string AddOrigin()
    {
        var origin = new TempDirectory("gw-scenario-origin-");
        _directories.Add(origin);
        TempRepoFixture.RunGit(origin.Path, "init", "-q", "--bare", "-b", "main");
        Git("remote", "add", "origin", origin.Path);
        Git("push", "-q", "-u", "origin", "main");
        return origin.Path;
    }

    /// <summary>
    /// Check <paramref name="existingBranch"/> out in a new linked worktree and return its path.
    /// </summary>
    public string AddWorktree(string existingBranch)
    {
        var worktree = new TempDirectory("gw-scenario-wt-", create: false);
        _directories.Add(worktree);
        Git("worktree", "add", "-q", worktree.Path, existingBranch);
        return worktree.Path;
    }

    public string Sha(string revision)
    {
        using var repository = new Repository(Path);
        return repository.Lookup(revision).Sha;
    }

    public bool HasBranch(string name)
    {
        using var repository = new Repository(Path);
        return repository.Branches[name] != null;
    }

    public List<string> StashShas()
    {
        using var repository = new Repository(Path);
        return repository.Stashes.Select(stash => stash.WorkTree.Sha).ToList();
    }

    public List<string> StashMessages()
    {
        using var repository = new Repository(Path);
        return repository.Stashes.Select(stash => stash.Message).ToList();
    }

    /// <summary>Every direct ref whose canonical name starts with <paramref name="prefix"/>, mapped to its target SHA.</summary>
    public Dictionary<string, string> RefTargets(string prefix)
    {
        using var repository = new Repository(Path);
        return repository.Refs
            .Where(reference => reference.CanonicalName.StartsWith(prefix, StringComparison.Ordinal))
            .ToDictionary(reference => reference.CanonicalName, reference => reference.TargetIdentifier, StringComparer.Ordinal);
    }

    /// <summary>
    /// Install a reference-transaction hook that aborts every transaction touching
    /// <c>refs/stash</c>, so <c>git stash store</c> fails while <c>git stash drop</c> (a reflog
    /// rewrite) still succeeds.
    /// </summary>
    public static void RejectStashRefUpdates(string repositoryPath)
    {
        var hooks = System.IO.Path.Combine(repositoryPath, ".git", "hooks");
        Directory.CreateDirectory(hooks);
        var hookPath = System.IO.Path.Combine(hooks, "reference-transaction");
        string[] script =
        [
            "#!/bin/sh",
            "if grep -q ' refs/stash$' && [ \"$1\" = prepared ]; then",
            "  echo 'test hook rejects refs/stash' >&2",
            "  exit 1",
            "fi",
            "exit 0"
        ];
        File.WriteAllText(hookPath, string.Join('\n', script) + "\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hookPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    public void Dispose()
    {
        foreach (var directory in _directories)
        {
            directory.Dispose();
        }
    }
}

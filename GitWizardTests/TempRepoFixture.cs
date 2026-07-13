using System.Diagnostics;
using LibGit2Sharp;

namespace GitWizardTests;

/// <summary>
/// Throwaway git repository in a temp directory. Disposing deletes it. Used
/// by tests that need to control pending-changes and unpushed-commit state
/// without depending on the running checkout.
/// </summary>
internal sealed class TempRepoFixture : IDisposable
{
    public string Path { get; }

    static readonly Signature Author = new("Test", "test@example.com", DateTimeOffset.Now);

    // Temp directories (upstream submodule sources) created alongside this fixture
    // that must also be deleted on dispose.
    readonly List<string> _extraCleanupDirs = new();

    TempRepoFixture(string path)
    {
        Path = path;
    }

    /// <summary>
    /// Create a throwaway repository with a single initial commit (a staged README.md).
    /// </summary>
    /// <param name="commitTime">
    /// Optional author/committer timestamp for the initial commit. Pass a past value to model a
    /// stale repository (e.g. <c>DateTimeOffset.Now.AddDays(-60)</c>) without poking the
    /// computed <c>DaysSinceLastCommit</c> via reflection. Defaults to "now".
    /// </param>
    public static TempRepoFixture CreateWithInitialCommit(DateTimeOffset? commitTime = null)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gw-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        Repository.Init(path);

        var signature = commitTime is { } when
            ? new Signature("Test", "test@example.com", when)
            : Author;

        using (var repository = new Repository(path))
        {
            var readmePath = System.IO.Path.Combine(path, "README.md");
            File.WriteAllText(readmePath, "initial");
            Commands.Stage(repository, "README.md");
            repository.Commit("initial", signature, signature);
        }

        return new TempRepoFixture(path);
    }

    public void AppendCommit(string fileName)
    {
        var filePath = System.IO.Path.Combine(Path, fileName);
        File.WriteAllText(filePath, Guid.NewGuid().ToString());
        using var repository = new Repository(Path);
        Commands.Stage(repository, fileName);
        repository.Commit($"add {fileName}", Author, Author);
    }

    /// <summary>
    /// Create <paramref name="branchName"/> off the current HEAD, add one
    /// commit on it, then switch back to the original branch - leaving the new
    /// branch one commit ahead of (and unmerged into) the default.
    /// </summary>
    public void CommitOnNewBranch(string branchName, string fileName)
    {
        using var repository = new Repository(Path);
        var originalBranch = repository.Head.FriendlyName;
        var branch = repository.Branches.Add(branchName, repository.Head.Tip);
        Commands.Checkout(repository, branch);

        var filePath = System.IO.Path.Combine(Path, fileName);
        File.WriteAllText(filePath, Guid.NewGuid().ToString());
        Commands.Stage(repository, fileName);
        repository.Commit($"add {fileName}", Author, Author);

        Commands.Checkout(repository, repository.Branches[originalBranch]);
    }

    /// <summary>
    /// Create a bare "origin" remote and push the current branch to it, setting
    /// upstream tracking. After this the pushed commits live on
    /// <c>refs/remotes/origin/&lt;branch&gt;</c> and are no longer local-only -
    /// modelling the realistic case where mainline history is already on a remote.
    /// </summary>
    public void AddOriginRemoteAndPush()
    {
        var upstream = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gw-origin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(upstream);
        Repository.Init(upstream, isBare: true);
        _extraCleanupDirs.Add(upstream);

        string branch;
        using (var repository = new Repository(Path))
            branch = repository.Head.FriendlyName;

        // git wants forward slashes in the remote URL, even on Windows.
        RunGit(Path, "remote", "add", "origin", upstream.Replace('\\', '/'));
        RunGit(Path, "push", "-u", "origin", branch);
    }

    /// <summary>
    /// Advance the "origin" remote's tracked branch independently of this repository: clone the
    /// bare origin created by <see cref="AddOriginRemoteAndPush"/> into a scratch working copy, add
    /// a commit there, and push it back. This repository's own HEAD and remote-tracking ref are
    /// left untouched - a fetch is required to observe the new commit - modelling another
    /// contributor pushing to the remote while this checkout sits still (the UniMerge-flub
    /// scenario git-wizard#78 is meant to catch).
    /// </summary>
    public void AdvanceOriginIndependently(string fileName)
    {
        string originUrl;
        using (var repository = new Repository(Path))
            originUrl = repository.Network.Remotes["origin"].Url;

        var scratch = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gw-scratch-" + Guid.NewGuid().ToString("N"));
        RunGit(System.IO.Path.GetTempPath(), "clone", originUrl, scratch);
        _extraCleanupDirs.Add(scratch);

        File.WriteAllText(System.IO.Path.Combine(scratch, fileName), Guid.NewGuid().ToString());
        RunGit(scratch, "add", fileName);
        RunGit(scratch, "-c", "user.email=test@example.com", "-c", "user.name=Test",
            "commit", "-m", $"add {fileName}");
        RunGit(scratch, "push", "origin", "HEAD");
    }

    /// <summary>
    /// Commit a <c>.gitmodules</c> entry pointing at <paramref name="path"/> WITHOUT
    /// adding a matching gitlink to the index. The superproject then declares a
    /// submodule that has no index entry - modelling the "declared in .gitmodules
    /// but missing from the index" health issue (e.g. a submodule removed with a
    /// plain <c>rm -r</c> instead of <c>git submodule deinit</c>).
    /// </summary>
    public void AddOrphanedGitmodulesEntry(string name, string path)
    {
        var gitmodulesPath = System.IO.Path.Combine(Path, ".gitmodules");
        File.WriteAllText(gitmodulesPath,
            $"[submodule \"{name}\"]\n\tpath = {path}\n\turl = https://example.com/{name}.git\n");
        using var repository = new Repository(Path);
        Commands.Stage(repository, ".gitmodules");
        repository.Commit($"declare submodule {name}", Author, Author);
    }

    /// <summary>
    /// Add a real, checked-out submodule at <paramref name="path"/> (cloned from a
    /// throwaway upstream repo) and commit the gitlink. Returns the absolute path to
    /// the submodule's working directory.
    /// </summary>
    public string AddInitializedSubmodule(string path)
    {
        var upstream = CreateUpstreamRepo();
        RunGit(Path, "-c", "protocol.file.allow=always", "-c", "user.email=test@example.com",
            "-c", "user.name=Test", "submodule", "add", upstream, path);
        RunGit(Path, "-c", "user.email=test@example.com", "-c", "user.name=Test",
            "commit", "-m", $"add submodule {path}");
        return System.IO.Path.Combine(Path, path);
    }

    /// <summary>
    /// Add a submodule then deinitialize it, leaving the gitlink in the index and the
    /// entry in .gitmodules but no working-tree checkout - the state of a superproject
    /// cloned without <c>--recursive</c>.
    /// </summary>
    public void AddUninitializedSubmodule(string path)
    {
        AddInitializedSubmodule(path);
        RunGit(Path, "submodule", "deinit", "-f", path);
    }

    /// <summary>
    /// Add a submodule, then advance the submodule's own HEAD with a fresh commit
    /// WITHOUT updating the superproject's gitlink - so the checked-out commit no
    /// longer matches the ref the superproject records.
    /// </summary>
    public void AddSubmoduleAtWrongRef(string path)
    {
        var submoduleDir = AddInitializedSubmodule(path);
        File.WriteAllText(System.IO.Path.Combine(submoduleDir, "drift.txt"), Guid.NewGuid().ToString());
        RunGit(submoduleDir, "-c", "user.email=test@example.com", "-c", "user.name=Test", "add", "drift.txt");
        RunGit(submoduleDir, "-c", "user.email=test@example.com", "-c", "user.name=Test", "commit", "-m", "drift");
    }

    /// <summary>
    /// Stage a gitlink at <paramref name="path"/> (pointing at the current HEAD) and
    /// commit it WITHOUT a matching .gitmodules entry - an index that records a
    /// submodule the .gitmodules file knows nothing about.
    /// </summary>
    public void AddGitlinkWithoutGitmodules(string path)
    {
        string sha;
        using (var repository = new Repository(Path))
            sha = repository.Head.Tip!.Sha;

        RunGit(Path, "update-index", "--add", "--cacheinfo", $"160000,{sha},{path}");
        RunGit(Path, "-c", "user.email=test@example.com", "-c", "user.name=Test",
            "commit", "-m", $"orphan gitlink {path}");
    }

    /// <summary>
    /// Detach HEAD at <paramref name="reference"/> (default current HEAD), modelling a repository
    /// checked out at a specific commit rather than on a branch. Advice output is suppressed so the
    /// detach doesn't write to stderr.
    /// </summary>
    public void DetachHead(string reference = "HEAD")
        => RunGit(Path, "-c", "advice.detachedHead=false", "checkout", "--detach", reference);

    /// <summary>
    /// Create <paramref name="name"/> at the current HEAD WITHOUT adding any commit, leaving it at
    /// exactly the default branch's tip - a "boring" branch the actionable view drops but the full
    /// inventory keeps.
    /// </summary>
    public void AddBranchAtHead(string name) => RunGit(Path, "branch", name);

    /// <summary>
    /// Merge <paramref name="name"/> into the current branch with an explicit merge commit
    /// (<c>--no-ff</c>), leaving the merged branch fully contained in - but no longer at the tip of -
    /// the default branch. The branch then reads as merged/downstream.
    /// </summary>
    public void MergeBranchNoFastForward(string name)
        => RunGit(Path, "-c", "user.email=test@example.com", "-c", "user.name=Test",
            "merge", "--no-ff", "-m", $"merge {name}", name);

    /// <summary>
    /// Stage an untracked file so the working tree reports pending changes on the next refresh.
    /// </summary>
    public void AddUntrackedFile(string fileName)
        => File.WriteAllText(System.IO.Path.Combine(Path, fileName), Guid.NewGuid().ToString());

    /// <summary>
    /// Add a linked worktree (on a fresh branch <paramref name="branchName"/>) in a sibling temp
    /// directory and return its absolute path. The directory is cleaned up on dispose.
    /// </summary>
    public string AddWorktree(string branchName)
    {
        var worktreePath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "gw-wt-" + Guid.NewGuid().ToString("N"));
        // git creates the directory; it must not exist beforehand.
        RunGit(Path, "worktree", "add", worktreePath, "-b", branchName);
        _extraCleanupDirs.Add(worktreePath);
        return worktreePath;
    }

    string CreateUpstreamRepo()
    {
        var upstream = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gw-upstream-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(upstream);
        Repository.Init(upstream);
        using (var repository = new Repository(upstream))
        {
            File.WriteAllText(System.IO.Path.Combine(upstream, "lib.txt"), "lib");
            Commands.Stage(repository, "lib.txt");
            repository.Commit("init upstream", Author, Author);
        }

        _extraCleanupDirs.Add(upstream);
        // git wants forward slashes in the submodule URL, even on Windows.
        return upstream.Replace('\\', '/');
    }

    static void RunGit(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start git");
        // Drain stdout so a full pipe can't deadlock the child; the content is unused.
        _ = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} failed (exit {process.ExitCode}): {standardError}");
    }

    /// <summary>
    /// Delete the repository directory immediately - e.g. to simulate an external deletion
    /// mid-test. Idempotent with <see cref="Dispose"/>, which still runs at scope end.
    /// </summary>
    public void DeleteNow() => DeleteTree(Path);

    public void Dispose()
    {
        DeleteTree(Path);
        foreach (var directory in _extraCleanupDirs)
            DeleteTree(directory);
    }

    static void DeleteTree(string directory)
    {
        try
        {
            // LibGit2Sharp leaves some files as read-only; clear attributes
            // recursively before deletion so cleanup doesn't explode on CI.
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup; ignore if the OS is still holding locks.
        }
    }
}

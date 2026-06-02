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

    public static TempRepoFixture CreateWithInitialCommit()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gw-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        Repository.Init(path);

        using (var repository = new Repository(path))
        {
            var readmePath = System.IO.Path.Combine(path, "README.md");
            File.WriteAllText(readmePath, "initial");
            Commands.Stage(repository, "README.md");
            repository.Commit("initial", Author, Author);
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
    /// commit on it, then switch back to the original branch — leaving the new
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
    /// Commit a <c>.gitmodules</c> entry pointing at <paramref name="path"/> WITHOUT
    /// adding a matching gitlink to the index. The superproject then declares a
    /// submodule that has no index entry — modelling the "declared in .gitmodules
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
    /// entry in .gitmodules but no working-tree checkout — the state of a superproject
    /// cloned without <c>--recursive</c>.
    /// </summary>
    public void AddUninitializedSubmodule(string path)
    {
        AddInitializedSubmodule(path);
        RunGit(Path, "submodule", "deinit", "-f", path);
    }

    /// <summary>
    /// Add a submodule, then advance the submodule's own HEAD with a fresh commit
    /// WITHOUT updating the superproject's gitlink — so the checked-out commit no
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
    /// commit it WITHOUT a matching .gitmodules entry — an index that records a
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

    static string RunGit(string workingDirectory, params string[] arguments)
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
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} failed (exit {process.ExitCode}): {standardError}");
        return standardOutput;
    }

    /// <summary>
    /// Delete the repository directory immediately — e.g. to simulate an external deletion
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
        catch (Exception)
        {
            // Best-effort cleanup; ignore if the OS is still holding locks.
        }
    }
}

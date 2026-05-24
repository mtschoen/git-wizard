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

    public void Dispose()
    {
        try
        {
            // LibGit2Sharp leaves some files as read-only; clear attributes
            // recursively before deletion so cleanup doesn't explode on CI.
            foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception)
        {
            // Best-effort cleanup; ignore if the OS is still holding locks.
        }
    }
}

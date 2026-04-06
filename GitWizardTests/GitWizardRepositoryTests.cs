using System.Text.Json;
using GitWizard;

namespace GitWizardTests;

public class GitWizardRepositoryTests
{
    [Test]
    public void GitWizardCommitInfo_RoundTripsJson()
    {
        var commit = new GitWizardCommitInfo
        {
            Hash = "abc1234",
            Message = "Fix the thing",
            Date = new DateTimeOffset(2026, 4, 1, 12, 0, 0, TimeSpan.Zero),
            AuthorEmail = "dev@example.com"
        };

        var json = JsonSerializer.Serialize(commit);
        var deserialized = JsonSerializer.Deserialize<GitWizardCommitInfo>(json);

        Assert.That(deserialized, Is.Not.Null);
        Assert.That(deserialized!.Hash, Is.EqualTo("abc1234"));
        Assert.That(deserialized.Message, Is.EqualTo("Fix the thing"));
        Assert.That(deserialized.AuthorEmail, Is.EqualTo("dev@example.com"));
    }

    [Test]
    public void Refresh_PopulatesRecentCommits()
    {
        GitWizardLog.SilentMode = true;
        var repoPath = FindRepoRoot();
        var repository = new GitWizardRepository(repoPath);
        repository.Refresh();

        Assert.That(repository.RecentCommits, Is.Not.Null);
        Assert.That(repository.RecentCommits, Has.Count.GreaterThan(0));
        Assert.That(repository.RecentCommits!.Count, Is.LessThanOrEqualTo(10));

        var first = repository.RecentCommits[0];
        Assert.That(first.Hash, Has.Length.EqualTo(7));
        Assert.That(first.Message, Is.Not.Empty);
        Assert.That(first.AuthorEmail, Is.Not.Empty);
    }

    static string FindRepoRoot()
    {
        var directory = Directory.GetCurrentDirectory();
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory, ".git")))
                return directory;
            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not find git repo root from working directory");
    }
}

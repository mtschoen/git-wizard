using System.Reflection;
using System.Text.Json;
using GitWizard;
using LibGit2Sharp;

namespace GitWizardTests;

/// <summary>
/// Additional coverage tests for <see cref="GitWizardRepository"/> partial:
/// Refresh edge cases, Submodule-related refresh, serialization, and IsPublishReady.
/// </summary>
public class CoverageBoostRepositoryTests
{
    [SetUp]
    public void SetUp() => GitWizardLog.SilentMode = true;

    [Test]
    public void Refresh_ModifiedFile_DetectsModifiedStatus()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        File.WriteAllText(Path.Combine(fixture.Path, "README.md"), "modified content");

        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        Assert.Multiple(() =>
        {
            Assert.That(repo.HasPendingChanges, Is.True);
            Assert.That(repo.NumberOfPendingChanges, Is.GreaterThan(0));
        });
    }

    [Test]
    public void Refresh_DeletedTrackedFile_DetectsPending()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        File.Delete(Path.Combine(fixture.Path, "README.md"));

        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        Assert.That(repo.HasPendingChanges, Is.True);
    }

    [Test]
    public void Refresh_RenamedFile_DetectsPending()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var original = Path.Combine(fixture.Path, "README.md");
        var renamed = Path.Combine(fixture.Path, "RENAMED.md");
        File.Move(original, renamed);
        using (var libRepo = new Repository(fixture.Path))
        {
            Commands.Stage(libRepo, "README.md");
            Commands.Stage(libRepo, "RENAMED.md");
        }

        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        Assert.That(repo.HasPendingChanges, Is.True);
    }

    [Test]
    public void Refresh_MultipleUntracked_CountsAll()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        for (var i = 0; i < 5; i++)
            File.WriteAllText(Path.Combine(fixture.Path, $"untracked{i}.txt"), $"content{i}");

        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        Assert.Multiple(() =>
        {
            Assert.That(repo.HasPendingChanges, Is.True);
            Assert.That(repo.NumberOfPendingChanges, Is.GreaterThanOrEqualTo(5));
        });
    }

    [Test]
    public void Refresh_WithSubmodule_PopulatesSubmodules()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddInitializedSubmodule("ext/lib");

        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        Assert.That(repo.Submodules, Is.Not.Null);
        Assert.That(repo.Submodules!, Is.Not.Empty);
    }

    [Test]
    public void Refresh_NoSubmodules_SubmodulesNull()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        Assert.That(repo.Submodules, Is.Null);
    }

    [Test]
    public void IsPublishReady_CleanOnDefaultBranch_NoBehind_IsTrue()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        // On default branch, clean, no remote = BehindRemoteCount=0
        Assert.That(repo.IsPublishReady, Is.True);
    }

    [Test]
    public void IsPublishReady_WithPendingChanges_IsFalse()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        File.WriteAllText(Path.Combine(fixture.Path, "dirty.txt"), "dirty");
        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        Assert.That(repo.IsPublishReady, Is.False);
    }

    [Test]
    public void Refresh_RecentCommits_LimitedTo10()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        for (var i = 0; i < 15; i++)
            fixture.AppendCommit($"file{i}.txt");

        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        Assert.That(repo.RecentCommits, Is.Not.Null);
        Assert.That(repo.RecentCommits!, Has.Count.LessThanOrEqualTo(10));
    }

    [Test]
    public void Refresh_RecentCommits_ContainsShortHash()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        Assert.That(repo.RecentCommits, Is.Not.Null);
        Assert.That(repo.RecentCommits![0].Hash, Has.Length.EqualTo(7));
    }

    [Test]
    public void Refresh_AuthorEmails_CollectsFromCommits()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AppendCommit("extra.txt");
        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        Assert.That(repo.AuthorEmails, Is.Not.Null);
        Assert.That(repo.AuthorEmails!, Does.Contain("test@example.com"));
    }

    [Test]
    public void Repository_SerializesAndDeserializes()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        var json = JsonSerializer.Serialize(repo);

        Assert.That(json, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("WorkingDirectory"));
            Assert.That(json, Does.Contain("CurrentBranch"));
            Assert.That(json, Does.Contain("SizeOnDisk"));
        });
    }

    [Test]
    public void Refresh_SizeOnDisk_IsPositive()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        File.WriteAllText(Path.Combine(fixture.Path, "bigfile.txt"), new string('x', 1000));
        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        Assert.That(repo.SizeOnDisk, Is.GreaterThan(0));
    }

    [Test]
    public void GitWizardRepository_PrivateRefreshMethods_CatchExceptions()
    {
        var repo = new GitWizardRepository("/fake");

        var methods = new[]
        {
            "RefreshLocalOnlyCommits",
            "RefreshAuthorEmails",
            "RefreshRecentCommits"
        };

        foreach (var name in methods)
        {
            var method = typeof(GitWizardRepository).GetMethod(name,
                BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.That(method, Is.Not.Null, $"Method {name} not found");
            Assert.DoesNotThrow(() => method.Invoke(repo, new object?[] { null }));
        }

        var statusMethod = typeof(GitWizardRepository).GetMethod("RefreshPendingChangesStatus",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.That(statusMethod, Is.Not.Null);
        Assert.DoesNotThrow(() => statusMethod.Invoke(repo, new object?[] { null, false }));

        var divMethod = typeof(GitWizardRepository).GetMethod("RefreshBranchDivergence",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.That(divMethod, Is.Not.Null);
        Assert.DoesNotThrow(() => divMethod.Invoke(repo, new object?[] { null, false }));
    }

    [Test]
    public void GitWizardRepository_RefreshSizeOnDisk_CatchesException()
    {
        var repo = new GitWizardRepository("/fake");
        var method = typeof(GitWizardRepository).GetMethod("RefreshSizeOnDisk",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        Assert.DoesNotThrow(() => method.Invoke(repo, new object?[] { null }));
    }

    [Test]
    public void GitWizardRepository_NotifyRefreshCompleted_CatchesException()
    {
        var repo = new GitWizardRepository("/fake");
        var method = typeof(GitWizardRepository).GetMethod("NotifyRefreshCompleted",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        var handler = new FailingUpdateHandler();
        Assert.DoesNotThrow(() => method.Invoke(repo, new object[] { handler }));
    }

    [Test]
    public void GitWizardRepository_ParameterlessConstructor()
    {
        var ctor = typeof(GitWizardRepository).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
        Assert.That(ctor, Is.Not.Null);
        var repo = (GitWizardRepository)ctor.Invoke(null);
        Assert.That(repo, Is.Not.Null);
    }

    sealed class FailingUpdateHandler : IUpdateHandler
    {
        public void StartProgress(string description, int total) { }
        public void UpdateProgress(int count) { }
        public void SendUpdateMessage(string? message) { }
        public void OnRepositoryCreated(GitWizardRepository gitWizardRepository) { }
        public void OnSubmoduleCreated(GitWizardRepository parent, GitWizardRepository submodule) { }
        public void OnWorktreeCreated(GitWizardRepository worktree) { }
        public void OnUninitializedSubmoduleCreated(GitWizardRepository parent, string submodulePath) { }
        public void OnRepositoryRefreshCompleted(GitWizardRepository gitWizardRepository) =>
            throw new InvalidOperationException("Simulated handler failure");
    }
}

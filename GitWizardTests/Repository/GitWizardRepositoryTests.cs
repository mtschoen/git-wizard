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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(deserialized!.Hash, Is.EqualTo("abc1234"));
            Assert.That(deserialized.Message, Is.EqualTo("Fix the thing"));
            Assert.That(deserialized.AuthorEmail, Is.EqualTo("dev@example.com"));
        }
    }

    [Test]
    public void BranchInfo_RoundTripsJson()
    {
        var branch = new BranchInfo
        {
            Name = "feature/x",
            IsMerged = false,
            MergedInto = null,
            AheadOfDefault = 4,
            BehindDefault = 1,
            LastCommitDate = new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero),
            HasUpstream = true
        };

        var json = JsonSerializer.Serialize(branch);
        var deserialized = JsonSerializer.Deserialize<BranchInfo>(json);

        Assert.That(deserialized, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(deserialized!.Name, Is.EqualTo("feature/x"));
            Assert.That(deserialized.AheadOfDefault, Is.EqualTo(4));
            Assert.That(deserialized.BehindDefault, Is.EqualTo(1));
            Assert.That(deserialized.IsMerged, Is.False);
            Assert.That(deserialized.HasUpstream, Is.True);
        }
    }

    [Test]
    public void Repository_SerializesAndDeserializes()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        var json = JsonSerializer.Serialize(repo);

        Assert.That(json, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(json, Does.Contain("WorkingDirectory"));
            Assert.That(json, Does.Contain("CurrentBranch"));
            Assert.That(json, Does.Contain("SizeOnDisk"));
        }
    }

    [Test]
    public void GitWizardRepository_ParameterlessConstructor()
    {
        var repo = new GitWizardRepository();
        Assert.That(repo, Is.Not.Null);
    }

    [Test]
    public void Constructor_SetsWorkingDirectory()
    {
        var repo = new GitWizardRepository("/test/path");
        Assert.That(repo.WorkingDirectory, Is.EqualTo("/test/path"));
    }

    [Test]
    public void Constructor_DoesNotThrow_ForNonExistentPath()
    {
        var repo = new GitWizardRepository("/nonexistent/path");
        Assert.That(repo.WorkingDirectory, Is.EqualTo("/nonexistent/path"));
    }

    [Test]
    public void DefaultCtor_CreatesValidInstance()
    {
        var repo = new GitWizardRepository(string.Empty);
        Assert.That(repo, Is.Not.Null);
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
    public void Refresh_FlagsSubmoduleDeclaredButMissingFromIndex()
    {
        GitWizardLog.SilentMode = true;
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddOrphanedGitmodulesEntry("libfoo", "external/libfoo");

        var repository = new GitWizardRepository(fixture.Path);
        repository.Refresh();

        Assert.That(repository.HasSubmoduleIssues, Is.True);
        var submoduleHealth = repository.SubmoduleHealth;
        Assert.That(submoduleHealth, Does.ContainKey("external/libfoo"));
        var health = submoduleHealth["external/libfoo"];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(health.Status, Is.EqualTo(SubmoduleHealthStatus.MissingFromIndex));
            Assert.That(health.Issues, Is.Not.Empty);
        }
    }

    [Test]
    public void Refresh_FlagsGitlinkMissingFromGitmodules()
    {
        GitWizardLog.SilentMode = true;
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddGitlinkWithoutGitmodules("orphan/libbar");

        var repository = new GitWizardRepository(fixture.Path);
        repository.Refresh();

        Assert.That(repository.HasSubmoduleIssues, Is.True);
        var health = repository.SubmoduleHealth["orphan/libbar"];
        Assert.That(health.Status, Is.EqualTo(SubmoduleHealthStatus.MissingFromGitmodules));
    }

    [Test]
    public void Refresh_FlagsUninitializedSubmodule()
    {
        GitWizardLog.SilentMode = true;
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddUninitializedSubmodule("external/libfoo");

        var repository = new GitWizardRepository(fixture.Path);
        repository.Refresh();

        Assert.That(repository.HasSubmoduleIssues, Is.True);
        var health = repository.SubmoduleHealth["external/libfoo"];
        Assert.That(health.Status, Is.EqualTo(SubmoduleHealthStatus.Uninitialized));
    }

    [Test]
    public void Refresh_FlagsSubmoduleAtWrongRef()
    {
        GitWizardLog.SilentMode = true;
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddSubmoduleAtWrongRef("external/libfoo");

        var repository = new GitWizardRepository(fixture.Path);
        repository.Refresh();

        Assert.That(repository.HasSubmoduleIssues, Is.True);
        var health = repository.SubmoduleHealth["external/libfoo"];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(health.Status, Is.EqualTo(SubmoduleHealthStatus.WrongRef));
            Assert.That(health.ExpectedCommitSha, Is.Not.Null);
            Assert.That(health.ActualCommitSha, Is.Not.Null);
        }
        Assert.That(health.ActualCommitSha, Is.Not.EqualTo(health.ExpectedCommitSha));
    }

    [Test]
    public void Refresh_HealthyRepoReportsNoSubmoduleIssues()
    {
        GitWizardLog.SilentMode = true;
        using var fixture = TempRepoFixture.CreateWithInitialCommit();

        var repository = new GitWizardRepository(fixture.Path);
        repository.Refresh();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repository.HasSubmoduleIssues, Is.False);
            Assert.That(repository.SubmoduleHealth, Is.Empty);
        }
    }

    [Test]
    public void Refresh_DetectsStaleUpstreamSubmodule()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddSubmoduleBehindUpstream("external/libfoo", commitsBehind: 3);

        var repository = new GitWizardRepository(fixture.Path);
        repository.Refresh();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repository.HasSubmoduleIssues, Is.True);
            Assert.That(repository.SubmoduleHealth, Does.ContainKey("external/libfoo"));
            var health = repository.SubmoduleHealth["external/libfoo"];
            Assert.That(health.Status, Is.EqualTo(SubmoduleHealthStatus.StaleUpstream));
            Assert.That(health.BehindUpstreamCount, Is.EqualTo(3));
            Assert.That(health.GitLinkSha, Is.Not.Null);
            Assert.That(health.GitLinkSha!, Has.Length.GreaterThanOrEqualTo(7));
            Assert.That(health.ExpectedCommitSha, Is.EqualTo(health.GitLinkSha));
            Assert.That(health.ActualCommitSha, Is.Not.Null);
            Assert.That(health.ActualCommitSha, Is.EqualTo(health.GitLinkSha));
            Assert.That(health.Issues, Is.Not.Empty);
        }
    }

    [Test]
    public void Refresh_DetectsStaleUpstreamSubmodule_WhenRemoteIsNotOrigin()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddSubmoduleBehindUpstream("external/libfoo", commitsBehind: 3, remoteName: "upstream");

        var repository = new GitWizardRepository(fixture.Path);
        repository.Refresh();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repository.HasSubmoduleIssues, Is.True);
            Assert.That(repository.SubmoduleHealth, Does.ContainKey("external/libfoo"));
            var health = repository.SubmoduleHealth["external/libfoo"];
            Assert.That(health.Status, Is.EqualTo(SubmoduleHealthStatus.StaleUpstream));
            Assert.That(health.BehindUpstreamCount, Is.EqualTo(3));
        }
    }

    [Test]
    public void Refresh_WrongRefSubmoduleAtDefaultBranchTip_HasZeroBehindUpstream()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddInitializedSubmodule("external/libfoo");
        fixture.AddSubmoduleAtWrongRef("external/libbar");

        var repository = new GitWizardRepository(fixture.Path);
        repository.Refresh();

        var health = repository.SubmoduleHealth["external/libbar"];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(health.Status, Is.EqualTo(SubmoduleHealthStatus.WrongRef));
            Assert.That(health.BehindUpstreamCount, Is.Zero);
        }
    }

    [Test]
    public void IsPublishReady_StaleSubmodule_IsFalse()
    {
        GitWizardLog.SilentMode = true;
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddSubmoduleBehindUpstream("external/libfoo", commitsBehind: 5);

        var repository = new GitWizardRepository(fixture.Path);
        repository.Refresh();

        Assert.That(repository.IsPublishReady, Is.False,
            "A superproject with a stale submodule pointer must not be publish-ready.");
    }

    [Test]
    public void IsPublishReady_HealthySubmodule_IsTrue()
    {
        GitWizardLog.SilentMode = true;
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddInitializedSubmodule("external/libfoo");

        var repository = new GitWizardRepository(fixture.Path);
        repository.Refresh();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repository.HasSubmoduleIssues, Is.False);
            Assert.That(repository.IsPublishReady, Is.True);
        }
    }
}

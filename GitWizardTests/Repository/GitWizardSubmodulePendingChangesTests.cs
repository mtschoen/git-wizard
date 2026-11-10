using GitWizard;
using LibGit2Sharp;

namespace GitWizardTests;

/// <summary>
/// The parent status call excludes submodule working trees, so a modified submodule makes its parent
/// dirty only through the child's own refresh result (or a checked-out commit that differs from the
/// recorded gitlink).
/// </summary>
[NonParallelizable]
public class GitWizardSubmodulePendingChangesTests
{
    [SetUp]
    public void SetUp() => GitWizardLog.SilentMode = true;

    [Test]
    public void RefreshPendingChangesStatus_ChangeOnlyInsideSubmoduleWorkingTree_ReportsCleanFromTheStatusCall()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var submoduleDirectory = fixture.AddInitializedSubmodule("ext/lib");
        File.WriteAllText(Path.Combine(submoduleDirectory, "untracked.txt"), "dirty");

        var repo = new GitWizardRepository(fixture.Path);
        using var repository = new Repository(fixture.Path);
        repo.RefreshPendingChangesStatus(repository, false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repo.HasPendingChanges, Is.False);
            Assert.That(repo.NumberOfPendingChanges, Is.Zero);
        }
    }

    [Test]
    public void Refresh_DirtySubmodule_MarksParentDirtyWithOneModifiedEntry()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var submoduleDirectory = fixture.AddInitializedSubmodule("ext/lib");
        File.WriteAllText(Path.Combine(submoduleDirectory, "untracked.txt"), "dirty");

        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repo.HasPendingChanges, Is.True);
            Assert.That(repo.NumberOfPendingChanges, Is.EqualTo(1));
        }
    }

    [Test]
    public void Refresh_DirtySubmoduleAndDirtyParent_AddsOneEntryToTheParentCount()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var submoduleDirectory = fixture.AddInitializedSubmodule("ext/lib");
        File.WriteAllText(Path.Combine(submoduleDirectory, "untracked.txt"), "dirty");
        fixture.AddUntrackedFile("parent-only.txt");

        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repo.HasPendingChanges, Is.True);
            Assert.That(repo.NumberOfPendingChanges, Is.EqualTo(2));
        }
    }

    [Test]
    public void Refresh_RepeatedWithDirtySubmodule_DoesNotAccumulateTheCount()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var submoduleDirectory = fixture.AddInitializedSubmodule("ext/lib");
        File.WriteAllText(Path.Combine(submoduleDirectory, "untracked.txt"), "dirty");

        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();
        repo.Refresh();

        Assert.That(repo.NumberOfPendingChanges, Is.EqualTo(1));
    }

    [Test]
    public void Refresh_CleanSubmodule_LeavesCleanParentClean()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddInitializedSubmodule("ext/lib");

        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repo.HasPendingChanges, Is.False);
            Assert.That(repo.NumberOfPendingChanges, Is.Zero);
        }
    }

    [Test]
    public void Refresh_SubmoduleAtWrongRef_CountsAsOneModifiedEntryOnTheParent()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddSubmoduleAtWrongRef("ext/lib");

        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repo.HasPendingChanges, Is.True);
            Assert.That(repo.NumberOfPendingChanges, Is.EqualTo(1));
        }
    }

    [Test]
    public void Refresh_UninitializedSubmodule_DoesNotMakeParentDirty()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddUninitializedSubmodule("ext/lib");

        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        Assert.That(repo.HasPendingChanges, Is.False);
    }

    [Test]
    public void Refresh_GitlinkWithoutGitmodulesDeclaration_IsNeitherReportedNorEnumerated()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddGitlinkWithoutGitmodules("orphan/lib");

        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repo.HasSubmoduleIssues, Is.False);
            Assert.That(repo.SubmoduleHealth, Is.Empty);
            Assert.That(repo.Submodules, Is.Null);
        }
    }

    [Test]
    public void Refresh_DeclaredIndexedHealthySubmodule_RecordsNoHealthEntry()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddInitializedSubmodule("ext/lib");

        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repo.HasSubmoduleIssues, Is.False);
            Assert.That(repo.SubmoduleHealth, Is.Empty);
            Assert.That(repo.Submodules, Has.Count.EqualTo(1));
        }
    }

    [TestCase(false, 1)]
    [TestCase(true, 2)]
    public void Refresh_StagedGitlinkUpdate_Issue476CountsIndexAndChildChanges(bool dirtyChild, int expectedChanges)
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddSubmoduleAtWrongRef("ext/lib");
        TempRepoFixture.RunGit(fixture.Path, "add", "ext/lib");
        if (dirtyChild)
        {
            File.WriteAllText(Path.Combine(fixture.Path, "ext/lib/untracked.txt"), "dirty");
        }

        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repo.RefreshError, Is.Null);
            Assert.That(repo.HasPendingChanges, Is.True);
            Assert.That(repo.NumberOfPendingChanges, Is.EqualTo(expectedChanges));
            Assert.That(repo.IsPublishReady, Is.False);
        }
    }

    [Test]
    public void Refresh_StagedGitlinkAddition_Issue476CountsOneIndexChange()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddInitializedSubmodule("ext/lib");
        using (var parent = new Repository(fixture.Path))
        {
            parent.Index.Remove("ext/lib");
            parent.Index.Write();
            var author = parent.Head.Tip.Author;
            parent.Commit("remove gitlink, retaining its declaration", author, author);
        }

        TempRepoFixture.RunGit(fixture.Path, "add", "ext/lib");
        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repo.RefreshError, Is.Null);
            Assert.That(repo.HasPendingChanges, Is.True);
            Assert.That(repo.NumberOfPendingChanges, Is.EqualTo(1));
        }
    }

    [TestCase(true, 1)]
    [TestCase(false, 2)]
    public void Refresh_StagedGitlinkRemoval_Issue476CountsIndexAndCurrentChild(bool deinitialize, int expectedChanges)
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var submoduleDirectory = fixture.AddInitializedSubmodule("ext/lib");
        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();
        File.WriteAllText(Path.Combine(submoduleDirectory, "untracked.txt"), "dirty");
        repo.Refresh();
        Assert.That(repo.HasPendingChanges, Is.True, "Precondition: the cached child was dirty.");

        if (deinitialize)
        {
            TempRepoFixture.RunGit(fixture.Path, "submodule", "deinit", "-f", "ext/lib");
        }

        TempRepoFixture.RunGit(fixture.Path, "rm", "--cached", "ext/lib");
        repo.Refresh();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repo.RefreshError, Is.Null);
            Assert.That(repo.HasPendingChanges, Is.True);
            Assert.That(repo.NumberOfPendingChanges, Is.EqualTo(expectedChanges));
            Assert.That(repo.Submodules!.Values.Single()!.RefreshError, Is.Null);
            Assert.That(repo.SubmoduleHealth["ext/lib"].Status, Is.EqualTo(SubmoduleHealthStatus.MissingFromIndex));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Refresh_StagedGitlinkUpdate_Issue476CliMatchesLibgit2(bool dirtyChild)
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddSubmoduleAtWrongRef("ext/lib");
        TempRepoFixture.RunGit(fixture.Path, "add", "ext/lib");
        if (dirtyChild)
        {
            File.WriteAllText(Path.Combine(fixture.Path, "ext/lib/untracked.txt"), "dirty");
        }

        using var repository = new Repository(fixture.Path);
        var expected = new GitWizardRepository(fixture.Path);
        expected.RefreshPendingChangesStatus(repository, false);
        var actual = new GitWizardRepository(fixture.Path);

        actual.RefreshPendingChangesStatus(repository, false, 0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(expected.NumberOfPendingChanges, Is.EqualTo(1));
            Assert.That(actual.RefreshError, Is.Null);
            Assert.That(actual.HasPendingChanges, Is.EqualTo(expected.HasPendingChanges));
            Assert.That(actual.NumberOfPendingChanges, Is.EqualTo(expected.NumberOfPendingChanges));
        }
    }

    [Test]
    public void Refresh_StagedGitlinkAddition_Issue476CliMatchesLibgit2()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddInitializedSubmodule("ext/lib");
        using (var parent = new Repository(fixture.Path))
        {
            parent.Index.Remove("ext/lib");
            parent.Index.Write();
            var author = parent.Head.Tip.Author;
            parent.Commit("remove gitlink, retaining its declaration", author, author);
        }

        TempRepoFixture.RunGit(fixture.Path, "add", "ext/lib");
        using var repository = new Repository(fixture.Path);
        var expected = new GitWizardRepository(fixture.Path);
        expected.RefreshPendingChangesStatus(repository, false);
        var actual = new GitWizardRepository(fixture.Path);

        actual.RefreshPendingChangesStatus(repository, false, 0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(expected.NumberOfPendingChanges, Is.EqualTo(1));
            Assert.That(actual.RefreshError, Is.Null);
            Assert.That(actual.HasPendingChanges, Is.EqualTo(expected.HasPendingChanges));
            Assert.That(actual.NumberOfPendingChanges, Is.EqualTo(expected.NumberOfPendingChanges));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Refresh_StagedGitlinkRemoval_Issue476CliMatchesLibgit2(bool deinitialize)
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var submoduleDirectory = fixture.AddInitializedSubmodule("ext/lib");
        File.WriteAllText(Path.Combine(submoduleDirectory, "untracked.txt"), "dirty");
        if (deinitialize)
        {
            TempRepoFixture.RunGit(fixture.Path, "submodule", "deinit", "-f", "ext/lib");
        }

        TempRepoFixture.RunGit(fixture.Path, "rm", "--cached", "ext/lib");
        using var repository = new Repository(fixture.Path);
        var expected = new GitWizardRepository(fixture.Path);
        expected.RefreshPendingChangesStatus(repository, false);
        var actual = new GitWizardRepository(fixture.Path);

        actual.RefreshPendingChangesStatus(repository, false, 0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(expected.NumberOfPendingChanges, Is.EqualTo(1));
            Assert.That(actual.RefreshError, Is.Null);
            Assert.That(actual.HasPendingChanges, Is.EqualTo(expected.HasPendingChanges));
            Assert.That(actual.NumberOfPendingChanges, Is.EqualTo(expected.NumberOfPendingChanges));
        }
    }

    [Test]
    public void Refresh_DirtyChildDeinitialized_Issue476DropsCachedChanges()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var submoduleDirectory = fixture.AddInitializedSubmodule("ext/lib");
        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();
        File.WriteAllText(Path.Combine(submoduleDirectory, "untracked.txt"), "dirty");
        repo.Refresh();
        Assert.That(repo.HasPendingChanges, Is.True, "Precondition: the cached child was dirty.");

        TempRepoFixture.RunGit(fixture.Path, "submodule", "deinit", "-f", "ext/lib");
        repo.Refresh();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repo.RefreshError, Is.Null);
            Assert.That(repo.HasPendingChanges, Is.False);
            Assert.That(repo.NumberOfPendingChanges, Is.Zero);
            Assert.That(repo.Submodules!.Values, Has.All.Not.Null);
            Assert.That(repo.SubmoduleHealth["ext/lib"].Status, Is.EqualTo(SubmoduleHealthStatus.Uninitialized));
        }
    }

    [Test]
    public void Refresh_DirtyChildDeclarationRemoved_Issue476DropsCachedChanges()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var submoduleDirectory = fixture.AddInitializedSubmodule("ext/lib");
        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();
        File.WriteAllText(Path.Combine(submoduleDirectory, "untracked.txt"), "dirty");
        repo.Refresh();
        Assert.That(repo.HasPendingChanges, Is.True, "Precondition: the cached child was dirty.");

        File.WriteAllText(Path.Combine(fixture.Path, ".gitmodules"), string.Empty);
        using (var parent = new Repository(fixture.Path))
        {
            Commands.Stage(parent, ".gitmodules");
            var author = parent.Head.Tip.Author;
            parent.Commit("remove submodule declaration", author, author);
        }

        repo.Refresh();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repo.RefreshError, Is.Null);
            Assert.That(repo.HasPendingChanges, Is.False);
            Assert.That(repo.NumberOfPendingChanges, Is.Zero);
            Assert.That(repo.Submodules, Is.Null.Or.Empty);
            Assert.That(repo.SubmoduleHealth, Is.Empty);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Refresh_DirtyChildRefreshFails_Issue476DropsCachedChanges(bool malformedIndex)
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var submoduleDirectory = fixture.AddInitializedSubmodule("ext/lib");
        File.WriteAllText(Path.Combine(submoduleDirectory, "untracked.txt"), "dirty");
        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();
        var child = repo.Submodules!.Values.Single()!;
        Assert.That(child.HasPendingChanges, Is.True, "Precondition: the cached child was dirty.");
        string indexPath;
        using (var childRepository = new Repository(submoduleDirectory))
        {
            indexPath = Path.Combine(childRepository.Info.Path, "index");
        }

        var indexBytes = File.ReadAllBytes(indexPath);
        child.PhaseObserverForTesting = phase =>
        {
            if (!malformedIndex)
            {
                throw new InvalidOperationException("child refresh failed");
            }

            if (phase == 4)
            {
                File.WriteAllText(indexPath, new string('x', 128));
            }
        };

        repo.Refresh();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(child.RefreshError, Is.Not.Null);
            Assert.That(repo.Submodules!.Values.Single(), Is.SameAs(child));
            Assert.That(repo.HasPendingChanges, Is.False);
            Assert.That(repo.NumberOfPendingChanges, Is.Zero);
            Assert.That(repo.Submodules!.Values, Has.All.Not.Null);
        }

        child.PhaseObserverForTesting = null;
        File.WriteAllBytes(indexPath, indexBytes);
        repo.Refresh();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repo.Submodules!.Values.Single(), Is.SameAs(child));
            Assert.That(child.RefreshError, Is.Null);
            Assert.That(repo.HasPendingChanges, Is.True);
            Assert.That(repo.NumberOfPendingChanges, Is.EqualTo(1));
        }
    }
}

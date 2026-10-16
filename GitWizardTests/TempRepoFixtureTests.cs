using LibGit2Sharp;

namespace GitWizardTests;

public class TempRepoFixtureTests
{
    [Test]
    public void IndependentOriginAdvance_PreservesLocalHeadAndTrackingReference()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddOriginRemoteAndPush();
        using var repository = new Repository(fixture.Path);
        var initialCommit = repository.Head.Tip;
        var trackedBranch = repository.Head.TrackedBranch;
        Assert.That(trackedBranch, Is.Not.Null);
        Assert.That(trackedBranch.Tip, Is.EqualTo(initialCommit));

        fixture.AdvanceOriginIndependently("remote-only.txt");

        using var origin = new Repository(repository.Network.Remotes["origin"].Url);
        Assert.Multiple(() =>
        {
            Assert.That(repository.Head.Tip, Is.EqualTo(initialCommit));
            Assert.That(repository.Head.TrackedBranch.Tip, Is.EqualTo(initialCommit));
            Assert.That(origin.Head.Tip.Parents.Single(), Is.EqualTo(initialCommit));
            Assert.That(origin.Head.Tip.Tree["remote-only.txt"], Is.Not.Null);
            Assert.That(repository.RetrieveStatus().IsDirty, Is.False);
        });
        TempRepoFixture.RunGit(fixture.Path, "diff", "--exit-code", "HEAD");
        TempRepoFixture.RunGit(origin.Info.Path, "fsck", "--no-dangling");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SubmoduleSetup_CommitsGitlinkAndLeavesExpectedCheckout(bool wrongReference)
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        const string submodulePath = "external/library";
        if (wrongReference)
            fixture.AddSubmoduleAtWrongRef(submodulePath);
        else
            fixture.AddInitializedSubmodule(submodulePath);

        using var repository = new Repository(fixture.Path);
        using var submodule = new Repository(Path.Combine(fixture.Path, submodulePath));
        using var indexChanges = repository.Diff.Compare<TreeChanges>(repository.Head.Tip.Tree, DiffTargets.Index);
        var gitlink = repository.Head.Tip.Tree[submodulePath];
        Assert.Multiple(() =>
        {
            Assert.That(gitlink.Mode, Is.EqualTo(Mode.GitLink));
            Assert.That(repository.Head.Tip.Tree[".gitmodules"], Is.Not.Null);
            Assert.That(indexChanges, Is.Empty);
            Assert.That(submodule.RetrieveStatus().IsDirty, Is.False);
            Assert.That(submodule.Head.Tip.Sha == gitlink.Target.Id.Sha, Is.EqualTo(!wrongReference));
        });
        TempRepoFixture.RunGit(fixture.Path, "diff", "--cached", "--exit-code", "HEAD");
        TempRepoFixture.RunGit(submodule.Info.WorkingDirectory, "fsck", "--no-dangling");
    }
}

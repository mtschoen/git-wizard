using GitWizard;
using GitWizardTests.TestSupport;
using LibGit2Sharp;

namespace GitWizardTests;

[TestFixture]
public class BaseBranchResolverTests
{
    static string? Resolve(GitScenarioRepo scenario, string? explicitName = null)
    {
        using var repository = new Repository(scenario.Path);
        return BaseBranchResolver.Resolve(repository, explicitName)?.FriendlyName;
    }

    [Test]
    public void NoRemote_PrefersLocalMain()
    {
        using var scenario = GitScenarioRepo.Create();
        scenario.NewBranch("feature");

        Assert.That(Resolve(scenario), Is.EqualTo("main"));
    }

    [Test]
    public void NoDefaultNames_FallsBackToCheckedOutBranch()
    {
        using var scenario = GitScenarioRepo.Create();
        scenario.Git("branch", "-q", "-m", "trunk");

        Assert.That(Resolve(scenario), Is.EqualTo("trunk"));
    }

    [Test]
    public void OriginMainExists_WinsOverLocalMain()
    {
        using var scenario = GitScenarioRepo.Create();
        scenario.AddOrigin();

        Assert.That(Resolve(scenario), Is.EqualTo("origin/main"));
    }

    [Test]
    public void OriginHead_WinsOverOriginMain()
    {
        using var scenario = GitScenarioRepo.Create();
        scenario.AddOrigin();
        scenario.Git("push", "-q", "origin", "main:develop");
        scenario.Git("fetch", "-q", "origin");
        scenario.Git("remote", "set-head", "origin", "develop");

        Assert.That(Resolve(scenario), Is.EqualTo("origin/develop"));
    }

    [Test]
    public void ExplicitName_ResolvesLocalBranchAsBefore()
    {
        using var scenario = GitScenarioRepo.Create();
        scenario.AddOrigin();

        Assert.That(Resolve(scenario, "main"), Is.EqualTo("main"));
    }

    [Test]
    public void IsBaseOrCounterpart_MatchesLocalBranchOfRemoteBase()
    {
        using var scenario = GitScenarioRepo.Create();
        scenario.AddOrigin();
        scenario.Git("branch", "feature");

        using var repository = new Repository(scenario.Path);
        var baseBranch = BaseBranchResolver.Resolve(repository);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(BaseBranchResolver.IsBaseOrCounterpart(repository.Branches["main"], baseBranch), Is.True);
            Assert.That(BaseBranchResolver.IsBaseOrCounterpart(repository.Branches["feature"], baseBranch), Is.False);
            Assert.That(BaseBranchResolver.IsBaseOrCounterpart(repository.Branches["feature"], null), Is.False);
        }
    }
}

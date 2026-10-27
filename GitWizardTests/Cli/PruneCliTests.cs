using System.Text.Json;
using GitWizard;
using GitWizard.CLI;
using GitWizardTests.TestSupport;
using static GitWizardTests.CliProcess;

namespace GitWizardTests;

[TestFixture]
public class PruneCliTests
{
    bool _originalVerboseMode;

    [SetUp]
    public void SetUp() => _originalVerboseMode = GitWizardLog.VerboseMode;

    [TearDown]
    public void TearDown() => GitWizardLog.VerboseMode = _originalVerboseMode;

    [Test]
    public void Parse_Prune_DefaultsToDryRunOverBranchesAndStashes()
    {
        var result = CliParser.Parse(["prune"]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Command, Is.EqualTo(CommandKind.Prune));
            Assert.That(result.PruneBranches, Is.True);
            Assert.That(result.PruneStashes, Is.True);
            Assert.That(result.PruneApply, Is.False);
            Assert.That(result.PruneBaseBranch, Is.Null);
            Assert.That(result.HasParseError, Is.False);
        }
    }

    [Test]
    public void Parse_Prune_WithOptions()
    {
        var result = CliParser.Parse(["prune", "--branches", "--apply", "--base", "develop", "-p", "/repo", "-f", "re", "--print-minified", "-v"]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Command, Is.EqualTo(CommandKind.Prune));
            Assert.That(result.PruneBranches, Is.True);
            Assert.That(result.PruneStashes, Is.False);
            Assert.That(result.PruneApply, Is.True);
            Assert.That(result.PruneBaseBranch, Is.EqualTo("develop"));
            Assert.That(result.PathsArgument, Is.EqualTo("/repo"));
            Assert.That(result.FilterPattern, Is.EqualTo("re"));
            Assert.That(result.Minified, Is.True);
            Assert.That(GitWizardLog.VerboseMode, Is.True);
        }
    }

    [Test]
    public void Parse_Prune_StashesOnly()
    {
        var result = CliParser.Parse(["prune", "--stashes"]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.PruneBranches, Is.False);
            Assert.That(result.PruneStashes, Is.True);
        }
    }

    [Test]
    public void Parse_Prune_HelpOption_RequestsExit()
    {
        using var output = new StringWriter();
        var original = Console.Out;
        try
        {
            Console.SetOut(output);
            Assert.That(CliParser.Parse(["prune", "--help"]).Command, Is.EqualTo(CommandKind.HelpPrinted));
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    [Test]
    public void Parse_Branches_LocalOnly()
    {
        Assert.That(CliParser.Parse(["branches", "--local-only"]).LocalOnly, Is.True);
    }

    static GitScenarioRepo CreateWithSquashMergedBranch()
    {
        var scenario = GitScenarioRepo.Create();
        scenario.NewBranch("feature");
        scenario.Commit("feature.txt", "feature", "feature");
        scenario.Checkout("main");
        scenario.SquashMerge("feature");
        return scenario;
    }

    [Test]
    public async Task Main_Prune_DryRunPrintsJsonAndKeepsBranch()
    {
        using var scenario = CreateWithSquashMergedBranch();

        var result = await RunCliAsync("prune", "--paths", scenario.Path);

        var report = JsonSerializer.Deserialize<PruneReport>(result.StandardOutput)!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ExitCode, Is.Zero, result.StandardError);
            Assert.That(report.Applied, Is.False);
            Assert.That(report.Candidates.Single(c => c.Name == "feature").Action, Is.EqualTo(PruneCandidate.WouldDelete));
            Assert.That(scenario.HasBranch("feature"), Is.True);
        }
    }

    [Test]
    public async Task Main_PruneApply_DeletesBranchAndPrintsRestoreHintToStderr()
    {
        using var scenario = CreateWithSquashMergedBranch();
        var sha = scenario.Sha("feature");

        var result = await RunCliAsync("prune", "--apply", "--paths", scenario.Path);

        var report = JsonSerializer.Deserialize<PruneReport>(result.StandardOutput)!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ExitCode, Is.Zero, result.StandardError);
            Assert.That(report.Candidates.Single(c => c.Name == "feature").Action, Is.EqualTo(PruneCandidate.Deleted));
            Assert.That(scenario.HasBranch("feature"), Is.False);
            Assert.That(result.StandardError, Does.Contain($"branch 'feature' {sha}"));
        }
    }

    [Test]
    public async Task Main_BranchesLocalOnly_ListsOnlyUnmergedLocalWork()
    {
        using var scenario = CreateWithSquashMergedBranch();
        scenario.NewBranch("local-work");
        scenario.Commit("local.txt", "local", "local work");
        scenario.Checkout("main");

        var result = await RunCliAsync("branches", "--local-only", "--paths", scenario.Path);

        var report = JsonSerializer.Deserialize<BranchInventoryReport>(result.StandardOutput)!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ExitCode, Is.Zero, result.StandardError);
            Assert.That(report.Repositories.Single().Branches.Select(b => b.Name), Is.EqualTo(["local-work"]));
        }
    }
}

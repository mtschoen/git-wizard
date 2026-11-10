using System.Text.Json;
using System.Text.Json.Nodes;
using GitWizard;
using GitWizard.CLI;
using GitWizardTests.TestSupport;

namespace GitWizardTests;

/// <summary>
/// The prune and merge commands run in-process through <see cref="Program.RunCommandCoreAsync"/>, so the
/// dispatch, report output and stderr logging are observable without a child process.
/// </summary>
[TestFixture]
[NonParallelizable]
public class ProgramReportCommandTests
{
    LocalFilesSandbox? _sandbox;

    [SetUp]
    public void SetUp()
    {
        _sandbox = new LocalFilesSandbox();
        GitWizardLog.SilentMode = false;
    }

    [TearDown]
    public void TearDown()
    {
        GitWizardLog.SilentMode = true;
        _sandbox?.Dispose();
        _sandbox = null;
    }

    [TestCase(false, TestName = "Prune_DryRun_ListsTheSquashMergedBranchAndKeepsIt")]
    [TestCase(true, TestName = "Prune_Apply_DeletesTheSquashMergedBranchAndPrintsTheRestoreHint")]
    public async Task Prune_RunsInProcess(bool apply)
    {
        using var scenario = GitScenarioRepo.Create();
        scenario.NewBranch("feature");
        scenario.Commit("feature.txt", "feature", "feature");
        scenario.Checkout("main");
        scenario.SquashMerge("feature");
        var sha = scenario.Sha("feature");
        string[] arguments = apply
            ? ["prune", "--apply", "--paths", scenario.Path]
            : ["prune", "--paths", scenario.Path];
        await using var stdout = new StringWriter();
        await using var stderr = new StringWriter();

        var exitCode = await Program.RunCommandCoreAsync(
            CliParser.Parse(arguments), new GitWizardConfiguration(), stdout: stdout, stderr: stderr);

        var report = JsonSerializer.Deserialize<PruneReport>(stdout.ToString())!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(exitCode, Is.Zero, stderr.ToString());
            Assert.That(report.Applied, Is.EqualTo(apply));
            Assert.That(report.Candidates.Single(candidate => candidate.Name == "feature").Action,
                Is.EqualTo(apply ? PruneCandidate.Deleted : PruneCandidate.WouldDelete));
            Assert.That(scenario.HasBranch("feature"), Is.EqualTo(!apply));
            Assert.That(stderr.ToString().Contains($"branch 'feature' {sha}", StringComparison.Ordinal), Is.EqualTo(apply));
        }
    }

    [Test]
    public async Task Merge_RefreshesTheNamedRepositoryIntoTheSavedReportAndKeepsOtherEntries()
    {
        using var repository = TempRepoFixture.CreateWithInitialCommit();
        using var directory = new TempDirectory("gw-merge-");
        var savePath = Path.Combine(directory.Path, "report.json");
        var existing = new JsonObject
        {
            ["Repositories"] = new JsonObject { ["/untouched"] = new JsonObject { ["Marker"] = "kept" } },
        };
        await File.WriteAllTextAsync(savePath, existing.ToJsonString());
        await using var stdout = new StringWriter();
        await using var stderr = new StringWriter();

        var exitCode = await Program.RunCommandCoreAsync(
            CliParser.Parse(["--merge", "--paths", repository.Path, "--save-path", savePath]),
            new GitWizardConfiguration(), stdout: stdout, stderr: stderr);

        var repositories = JsonNode.Parse(await File.ReadAllTextAsync(savePath))!["Repositories"]!.AsObject();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(exitCode, Is.Zero, stderr.ToString());
            Assert.That(repositories.ContainsKey(repository.Path), Is.True);
            Assert.That((string?)repositories["/untouched"]!["Marker"], Is.EqualTo("kept"));
            Assert.That(stderr.ToString(), Does.Contain($"Merging 1 repo(s) into {savePath}"));
        }
    }

    [TestCase(true, 1, TestName = "OutputReport_Silent_ExitsNonZeroForARepositoryNeedingAttention")]
    [TestCase(false, 0, TestName = "OutputReport_Silent_ExitsZeroWhenEveryRepositoryIsClean")]
    public void OutputReport_Silent_PrintsNothingAndReportsAttentionThroughTheExitCode(bool needsAttention, int expectedExitCode)
    {
        var report = new GitWizardReport();
        report.Repositories["C:/repos/a"] = new GitWizardRepository("C:/repos/a") { HasPendingChanges = needsAttention };
        using var stdout = new StringWriter();

        var exitCode = Program.OutputReport(report, new RunConfiguration { Silent = true }, stdout);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exitCode, Is.EqualTo(expectedExitCode));
            Assert.That(stdout.ToString(), Is.Empty);
        }
    }
}

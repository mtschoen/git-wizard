using System.Text.Json;
using GitWizard;
using GitWizard.CLI;
using GitWizardTests.TestSupport;
using LibGit2Sharp;

namespace GitWizardTests;

/// <summary>
/// Pins the JSON, save-path and filter contract that the sweep, tags and branches commands share
/// against normalized golden outputs captured from before the command-duplication refactoring.
/// </summary>
[TestFixture]
[NonParallelizable]
public class InventoryCommandOutputTests
{
    LocalFilesSandbox? _sandbox;
    string _temporaryHome = null!;
    string _alphaPath = null!;
    string _betaPath = null!;

    [SetUp]
    public void SetUp()
    {
        _sandbox = new LocalFilesSandbox();
        _temporaryHome = _sandbox.Path;
        _alphaPath = CreateRepository("AlphaRepo");
        _betaPath = CreateRepository("beta");
    }

    [TearDown]
    public void TearDown()
    {
        _sandbox?.Dispose();
        _sandbox = null;
    }

    string CreateRepository(string name)
    {
        var path = Path.Combine(_temporaryHome, "repositories", name);
        using var repository = new Repository(Repository.Init(path));
        if (repository.Head.FriendlyName != "main")
        {
            repository.Refs.UpdateTarget("HEAD", "refs/heads/main");
        }

        var signature = new Signature("Test", "test@example.invalid", new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
        repository.Commit("initial", signature, signature, new CommitOptions { AllowEmptyCommit = true });
        repository.ApplyTag("v1");
        repository.CreateBranch("feature");

        return path;
    }

    async Task<(int ExitCode, string Output)> RunAsync(string command, bool minified, string? filter, string? savePath = null)
    {
        var arguments = new List<string> { command == "sweep" ? "--sweep" : command };
        arguments.AddRange(["--paths", $"{_alphaPath},{_betaPath}", "--no-mft"]);
        if (minified)
        {
            arguments.Add("--print-minified");
        }

        if (filter != null)
        {
            arguments.AddRange(["--filter", filter]);
        }

        if (savePath != null)
        {
            arguments.AddRange(["--save-path", savePath]);
        }

        await using var stdout = new StringWriter();
        await using var stderr = new StringWriter();
        var exitCode = await Program.RunCommandCoreAsync(
            CliParser.Parse([.. arguments]), new GitWizardConfiguration(), stdout: stdout, stderr: stderr);
        return (exitCode, stdout.ToString());
    }

    static string ExpectedGoldenJson(string command, bool minified, IReadOnlyList<string> paths)
    {
        var items = paths.Select(path => FormatGoldenItem(command, minified, path));
        return command switch
        {
            "sweep" => minified
                ? $"{{\"SchemaVersion\":\"1.0\",\"Repositories\":[{string.Join(",", items)}]}}"
                : $"{{\n  \"SchemaVersion\": \"1.0\",\n  \"Repositories\": [\n{string.Join(",\n", items)}\n  ]\n}}".ReplaceLineEndings(),
            "tags" => minified
                ? $"{{\"SchemaVersion\":\"1.0\",\"MatchPattern\":\"*\",\"Repositories\":[{string.Join(",", items)}]}}"
                : $"{{\n  \"SchemaVersion\": \"1.0\",\n  \"MatchPattern\": \"*\",\n  \"Repositories\": [\n{string.Join(",\n", items)}\n  ]\n}}".ReplaceLineEndings(),
            _ => minified
                ? $"{{\"SchemaVersion\":\"1.0\",\"Repositories\":[{string.Join(",", items)}]}}"
                : $"{{\n  \"SchemaVersion\": \"1.0\",\n  \"Repositories\": [\n{string.Join(",\n", items)}\n  ]\n}}".ReplaceLineEndings()
        };
    }

    static string FormatGoldenItem(string command, bool minified, string path)
    {
        var effectivePath = command == "sweep"
            ? path
            : GitWizardApi.NormalizePath(path);
        var jsonPath = JsonSerializer.Serialize(effectivePath);

        return command switch
        {
            "sweep" => minified
                ? $"{{\"Path\":{jsonPath},\"DirtyTrackedFiles\":[],\"UnpushedBranches\":[{{\"Name\":\"feature\",\"CommitCount\":1}},{{\"Name\":\"main\",\"CommitCount\":1}}],\"StashCount\":0}}"
                : $"    {{\n      \"Path\": {jsonPath},\n      \"DirtyTrackedFiles\": [],\n      \"UnpushedBranches\": [\n        {{\n          \"Name\": \"feature\",\n          \"CommitCount\": 1\n        }},\n        {{\n          \"Name\": \"main\",\n          \"CommitCount\": 1\n        }}\n      ],\n      \"StashCount\": 0\n    }}".ReplaceLineEndings(),
            "tags" => minified
                ? $"{{\"Path\":{jsonPath},\"Tags\":[{{\"Name\":\"v1\",\"CommitSha\":\"ee7e7ed97cd4a41ba4011da4b6d5038d2681f7de\",\"ExistsOnRemote\":false,\"Remotes\":[]}}]}}"
                : $"    {{\n      \"Path\": {jsonPath},\n      \"Tags\": [\n        {{\n          \"Name\": \"v1\",\n          \"CommitSha\": \"ee7e7ed97cd4a41ba4011da4b6d5038d2681f7de\",\n          \"ExistsOnRemote\": false,\n          \"Remotes\": []\n        }}\n      ]\n    }}".ReplaceLineEndings(),
            _ => minified
                ? $"{{\"Path\":{jsonPath},\"DefaultBranch\":\"main\",\"Branches\":[{{\"Name\":\"feature\",\"IsMerged\":true,\"AheadOfBase\":0,\"BehindBase\":0,\"HasUpstream\":false,\"AheadOfUpstream\":0,\"BehindUpstream\":0,\"UniqueCommitCount\":0,\"LastCommitDate\":\"2026-01-02T03:04:05+00:00\"}}]}}"
                : $"    {{\n      \"Path\": {jsonPath},\n      \"DefaultBranch\": \"main\",\n      \"Branches\": [\n        {{\n          \"Name\": \"feature\",\n          \"IsMerged\": true,\n          \"AheadOfBase\": 0,\n          \"BehindBase\": 0,\n          \"HasUpstream\": false,\n          \"AheadOfUpstream\": 0,\n          \"BehindUpstream\": 0,\n          \"UniqueCommitCount\": 0,\n          \"LastCommitDate\": \"2026-01-02T03:04:05+00:00\"\n        }}\n      ]\n    }}".ReplaceLineEndings()
        };
    }

    [Test]
    public async Task Command_Output_MatchesHistoricalGoldenJson(
        [Values("sweep", "tags", "branches")] string command,
        [Values] bool minified,
        [Values(null, "ALPHA")] string? filter)
    {
        var (exitCode, output) = await RunAsync(command, minified, filter);
        var expectedPaths = filter == null
            ? new[] { _alphaPath, _betaPath }
            : new[] { _alphaPath };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exitCode, Is.Zero);
            Assert.That(output, Is.EqualTo(ExpectedGoldenJson(command, minified, expectedPaths) + Environment.NewLine));
        }
    }

    [Test]
    public async Task Command_WithSavePath_WritesTheSameJsonWithoutTrailingNewline([Values("sweep", "tags", "branches")] string command)
    {
        var savePath = Path.Combine(_temporaryHome, $"{command}.json");

        var (exitCode, output) = await RunAsync(command, minified: false, filter: null, savePath);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exitCode, Is.Zero);
            Assert.That(await File.ReadAllTextAsync(savePath), Is.EqualTo(ExpectedGoldenJson(command, false, [_alphaPath, _betaPath])));
            Assert.That(output.TrimEnd(), Is.EqualTo(await File.ReadAllTextAsync(savePath)));
        }
    }

    [TestCase(null, "/work/alpha", true)]
    [TestCase("", "/work/alpha", true)]
    [TestCase("alpha", "/work/alpha", true)]
    [TestCase("ALPHA", "/work/Alpha-Repo", true)]
    [TestCase("beta", "/work/alpha", false)]
    public void MatchesFilter_PathAndPattern_AppliesCaseInsensitiveSubstring(string? pattern, string path, bool expected)
    {
        Assert.That(Program.MatchesFilter(path, pattern), Is.EqualTo(expected));
    }
}

using System.Text.Json;
using GitWizard;
using GitWizard.CLI;
using GitWizardTests.TestSupport;

namespace GitWizardTests;

/// <summary>
/// Tests for the internal static helper methods on <see cref="Program"/>:
/// ReportNeedsAttention, ApplyFilter, ParseExplicitPaths, SerializeReport,
/// and SerializeInventoryReport.
/// </summary>
[NonParallelizable]
public class ProgramHelperTests
{

    LocalFilesSandbox? _sandbox;
    string? _tempHome;

    [SetUp]
    public void SetUp()
    {
        GitWizardLog.SilentMode = true;
        _sandbox = new LocalFilesSandbox();
        _tempHome = _sandbox.Path;
    }

    [TearDown]
    public void TearDown()
    {
        _sandbox?.Dispose();
        _sandbox = null;
    }

    static GitWizardRepository CreateRepoWithFlags(
        bool hasPendingChanges = false,
        bool localOnlyCommits = false,
        int behindRemoteCount = 0) => new()
        {
            HasPendingChanges = hasPendingChanges,
            LocalOnlyCommits = localOnlyCommits,
            BehindRemoteCount = behindRemoteCount
        };

    #region SerializeInventoryReport

    [Test]
    public void SerializeInventoryReport_DefaultValues_ArePresentForStableContract()
    {
        var report = new RepositorySweepReport();
        report.Repositories.Add(new RepositorySweepItem { Path = "/repo/clean" });

        var json = Program.SerializeInventoryReport(new RunConfiguration(), report);
        using var document = JsonDocument.Parse(json);
        var repository = document.RootElement.GetProperty("Repositories")[0];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repository.GetProperty("DirtyTrackedFiles").GetArrayLength(), Is.Zero);
            Assert.That(repository.GetProperty("UnpushedBranches").GetArrayLength(), Is.Zero);
            Assert.That(repository.GetProperty("StashCount").GetInt32(), Is.Zero);
        }
    }

    [Test]
    public void SerializeInventoryReport_Minified_HasNoIndentedNewlines()
    {
        var json = Program.SerializeInventoryReport(new RunConfiguration { Minified = true }, new RepositorySweepReport());

        Assert.That(json, Does.Not.Contain(Environment.NewLine));
    }

    #endregion

    #region ReportNeedsAttention

    static IEnumerable<TestCaseData> ReportNeedsAttentionCases()
    {
        yield return new TestCaseData(Array.Empty<GitWizardRepository>(), false)
            .SetName("ReportNeedsAttention_EmptyReport_ReturnsFalse");
        yield return new TestCaseData(new[] { CreateRepoWithFlags(hasPendingChanges: true) }, true)
            .SetName("ReportNeedsAttention_RepoWithPendingChanges_ReturnsTrue");
        yield return new TestCaseData(new[] { CreateRepoWithFlags(localOnlyCommits: true) }, true)
            .SetName("ReportNeedsAttention_RepoWithLocalOnlyCommits_ReturnsTrue");
        yield return new TestCaseData(new[] { CreateRepoWithFlags(behindRemoteCount: 3) }, true)
            .SetName("ReportNeedsAttention_RepoWithBehindRemote_ReturnsTrue");
        yield return new TestCaseData(new[] { CreateRepoWithFlags() }, false)
            .SetName("ReportNeedsAttention_RepoWithNoIssues_ReturnsFalse");
        yield return new TestCaseData(new[] { CreateRepoWithFlags(), CreateRepoWithFlags(hasPendingChanges: true) }, true)
            .SetName("ReportNeedsAttention_MultipleReposOneWithIssues_ReturnsTrue");
        yield return new TestCaseData(new[] { CreateRepoWithFlags(), CreateRepoWithFlags() }, false)
            .SetName("ReportNeedsAttention_MultipleReposAllClean_ReturnsFalse");
    }

    [TestCaseSource(nameof(ReportNeedsAttentionCases))]
    public void ReportNeedsAttention_ReflectsRepositoryFlags(GitWizardRepository[] repositories, bool expected)
    {
        var report = new GitWizardReport();
        for (var index = 0; index < repositories.Length; index++)
        {
            report.Repositories[$"/repo/{index}"] = repositories[index];
        }

        Assert.That(Program.ReportNeedsAttention(report), Is.EqualTo(expected));
    }

    #endregion

    #region ApplyFilter

    [Test]
    public void ApplyFilter_NullFilter_ReturnsSameReport()
    {
        var report = new GitWizardReport();
        report.Repositories["/repo/a"] = CreateRepoWithFlags();

        var config = new RunConfiguration();
        var result = Program.ApplyFilter(config, report);

        Assert.That(result, Is.SameAs(report));
    }

    [Test]
    public void ApplyFilter_EmptyFilter_ReturnsSameReport()
    {
        var rc = new RunConfiguration { FilterPattern = "" };

        var report = new GitWizardReport();
        report.Repositories["/repo/a"] = CreateRepoWithFlags();

        var result = Program.ApplyFilter(rc, report);
        Assert.That(result, Is.SameAs(report));
    }

    [Test]
    public void ApplyFilter_MatchingFilter_ReturnsOnlyMatchingRepos()
    {
        var report = new GitWizardReport();
        report.Repositories["/repo/alpha"] = CreateRepoWithFlags();
        report.Repositories["/repo/beta"] = CreateRepoWithFlags();
        report.Repositories["/repo/alpha-2"] = CreateRepoWithFlags();

        var rc = new RunConfiguration { FilterPattern = "alpha" };

        var result = Program.ApplyFilter(rc, report);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.Not.SameAs(report));
            Assert.That(result.Repositories, Has.Count.EqualTo(2));
            Assert.That(result.Repositories.ContainsKey("/repo/alpha"), Is.True);
            Assert.That(result.Repositories.ContainsKey("/repo/alpha-2"), Is.True);
        }
    }

    [Test]
    public void ApplyFilter_FilterMatchingNone_ReturnsEmptyRepositories()
    {
        var report = new GitWizardReport();
        report.Repositories["/repo/alpha"] = CreateRepoWithFlags();

        var rc = new RunConfiguration { FilterPattern = "nonexistent" };

        var result = Program.ApplyFilter(rc, report);
        Assert.That(result.Repositories, Is.Empty);
    }

    [Test]
    public void ApplyFilter_CaseInsensitive_MatchesRegardlessOfCase()
    {
        var report = new GitWizardReport();
        report.Repositories["/repo/MyProject"] = CreateRepoWithFlags();

        var rc = new RunConfiguration { FilterPattern = "myproject" };

        var result = Program.ApplyFilter(rc, report);
        Assert.That(result.Repositories, Has.Count.EqualTo(1));
    }

    [Test]
    public void ApplyFilter_PreservesSearchPathsAndIgnoredPaths()
    {
        var report = new GitWizardReport();
        report.SearchPaths.Add("/search/path");
        report.IgnoredPaths.Add("/ignored/path");
        report.Repositories["/repo/a"] = CreateRepoWithFlags();

        var rc = new RunConfiguration { FilterPattern = "nomatch" };

        var result = Program.ApplyFilter(rc, report);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.SearchPaths, Is.SameAs(report.SearchPaths));
            Assert.That(result.IgnoredPaths, Is.SameAs(report.IgnoredPaths));
        }
    }

    #endregion

    #region ParseExplicitPaths

    static IEnumerable<TestCaseData> ParseExplicitPathsCases()
    {
        yield return new TestCaseData(null, null)
            .SetName("ParseExplicitPaths_NullPathsArgument_ReturnsNull");
        yield return new TestCaseData(string.Empty, null)
            .SetName("ParseExplicitPaths_EmptyPathsArgument_ReturnsNull");
        yield return new TestCaseData("path1,path2,path3", new[] { "path1", "path2", "path3" })
            .SetName("ParseExplicitPaths_CommaSeparated_SplitsCorrectly");
        yield return new TestCaseData("  path1 , path2 , path3  ", new[] { "path1", "path2", "path3" })
            .SetName("ParseExplicitPaths_CommaSeparated_TrimsWhitespace");
        yield return new TestCaseData("path1,,path2,  ,path3", new[] { "path1", "path2", "path3" })
            .SetName("ParseExplicitPaths_CommaSeparated_SkipsEmptyEntries");
        yield return new TestCaseData("/single/path", new[] { "/single/path" })
            .SetName("ParseExplicitPaths_SinglePath_NoComma_ReturnsSingleElementArray");
    }

    [TestCaseSource(nameof(ParseExplicitPathsCases))]
    public void ParseExplicitPaths_InlineArgument_SplitsTrimsAndSkipsEmptyEntries(
        string? pathsArgument,
        string[]? expected)
    {
        var rc = new RunConfiguration { PathsArgument = pathsArgument };

        var result = Program.ParseExplicitPaths(rc);

        if (expected is null)
        {
            Assert.That(result, Is.Null);
        }
        else
        {
            Assert.That(result, Is.EqualTo(expected));
        }
    }

    [Test]
    public void ParseExplicitPaths_FilePathExists_ReadsLinesFromFile()
    {
        var tempFile = Path.Combine(_tempHome!, "paths.txt");
        File.WriteAllText(tempFile, "/repo/a\n/repo/b\n/repo/c\n");

        var rc = new RunConfiguration { PathsArgument = tempFile };

        var result = Program.ParseExplicitPaths(rc);
        Assert.That(result, Is.EqualTo(["/repo/a", "/repo/b", "/repo/c"]));
    }

    [Test]
    public void ParseExplicitPaths_FilePathExists_SkipsBlankLinesAndTrims()
    {
        var tempFile = Path.Combine(_tempHome!, "paths_blanks.txt");
        File.WriteAllText(tempFile, "  /repo/a  \n\n  \n/repo/b\n");

        var rc = new RunConfiguration { PathsArgument = tempFile };

        var result = Program.ParseExplicitPaths(rc);
        Assert.That(result, Is.EqualTo(["/repo/a", "/repo/b"]));
    }

    #endregion

    #region SerializeReport

    [Test]
    public void SerializeReport_NotMinified_ProducesIndentedJson()
    {
        var report = new GitWizardReport();
        report.SearchPaths.Add("/search");
        report.Repositories["/repo/a"] = CreateRepoWithFlags();

        var rc = new RunConfiguration();

        var json = Program.SerializeReport(rc, report);
        // Indented JSON has newlines
        using (Assert.EnterMultipleScope())
        {
            Assert.That(json, Does.Contain("\n"));
            Assert.That(JsonSerializer.Deserialize<JsonElement>(json).ValueKind, Is.EqualTo(JsonValueKind.Object));
        }
    }

    [Test]
    public void SerializeReport_Minified_ProducesCompactJson()
    {
        var report = new GitWizardReport();
        report.Repositories["/repo/a"] = CreateRepoWithFlags();

        var rc = new RunConfiguration { Minified = true };

        var json = Program.SerializeReport(rc, report);
        // Compact JSON should be a single line (no newlines)
        using (Assert.EnterMultipleScope())
        {
            Assert.That(json, Does.Not.Contain("\n"));
            Assert.That(JsonSerializer.Deserialize<JsonElement>(json).ValueKind, Is.EqualTo(JsonValueKind.Object));
        }
    }

    [Test]
    public void SerializeReport_SetsSchemaVersionOnReport()
    {
        var report = new GitWizardReport { SchemaVersion = "0.0" };

        var rc = new RunConfiguration();

        var json = Program.SerializeReport(rc, report);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(report.SchemaVersion, Is.EqualTo(GitWizardReport.CurrentSchemaVersion));
            Assert.That(json, Does.Contain("\"SchemaVersion\""));
            Assert.That(json, Does.Contain(GitWizardReport.CurrentSchemaVersion));
        }
    }

    [Test]
    public void SerializeReport_MinifiedAndNotMinified_ProduceSameData()
    {
        var report = new GitWizardReport();
        report.Repositories["/repo/a"] = CreateRepoWithFlags();

        var rcIndented = new RunConfiguration();
        var rcMinified = new RunConfiguration { Minified = true };

        var indented = Program.SerializeReport(rcIndented, report);
        var compact = Program.SerializeReport(rcMinified, report);

        // Both should deserialize to the same logical structure
        var indentedDoc = JsonSerializer.Deserialize<JsonElement>(indented);
        var compactDoc = JsonSerializer.Deserialize<JsonElement>(compact);

        Assert.That(compactDoc.GetProperty("SchemaVersion").GetString(),
            Is.EqualTo(indentedDoc.GetProperty("SchemaVersion").GetString()));
    }

    #endregion

    #region EnsureUsableSearchPaths tests

    [Test]
    public void EnsureUsableSearchPaths_AllInvalid_ExitsOneAndLogsSummary()
    {
        var config = new GitWizardConfiguration
        {
            SearchPaths = { "/nonexistent/path/one", "/nonexistent/path/two", "/nonexistent/path/three" }
        };

        var loggedMessages = new List<string>();
        GitWizardLog.LogMethod = msg =>
        {
            if (msg != null)
            {
                loggedMessages.Add(msg);
            }
        };
        var exitCode = -1;

        try
        {
            var result = Program.EnsureUsableSearchPaths(config, code => exitCode = code);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(result, Is.False);
                Assert.That(exitCode, Is.EqualTo(1));
                Assert.That(loggedMessages.Any(m => m.Contains("0 of 3 configured search paths are usable")), Is.True);
            }
        }
        finally
        {
            GitWizardLog.LogMethod = Console.WriteLine;
        }
    }

    [Test]
    public void EnsureUsableSearchPaths_EmptySearchPaths_ExitsOneAndLogsSummary()
    {
        var config = new GitWizardConfiguration();
        config.SearchPaths.Clear();

        var loggedMessages = new List<string>();
        GitWizardLog.LogMethod = msg =>
        {
            if (msg != null)
            {
                loggedMessages.Add(msg);
            }
        };
        var exitCode = -1;

        try
        {
            var result = Program.EnsureUsableSearchPaths(config, code => exitCode = code);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(result, Is.False);
                Assert.That(exitCode, Is.EqualTo(1));
                Assert.That(loggedMessages.Any(m => m.Contains("0 of 0 configured search paths are usable")), Is.True);
            }
        }
        finally
        {
            GitWizardLog.LogMethod = Console.WriteLine;
        }
    }

    [Test]
    public void EnsureUsableSearchPaths_WithUsablePath_ReturnsTrueWithoutExit()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var config = new GitWizardConfiguration
        {
            SearchPaths = { fixture.Path, "/nonexistent/path/two" }
        };

        var exitCalled = false;
        var result = Program.EnsureUsableSearchPaths(config, _ => exitCalled = true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.True);
            Assert.That(exitCalled, Is.False);
        }
    }

    #endregion
}

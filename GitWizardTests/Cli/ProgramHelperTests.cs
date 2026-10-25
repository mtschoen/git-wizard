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

        Assert.Multiple(() =>
        {
            Assert.That(repository.GetProperty("DirtyTrackedFiles").GetArrayLength(), Is.Zero);
            Assert.That(repository.GetProperty("UnpushedBranches").GetArrayLength(), Is.Zero);
            Assert.That(repository.GetProperty("StashCount").GetInt32(), Is.Zero);
        });
    }

    [Test]
    public void SerializeInventoryReport_Minified_HasNoIndentedNewlines()
    {
        var json = Program.SerializeInventoryReport(new RunConfiguration { Minified = true }, new RepositorySweepReport());

        Assert.That(json, Does.Not.Contain(Environment.NewLine));
    }

    #endregion

    #region ReportNeedsAttention

    [Test]
    public void ReportNeedsAttention_EmptyReport_ReturnsFalse()
    {
        var report = new GitWizardReport();
        Assert.That(Program.ReportNeedsAttention(report), Is.False);
    }

    [Test]
    public void ReportNeedsAttention_RepoWithPendingChanges_ReturnsTrue()
    {
        var report = new GitWizardReport();
        report.Repositories["/repo/a"] = CreateRepoWithFlags(hasPendingChanges: true);
        Assert.That(Program.ReportNeedsAttention(report), Is.True);
    }

    [Test]
    public void ReportNeedsAttention_RepoWithLocalOnlyCommits_ReturnsTrue()
    {
        var report = new GitWizardReport();
        report.Repositories["/repo/a"] = CreateRepoWithFlags(localOnlyCommits: true);
        Assert.That(Program.ReportNeedsAttention(report), Is.True);
    }

    [Test]
    public void ReportNeedsAttention_RepoWithBehindRemote_ReturnsTrue()
    {
        var report = new GitWizardReport();
        report.Repositories["/repo/a"] = CreateRepoWithFlags(behindRemoteCount: 3);
        Assert.That(Program.ReportNeedsAttention(report), Is.True);
    }

    [Test]
    public void ReportNeedsAttention_RepoWithNoIssues_ReturnsFalse()
    {
        var report = new GitWizardReport();
        report.Repositories["/repo/a"] = CreateRepoWithFlags();
        Assert.That(Program.ReportNeedsAttention(report), Is.False);
    }

    [Test]
    public void ReportNeedsAttention_MultipleReposOneWithIssues_ReturnsTrue()
    {
        var report = new GitWizardReport();
        report.Repositories["/repo/clean"] = CreateRepoWithFlags();
        report.Repositories["/repo/dirty"] = CreateRepoWithFlags(hasPendingChanges: true);
        Assert.That(Program.ReportNeedsAttention(report), Is.True);
    }

    [Test]
    public void ReportNeedsAttention_MultipleReposAllClean_ReturnsFalse()
    {
        var report = new GitWizardReport();
        report.Repositories["/repo/a"] = CreateRepoWithFlags();
        report.Repositories["/repo/b"] = CreateRepoWithFlags();
        Assert.That(Program.ReportNeedsAttention(report), Is.False);
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
        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Not.SameAs(report));
            Assert.That(result.Repositories, Has.Count.EqualTo(2));
            Assert.That(result.Repositories.ContainsKey("/repo/alpha"), Is.True);
            Assert.That(result.Repositories.ContainsKey("/repo/alpha-2"), Is.True);
        });
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
        Assert.Multiple(() =>
        {
            Assert.That(result.SearchPaths, Is.SameAs(report.SearchPaths));
            Assert.That(result.IgnoredPaths, Is.SameAs(report.IgnoredPaths));
        });
    }

    #endregion

    #region ParseExplicitPaths

    [Test]
    public void ParseExplicitPaths_NullPathsArgument_ReturnsNull()
    {
        var rc = new RunConfiguration();
        // PathsArgument is null by default
        Assert.That(Program.ParseExplicitPaths(rc), Is.Null);
    }

    [Test]
    public void ParseExplicitPaths_EmptyPathsArgument_ReturnsNull()
    {
        var rc = new RunConfiguration { PathsArgument = "" };
        Assert.That(Program.ParseExplicitPaths(rc), Is.Null);
    }

    [Test]
    public void ParseExplicitPaths_CommaSeparated_SplitsCorrectly()
    {
        var rc = new RunConfiguration { PathsArgument = "path1,path2,path3" };

        var result = Program.ParseExplicitPaths(rc);
        Assert.That(result, Is.EqualTo(["path1", "path2", "path3"]));
    }

    [Test]
    public void ParseExplicitPaths_CommaSeparated_TrimsWhitespace()
    {
        var rc = new RunConfiguration { PathsArgument = "  path1 , path2 , path3  " };

        var result = Program.ParseExplicitPaths(rc);
        Assert.That(result, Is.EqualTo(["path1", "path2", "path3"]));
    }

    [Test]
    public void ParseExplicitPaths_CommaSeparated_SkipsEmptyEntries()
    {
        var rc = new RunConfiguration { PathsArgument = "path1,,path2,  ,path3" };

        var result = Program.ParseExplicitPaths(rc);
        Assert.That(result, Is.EqualTo(["path1", "path2", "path3"]));
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

    [Test]
    public void ParseExplicitPaths_SinglePath_NoComma_ReturnsSingleElementArray()
    {
        var rc = new RunConfiguration { PathsArgument = "/single/path" };

        var result = Program.ParseExplicitPaths(rc);
        Assert.That(result, Is.EqualTo(["/single/path"]));
    }

    #endregion

    #region SerializeReport

    [Test]
    public void SerializeReport_NotMinified_ProducesIndentedJson()
    {
        var report = new GitWizardReport();
        report.Repositories["/repo/a"] = CreateRepoWithFlags();

        var rc = new RunConfiguration();

        var json = Program.SerializeReport(rc, report);
        // Indented JSON has newlines
        Assert.That(json, Does.Contain("\n"));
    }

    [Test]
    public void SerializeReport_Minified_ProducesCompactJson()
    {
        var report = new GitWizardReport();
        report.Repositories["/repo/a"] = CreateRepoWithFlags();

        var rc = new RunConfiguration { Minified = true };

        var json = Program.SerializeReport(rc, report);
        // Compact JSON should be a single line (no newlines)
        Assert.That(json, Does.Not.Contain("\n"));
    }

    [Test]
    public void SerializeReport_SetsSchemaVersionOnReport()
    {
        var report = new GitWizardReport { SchemaVersion = "0.0" };

        var rc = new RunConfiguration();

        Program.SerializeReport(rc, report);
        Assert.That(report.SchemaVersion, Is.EqualTo(GitWizardReport.CurrentSchemaVersion));
    }

    [Test]
    public void SerializeReport_OutputIsValidJson()
    {
        var report = new GitWizardReport();
        report.SearchPaths.Add("/search");
        report.Repositories["/repo/a"] = CreateRepoWithFlags();

        var rc = new RunConfiguration();

        var json = Program.SerializeReport(rc, report);
        // Should not throw
        var deserialized = JsonSerializer.Deserialize<JsonElement>(json);
        Assert.That(deserialized.ValueKind, Is.EqualTo(JsonValueKind.Object));
    }

    [Test]
    public void SerializeReport_ContainsSchemaVersionInOutput()
    {
        var report = new GitWizardReport();

        var rc = new RunConfiguration();

        var json = Program.SerializeReport(rc, report);
        Assert.That(json, Does.Contain("\"SchemaVersion\""));
        Assert.That(json, Does.Contain(GitWizardReport.CurrentSchemaVersion));
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
            Assert.Multiple(() =>
            {
                Assert.That(result, Is.False);
                Assert.That(exitCode, Is.EqualTo(1));
                Assert.That(loggedMessages.Any(m => m.Contains("0 of 3 configured search paths are usable")), Is.True);
            });
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
            Assert.Multiple(() =>
            {
                Assert.That(result, Is.False);
                Assert.That(exitCode, Is.EqualTo(1));
                Assert.That(loggedMessages.Any(m => m.Contains("0 of 0 configured search paths are usable")), Is.True);
            });
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

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.True);
            Assert.That(exitCalled, Is.False);
        });
    }

    #endregion
}

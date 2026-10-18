using System.Text.Json;
using GitWizard;
using GitWizard.CLI;

namespace GitWizardTests;

/// <summary>
/// Tests for the internal static helper methods on <see cref="Program"/>:
/// ReportNeedsAttention, ApplyFilter, ParseExplicitPaths,
/// SerializeReport, and TryHandleElevatedHelperModes.
/// </summary>
public partial class ProgramHelperTests
{
    static readonly RunConfigurationResult DefaultConfiguration = new(
        RebuildRepositoryList: false, RebuildReport: false, ClearCache: false,
        DeleteAllLocalFiles: false, SetupDefender: false, ScanOnly: false, NoMft: false,
        FilterPattern: null, PathsArgument: null, Summary: false, Sweep: false,
        Merge: false, RefreshReport: false, Minified: false, SavePath: null,
        CustomConfigurationPath: null, DbSize: false, AllBranches: false,
        Watch: false, NoLocalCommitCount: false, HasError: false);

    string? _tempHome;

    [SetUp]
    public void SetUp()
    {
        GitWizardLog.SilentMode = true;
        TestUtilities.ResetStaticCaches();
        _tempHome = TestUtilities.RedirectLocalFilesToTemp();
    }

    [TearDown]
    public void TearDown()
    {
        TestUtilities.ResetStaticCaches();
        TestUtilities.ClearLocalFilesRedirect(_tempHome);
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

    #region SerializeSweepReport

    [Test]
    public void SerializeSweepReport_DefaultValues_ArePresentForStableContract()
    {
        var report = new RepositorySweepReport();
        report.Repositories.Add(new RepositorySweepItem { Path = "/repo/clean" });
        var config = new Program.RunConfiguration(DefaultConfiguration with { RefreshReport = true });

        var json = Program.SerializeSweepReport(config, report);
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
    public void SerializeSweepReport_Minified_HasNoIndentedNewlines()
    {
        var config = new Program.RunConfiguration(DefaultConfiguration with { RefreshReport = true, Minified = true });

        var json = Program.SerializeSweepReport(config, new RepositorySweepReport());

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

        var config = new Program.RunConfiguration(DefaultConfiguration with { RefreshReport = true });
        var result = Program.ApplyFilter(config, report);

        Assert.That(result, Is.SameAs(report));
    }

    [Test]
    public void ApplyFilter_EmptyFilter_ReturnsSameReport()
    {
        var rc = new Program.RunConfiguration(DefaultConfiguration with { FilterPattern = "" });

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

        var rc = new Program.RunConfiguration(DefaultConfiguration with { FilterPattern = "alpha" });

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

        var rc = new Program.RunConfiguration(DefaultConfiguration with { FilterPattern = "nonexistent" });

        var result = Program.ApplyFilter(rc, report);
        Assert.That(result.Repositories, Is.Empty);
    }

    [Test]
    public void ApplyFilter_CaseInsensitive_MatchesRegardlessOfCase()
    {
        var report = new GitWizardReport();
        report.Repositories["/repo/MyProject"] = CreateRepoWithFlags();

        var rc = new Program.RunConfiguration(DefaultConfiguration with { FilterPattern = "myproject" });

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

        var rc = new Program.RunConfiguration(DefaultConfiguration with { FilterPattern = "nomatch" });

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
        var rc = new Program.RunConfiguration(DefaultConfiguration);
        // PathsArgument is null by default
        Assert.That(Program.ParseExplicitPaths(rc), Is.Null);
    }

    [Test]
    public void ParseExplicitPaths_EmptyPathsArgument_ReturnsNull()
    {
        var rc = new Program.RunConfiguration(DefaultConfiguration with { PathsArgument = "" });
        Assert.That(Program.ParseExplicitPaths(rc), Is.Null);
    }

    [Test]
    public void ParseExplicitPaths_CommaSeparated_SplitsCorrectly()
    {
        var rc = new Program.RunConfiguration(DefaultConfiguration with { PathsArgument = "path1,path2,path3" });

        var result = Program.ParseExplicitPaths(rc);
        Assert.That(result, Is.EqualTo(new[] { "path1", "path2", "path3" }));
    }

    [Test]
    public void ParseExplicitPaths_CommaSeparated_TrimsWhitespace()
    {
        var rc = new Program.RunConfiguration(DefaultConfiguration with { PathsArgument = "  path1 , path2 , path3  " });

        var result = Program.ParseExplicitPaths(rc);
        Assert.That(result, Is.EqualTo(new[] { "path1", "path2", "path3" }));
    }

    [Test]
    public void ParseExplicitPaths_CommaSeparated_SkipsEmptyEntries()
    {
        var rc = new Program.RunConfiguration(DefaultConfiguration with { PathsArgument = "path1,,path2,  ,path3" });

        var result = Program.ParseExplicitPaths(rc);
        Assert.That(result, Is.EqualTo(new[] { "path1", "path2", "path3" }));
    }

    [Test]
    public void ParseExplicitPaths_FilePathExists_ReadsLinesFromFile()
    {
        var tempFile = Path.Combine(_tempHome!, "paths.txt");
        File.WriteAllText(tempFile, "/repo/a\n/repo/b\n/repo/c\n");

        var rc = new Program.RunConfiguration(DefaultConfiguration with { PathsArgument = tempFile });

        var result = Program.ParseExplicitPaths(rc);
        Assert.That(result, Is.EqualTo(new[] { "/repo/a", "/repo/b", "/repo/c" }));
    }

    [Test]
    public void ParseExplicitPaths_FilePathExists_SkipsBlankLinesAndTrims()
    {
        var tempFile = Path.Combine(_tempHome!, "paths_blanks.txt");
        File.WriteAllText(tempFile, "  /repo/a  \n\n  \n/repo/b\n");

        var rc = new Program.RunConfiguration(DefaultConfiguration with { PathsArgument = tempFile });

        var result = Program.ParseExplicitPaths(rc);
        Assert.That(result, Is.EqualTo(new[] { "/repo/a", "/repo/b" }));
    }

    [Test]
    public void ParseExplicitPaths_SinglePath_NoComma_ReturnsSingleElementArray()
    {
        var rc = new Program.RunConfiguration(DefaultConfiguration with { PathsArgument = "/single/path" });

        var result = Program.ParseExplicitPaths(rc);
        Assert.That(result, Is.EqualTo(new[] { "/single/path" }));
    }

    #endregion

    #region SerializeReport

    [Test]
    public void SerializeReport_NotMinified_ProducesIndentedJson()
    {
        var report = new GitWizardReport();
        report.Repositories["/repo/a"] = CreateRepoWithFlags();

        var rc = new Program.RunConfiguration(DefaultConfiguration);

        var json = Program.SerializeReport(rc, report);
        // Indented JSON has newlines
        Assert.That(json, Does.Contain("\n"));
    }

    [Test]
    public void SerializeReport_Minified_ProducesCompactJson()
    {
        var report = new GitWizardReport();
        report.Repositories["/repo/a"] = CreateRepoWithFlags();

        var rc = new Program.RunConfiguration(DefaultConfiguration with { Minified = true });

        var json = Program.SerializeReport(rc, report);
        // Compact JSON should be a single line (no newlines)
        Assert.That(json, Does.Not.Contain("\n"));
    }

    [Test]
    public void SerializeReport_SetsSchemaVersionOnReport()
    {
        var report = new GitWizardReport { SchemaVersion = "0.0" };

        var rc = new Program.RunConfiguration(DefaultConfiguration);

        Program.SerializeReport(rc, report);
        Assert.That(report.SchemaVersion, Is.EqualTo(GitWizardReport.CurrentSchemaVersion));
    }

    [Test]
    public void SerializeReport_OutputIsValidJson()
    {
        var report = new GitWizardReport();
        report.SearchPaths.Add("/search");
        report.Repositories["/repo/a"] = CreateRepoWithFlags();

        var rc = new Program.RunConfiguration(DefaultConfiguration);

        var json = Program.SerializeReport(rc, report);
        // Should not throw
        var deserialized = JsonSerializer.Deserialize<JsonElement>(json);
        Assert.That(deserialized.ValueKind, Is.EqualTo(JsonValueKind.Object));
    }

    [Test]
    public void SerializeReport_ContainsSchemaVersionInOutput()
    {
        var report = new GitWizardReport();

        var rc = new Program.RunConfiguration(DefaultConfiguration);

        var json = Program.SerializeReport(rc, report);
        Assert.That(json, Does.Contain($"\"SchemaVersion\""));
        Assert.That(json, Does.Contain(GitWizardReport.CurrentSchemaVersion));
    }

    [Test]
    public void SerializeReport_MinifiedAndNotMinified_ProduceSameData()
    {
        var report = new GitWizardReport();
        report.Repositories["/repo/a"] = CreateRepoWithFlags();

        var rcIndented = new Program.RunConfiguration(DefaultConfiguration);
        var rcMinified = new Program.RunConfiguration(DefaultConfiguration with { Minified = true });

        var indented = Program.SerializeReport(rcIndented, report);
        var compact = Program.SerializeReport(rcMinified, report);

        // Both should deserialize to the same logical structure
        var indentedDoc = JsonSerializer.Deserialize<JsonElement>(indented);
        var compactDoc = JsonSerializer.Deserialize<JsonElement>(compact);

        Assert.That(compactDoc.GetProperty("SchemaVersion").GetString(),
            Is.EqualTo(indentedDoc.GetProperty("SchemaVersion").GetString()));
    }

    #endregion

    #region TryHandleElevatedHelperModes

    [Test]
    public void TryHandleElevatedHelperModes_NoElevatedArgs_ReturnsFalse()
    {
        var result = Program.TryHandleElevatedHelperModes(new[] { "git-wizard.exe", "-summary" });
        Assert.That(result, Is.False);
    }

    [Test]
    public void TryHandleElevatedHelperModes_EmptyArgs_ReturnsFalse()
    {
        var result = Program.TryHandleElevatedHelperModes(Array.Empty<string>());
        Assert.That(result, Is.False);
    }

    [Test]
    public void TryHandleElevatedHelperModes_UnrelatedArgs_ReturnsFalse()
    {
        var result = Program.TryHandleElevatedHelperModes(
            new[] { "git-wizard.exe", "-filter", "test", "-print-minified" });
        Assert.That(result, Is.False);
    }

    #endregion

}

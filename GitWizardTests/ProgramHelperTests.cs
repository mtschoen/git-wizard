// FormatterServices.GetUninitializedObject is the only way to create the RunConfiguration
// struct without invoking its constructor (which calls Environment.GetCommandLineArgs).
#pragma warning disable SYSLIB0050 // FormatterServices is obsolete

using System.Reflection;
using System.Runtime.Serialization;
using System.Text.Json;
using GitWizard;
using GitWizard.CLI;

namespace GitWizardTests;

/// <summary>
/// Tests for the private static helper methods on <see cref="Program"/>:
/// FormatSize, ReportNeedsAttention, ApplyFilter, ParseExplicitPaths,
/// SerializeReport, and TryHandleElevatedHelperModes.
/// Uses reflection because these methods are private.
/// </summary>
public class ProgramHelperTests
{
    static readonly Type ProgramType = typeof(Program);
    static readonly Type RunConfigType = ProgramType.GetNestedType("RunConfiguration", BindingFlags.NonPublic)!;

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

    #region Reflection helpers

    static MethodInfo GetPrivateStaticMethod(string name, Type declaringType)
    {
        return declaringType.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;
    }

    /// <summary>
    /// Creates a RunConfiguration struct via <see cref="FormatterServices.GetUninitializedObject"/>
    /// (bypasses the constructor that calls Environment.GetCommandLineArgs) and sets the
    /// specified fields via reflection.
    /// </summary>
    static object CreateRunConfig(Action<Type, object>? configure = null)
    {
        var instance = FormatterServices.GetUninitializedObject(RunConfigType);
        // RefreshReport defaults to true in the real struct, but GetUninitializedObject
        // zeroes everything. Set it explicitly so tests that don't touch it get the real default.
        SetRunConfigField(RunConfigType, ref instance, "RefreshReport", true);
        configure?.Invoke(RunConfigType, instance);
        // Re-box after all mutations for value-type correctness
        return instance;
    }

    /// <summary>
    /// Sets a readonly field on a boxed RunConfiguration struct.
    /// The caller must re-read the boxed reference after calling this (the box is mutated in-place).
    /// </summary>
    static void SetRunConfigField(Type type, ref object boxed, string fieldName, object value)
    {
        var field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (field == null)
            throw new ArgumentException($"No field '{fieldName}' on {type.Name}");

        field.SetValue(boxed, value);
    }

    /// <summary>
    /// Invokes the private static FormatSize(long) method on Program.
    /// </summary>
    static string InvokeFormatSize(long bytes)
    {
        var method = GetPrivateStaticMethod("FormatSize", ProgramType);
        return (string)method.Invoke(null, new object[] { bytes })!;
    }

    /// <summary>
    /// Invokes the private static ReportNeedsAttention(RunConfiguration, GitWizardReport) method.
    /// </summary>
    static bool InvokeReportNeedsAttention(GitWizardReport report)
    {
        var method = GetPrivateStaticMethod("ReportNeedsAttention", ProgramType);
        return (bool)method.Invoke(null, new object[] { report })!;
    }

    /// <summary>
    /// Invokes the private static ApplyFilter(RunConfiguration, GitWizardReport) method.
    /// </summary>
    static GitWizardReport InvokeApplyFilter(object runConfig, GitWizardReport report)
    {
        var method = GetPrivateStaticMethod("ApplyFilter", ProgramType);
        return (GitWizardReport)method.Invoke(null, new[] { runConfig, report })!;
    }

    /// <summary>
    /// Invokes the private static ParseExplicitPaths(RunConfiguration) method.
    /// </summary>
    static string[]? InvokeParseExplicitPaths(object runConfig)
    {
        var method = GetPrivateStaticMethod("ParseExplicitPaths", ProgramType);
        return (string[]?)method.Invoke(null, new[] { runConfig });
    }

    /// <summary>
    /// Invokes the private static SerializeReport(RunConfiguration, GitWizardReport) method.
    /// </summary>
    static string InvokeSerializeReport(object runConfig, GitWizardReport report)
    {
        var method = GetPrivateStaticMethod("SerializeReport", ProgramType);
        return (string)method.Invoke(null, new[] { runConfig, report })!;
    }

    /// <summary>
    /// Invokes the private static TryHandleElevatedHelperModes(string[]) method.
    /// </summary>
    static bool InvokeTryHandleElevatedHelperModes(string[] args)
    {
        var method = GetPrivateStaticMethod("TryHandleElevatedHelperModes", ProgramType);
        return (bool)method.Invoke(null, new object[] { args })!;
    }

    /// <summary>
    /// Sets a private-set property on <see cref="GitWizardRepository"/> via reflection.
    /// The parameterless constructor is private, so we use GetUninitializedObject.
    /// </summary>
    static GitWizardRepository CreateRepoWithFlags(
        bool hasPendingChanges = false,
        bool localOnlyCommits = false,
        int behindRemoteCount = 0)
    {
        var repo = (GitWizardRepository)FormatterServices.GetUninitializedObject(typeof(GitWizardRepository));
        var type = typeof(GitWizardRepository);

        type.GetProperty("HasPendingChanges")!
            .GetSetMethod(true)!
            .Invoke(repo, new object[] { hasPendingChanges });

        type.GetProperty("LocalOnlyCommits")!
            .GetSetMethod(true)!
            .Invoke(repo, new object[] { localOnlyCommits });

        type.GetProperty("BehindRemoteCount")!
            .GetSetMethod(true)!
            .Invoke(repo, new object[] { behindRemoteCount });

        return repo;
    }

    #endregion

    #region FormatSize

    [Test]
    public void FormatSize_ZeroBytes_Returns0B()
    {
        Assert.That(InvokeFormatSize(0L), Is.EqualTo("0 B"));
    }

    [Test]
    public void FormatSize_BytesBelowKB_ReturnsBytes()
    {
        Assert.That(InvokeFormatSize(1023L), Is.EqualTo("1023 B"));
    }

    [Test]
    public void FormatSize_ExactlyOneKB_Returns1KB()
    {
        Assert.That(InvokeFormatSize(1024L), Is.EqualTo("1 KB"));
    }

    [Test]
    public void FormatSize_ExactlyOneMB_Returns1MB()
    {
        Assert.That(InvokeFormatSize(1024L * 1024), Is.EqualTo("1 MB"));
    }

    [Test]
    public void FormatSize_ExactlyOneGB_Returns1GB()
    {
        Assert.That(InvokeFormatSize(1024L * 1024 * 1024), Is.EqualTo("1 GB"));
    }

    [Test]
    public void FormatSize_ExactlyOneTB_Returns1TB()
    {
        Assert.That(InvokeFormatSize(1024L * 1024 * 1024 * 1024), Is.EqualTo("1 TB"));
    }

    [Test]
    public void FormatSize_FractionalKB_ShowsDecimal()
    {
        // 1536 bytes = 1.5 KB
        Assert.That(InvokeFormatSize(1536L), Is.EqualTo("1.5 KB"));
    }

    [Test]
    public void FormatSize_FractionalMB_ShowsDecimal()
    {
        // 1.25 MB = 1310720 bytes
        Assert.That(InvokeFormatSize(1310720L), Is.EqualTo("1.25 MB"));
    }

    [Test]
    public void FormatSize_LargeGB_FormatsCorrectly()
    {
        // 5 GB exactly
        Assert.That(InvokeFormatSize(5L * 1024 * 1024 * 1024), Is.EqualTo("5 GB"));
    }

    [Test]
    public void FormatSize_OneByteExact_Returns1B()
    {
        Assert.That(InvokeFormatSize(1L), Is.EqualTo("1 B"));
    }

    #endregion

    #region ReportNeedsAttention

    [Test]
    public void ReportNeedsAttention_EmptyReport_ReturnsFalse()
    {
        var report = new GitWizardReport();
        Assert.That(InvokeReportNeedsAttention(report), Is.False);
    }

    [Test]
    public void ReportNeedsAttention_RepoWithPendingChanges_ReturnsTrue()
    {
        var report = new GitWizardReport();
        report.Repositories["/repo/a"] = CreateRepoWithFlags(hasPendingChanges: true);
        Assert.That(InvokeReportNeedsAttention(report), Is.True);
    }

    [Test]
    public void ReportNeedsAttention_RepoWithLocalOnlyCommits_ReturnsTrue()
    {
        var report = new GitWizardReport();
        report.Repositories["/repo/a"] = CreateRepoWithFlags(localOnlyCommits: true);
        Assert.That(InvokeReportNeedsAttention(report), Is.True);
    }

    [Test]
    public void ReportNeedsAttention_RepoWithBehindRemote_ReturnsTrue()
    {
        var report = new GitWizardReport();
        report.Repositories["/repo/a"] = CreateRepoWithFlags(behindRemoteCount: 3);
        Assert.That(InvokeReportNeedsAttention(report), Is.True);
    }

    [Test]
    public void ReportNeedsAttention_RepoWithNoIssues_ReturnsFalse()
    {
        var report = new GitWizardReport();
        report.Repositories["/repo/a"] = CreateRepoWithFlags();
        Assert.That(InvokeReportNeedsAttention(report), Is.False);
    }

    [Test]
    public void ReportNeedsAttention_MultipleReposOneWithIssues_ReturnsTrue()
    {
        var report = new GitWizardReport();
        report.Repositories["/repo/clean"] = CreateRepoWithFlags();
        report.Repositories["/repo/dirty"] = CreateRepoWithFlags(hasPendingChanges: true);
        Assert.That(InvokeReportNeedsAttention(report), Is.True);
    }

    [Test]
    public void ReportNeedsAttention_MultipleReposAllClean_ReturnsFalse()
    {
        var report = new GitWizardReport();
        report.Repositories["/repo/a"] = CreateRepoWithFlags();
        report.Repositories["/repo/b"] = CreateRepoWithFlags();
        Assert.That(InvokeReportNeedsAttention(report), Is.False);
    }

    #endregion

    #region ApplyFilter

    [Test]
    public void ApplyFilter_NullFilter_ReturnsSameReport()
    {
        var report = new GitWizardReport();
        report.Repositories["/repo/a"] = CreateRepoWithFlags();

        var config = CreateRunConfig();
        // FilterPattern is null by default (uninitialized)
        var result = InvokeApplyFilter(config, report);

        Assert.That(result, Is.SameAs(report));
    }

    [Test]
    public void ApplyFilter_EmptyFilter_ReturnsSameReport()
    {
        CreateRunConfig((type, box) => SetRunConfigField(type, ref box, "FilterPattern", ""));
        // Need to re-create because SetRunConfigField mutates the box in place but
        // CreateRunConfig returns the original box. For struct fields, re-box:
        var rc = FormatterServices.GetUninitializedObject(RunConfigType);
        SetRunConfigField(RunConfigType, ref rc, "FilterPattern", "");

        var report = new GitWizardReport();
        report.Repositories["/repo/a"] = CreateRepoWithFlags();

        var result = InvokeApplyFilter(rc, report);
        Assert.That(result, Is.SameAs(report));
    }

    [Test]
    public void ApplyFilter_MatchingFilter_ReturnsOnlyMatchingRepos()
    {
        var report = new GitWizardReport();
        report.Repositories["/repo/alpha"] = CreateRepoWithFlags();
        report.Repositories["/repo/beta"] = CreateRepoWithFlags();
        report.Repositories["/repo/alpha-2"] = CreateRepoWithFlags();

        var rc = FormatterServices.GetUninitializedObject(RunConfigType);
        SetRunConfigField(RunConfigType, ref rc, "FilterPattern", "alpha");

        var result = InvokeApplyFilter(rc, report);
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

        var rc = FormatterServices.GetUninitializedObject(RunConfigType);
        SetRunConfigField(RunConfigType, ref rc, "FilterPattern", "nonexistent");

        var result = InvokeApplyFilter(rc, report);
        Assert.That(result.Repositories, Is.Empty);
    }

    [Test]
    public void ApplyFilter_CaseInsensitive_MatchesRegardlessOfCase()
    {
        var report = new GitWizardReport();
        report.Repositories["/repo/MyProject"] = CreateRepoWithFlags();

        var rc = FormatterServices.GetUninitializedObject(RunConfigType);
        SetRunConfigField(RunConfigType, ref rc, "FilterPattern", "myproject");

        var result = InvokeApplyFilter(rc, report);
        Assert.That(result.Repositories, Has.Count.EqualTo(1));
    }

    [Test]
    public void ApplyFilter_PreservesSearchPathsAndIgnoredPaths()
    {
        var report = new GitWizardReport();
        report.SearchPaths.Add("/search/path");
        report.IgnoredPaths.Add("/ignored/path");
        report.Repositories["/repo/a"] = CreateRepoWithFlags();

        var rc = FormatterServices.GetUninitializedObject(RunConfigType);
        SetRunConfigField(RunConfigType, ref rc, "FilterPattern", "nomatch");

        var result = InvokeApplyFilter(rc, report);
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
        var rc = FormatterServices.GetUninitializedObject(RunConfigType);
        // PathsArgument is null by default
        Assert.That(InvokeParseExplicitPaths(rc), Is.Null);
    }

    [Test]
    public void ParseExplicitPaths_EmptyPathsArgument_ReturnsNull()
    {
        var rc = FormatterServices.GetUninitializedObject(RunConfigType);
        SetRunConfigField(RunConfigType, ref rc, "PathsArgument", "");
        Assert.That(InvokeParseExplicitPaths(rc), Is.Null);
    }

    [Test]
    public void ParseExplicitPaths_CommaSeparated_SplitsCorrectly()
    {
        var rc = FormatterServices.GetUninitializedObject(RunConfigType);
        SetRunConfigField(RunConfigType, ref rc, "PathsArgument", "path1,path2,path3");

        var result = InvokeParseExplicitPaths(rc);
        Assert.That(result, Is.EqualTo(new[] { "path1", "path2", "path3" }));
    }

    [Test]
    public void ParseExplicitPaths_CommaSeparated_TrimsWhitespace()
    {
        var rc = FormatterServices.GetUninitializedObject(RunConfigType);
        SetRunConfigField(RunConfigType, ref rc, "PathsArgument", "  path1 , path2 , path3  ");

        var result = InvokeParseExplicitPaths(rc);
        Assert.That(result, Is.EqualTo(new[] { "path1", "path2", "path3" }));
    }

    [Test]
    public void ParseExplicitPaths_CommaSeparated_SkipsEmptyEntries()
    {
        var rc = FormatterServices.GetUninitializedObject(RunConfigType);
        SetRunConfigField(RunConfigType, ref rc, "PathsArgument", "path1,,path2,  ,path3");

        var result = InvokeParseExplicitPaths(rc);
        Assert.That(result, Is.EqualTo(new[] { "path1", "path2", "path3" }));
    }

    [Test]
    public void ParseExplicitPaths_FilePathExists_ReadsLinesFromFile()
    {
        var tempFile = Path.Combine(_tempHome!, "paths.txt");
        File.WriteAllText(tempFile, "/repo/a\n/repo/b\n/repo/c\n");

        var rc = FormatterServices.GetUninitializedObject(RunConfigType);
        SetRunConfigField(RunConfigType, ref rc, "PathsArgument", tempFile);

        var result = InvokeParseExplicitPaths(rc);
        Assert.That(result, Is.EqualTo(new[] { "/repo/a", "/repo/b", "/repo/c" }));
    }

    [Test]
    public void ParseExplicitPaths_FilePathExists_SkipsBlankLinesAndTrims()
    {
        var tempFile = Path.Combine(_tempHome!, "paths_blanks.txt");
        File.WriteAllText(tempFile, "  /repo/a  \n\n  \n/repo/b\n");

        var rc = FormatterServices.GetUninitializedObject(RunConfigType);
        SetRunConfigField(RunConfigType, ref rc, "PathsArgument", tempFile);

        var result = InvokeParseExplicitPaths(rc);
        Assert.That(result, Is.EqualTo(new[] { "/repo/a", "/repo/b" }));
    }

    [Test]
    public void ParseExplicitPaths_SinglePath_NoComma_ReturnsSingleElementArray()
    {
        var rc = FormatterServices.GetUninitializedObject(RunConfigType);
        SetRunConfigField(RunConfigType, ref rc, "PathsArgument", "/single/path");

        var result = InvokeParseExplicitPaths(rc);
        Assert.That(result, Is.EqualTo(new[] { "/single/path" }));
    }

    #endregion

    #region SerializeReport

    [Test]
    public void SerializeReport_NotMinified_ProducesIndentedJson()
    {
        var report = new GitWizardReport();
        report.Repositories["/repo/a"] = CreateRepoWithFlags();

        var rc = FormatterServices.GetUninitializedObject(RunConfigType);
        // Minified is false by default (uninitialized = false)

        var json = InvokeSerializeReport(rc, report);
        // Indented JSON has newlines
        Assert.That(json, Does.Contain("\n"));
    }

    [Test]
    public void SerializeReport_Minified_ProducesCompactJson()
    {
        var report = new GitWizardReport();
        report.Repositories["/repo/a"] = CreateRepoWithFlags();

        var rc = FormatterServices.GetUninitializedObject(RunConfigType);
        SetRunConfigField(RunConfigType, ref rc, "Minified", true);

        var json = InvokeSerializeReport(rc, report);
        // Compact JSON should be a single line (no newlines)
        Assert.That(json, Does.Not.Contain("\n"));
    }

    [Test]
    public void SerializeReport_SetsSchemaVersionOnReport()
    {
        var report = new GitWizardReport { SchemaVersion = "0.0" };

        var rc = FormatterServices.GetUninitializedObject(RunConfigType);

        InvokeSerializeReport(rc, report);
        Assert.That(report.SchemaVersion, Is.EqualTo(GitWizardReport.CurrentSchemaVersion));
    }

    [Test]
    public void SerializeReport_OutputIsValidJson()
    {
        var report = new GitWizardReport();
        report.SearchPaths.Add("/search");
        report.Repositories["/repo/a"] = CreateRepoWithFlags();

        var rc = FormatterServices.GetUninitializedObject(RunConfigType);

        var json = InvokeSerializeReport(rc, report);
        // Should not throw
        var deserialized = JsonSerializer.Deserialize<JsonElement>(json);
        Assert.That(deserialized.ValueKind, Is.EqualTo(JsonValueKind.Object));
    }

    [Test]
    public void SerializeReport_ContainsSchemaVersionInOutput()
    {
        var report = new GitWizardReport();

        var rc = FormatterServices.GetUninitializedObject(RunConfigType);

        var json = InvokeSerializeReport(rc, report);
        Assert.That(json, Does.Contain($"\"SchemaVersion\""));
        Assert.That(json, Does.Contain(GitWizardReport.CurrentSchemaVersion));
    }

    [Test]
    public void SerializeReport_MinifiedAndNotMinified_ProduceSameData()
    {
        var report = new GitWizardReport();
        report.Repositories["/repo/a"] = CreateRepoWithFlags();

        var rcIndented = FormatterServices.GetUninitializedObject(RunConfigType);
        var rcMinified = FormatterServices.GetUninitializedObject(RunConfigType);
        SetRunConfigField(RunConfigType, ref rcMinified, "Minified", true);

        var indented = InvokeSerializeReport(rcIndented, report);
        var compact = InvokeSerializeReport(rcMinified, report);

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
        var result = InvokeTryHandleElevatedHelperModes(new[] { "git-wizard.exe", "-summary" });
        Assert.That(result, Is.False);
    }

    [Test]
    public void TryHandleElevatedHelperModes_EmptyArgs_ReturnsFalse()
    {
        var result = InvokeTryHandleElevatedHelperModes(Array.Empty<string>());
        Assert.That(result, Is.False);
    }

    [Test]
    public void TryHandleElevatedHelperModes_UnrelatedArgs_ReturnsFalse()
    {
        var result = InvokeTryHandleElevatedHelperModes(
            new[] { "git-wizard.exe", "-filter", "test", "-print-minified" });
        Assert.That(result, Is.False);
    }

    #endregion
}

using System.Reflection;
using GitWizard;
using GitWizard.CLI;

namespace GitWizardTests;

public class RunConfigurationTests
{
    // Cached reflection handles for the private nested types and methods.
    static readonly Type ProgramType = typeof(Program);
    static readonly Type RunConfigType = ProgramType.GetNestedType("RunConfiguration", BindingFlags.NonPublic)!;
    static readonly Type ParsedArgumentsType = RunConfigType.GetNestedType("ParsedArguments", BindingFlags.NonPublic)!;
    static readonly MethodInfo ParseMethod = RunConfigType.GetMethod("ParseCommandLine", BindingFlags.NonPublic | BindingFlags.Static)!;

    [SetUp]
    public void SetUp()
    {
        GitWizardLog.SilentMode = true;
    }

    [TearDown]
    public void TearDown()
    {
        // Restore log state that flags like -v and -silent mutate.
        GitWizardLog.VerboseMode = false;
        GitWizardLog.SilentMode = false;
    }

    /// <summary>
    /// Invoke the private <c>ParseCommandLine</c> method via reflection and return
    /// the resulting <c>ParsedArguments</c> object.
    /// </summary>
    static object Parse(params string[] args)
    {
        return ParseMethod.Invoke(null, new object[] { args })!;
    }

    /// <summary>
    /// Read a property value from a <c>ParsedArguments</c> instance by name.
    /// </summary>
    static T Get<T>(object parsed, string propertyName)
    {
        var prop = ParsedArgumentsType.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new ArgumentException($"Property '{propertyName}' not found on ParsedArguments");
        return (T)prop.GetValue(parsed)!;
    }

    static bool GetBool(object parsed, string name) => Get<bool>(parsed, name);
    static string? GetString(object parsed, string name)
    {
        var prop = ParsedArgumentsType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)!;
        return (string?)prop.GetValue(parsed);
    }

    // ---------------------------------------------------------------
    // Default values (no arguments)
    // ---------------------------------------------------------------

    [Test]
    public void ParseCommandLine_NoArguments_AllDefaultValues()
    {
        var parsed = Parse();
        Assert.Multiple(() =>
        {
            Assert.That(GetBool(parsed, "RebuildRepositoryList"), Is.False);
            Assert.That(GetBool(parsed, "RebuildReport"), Is.False);
            Assert.That(GetBool(parsed, "ClearCache"), Is.False);
            Assert.That(GetBool(parsed, "DeleteAllLocalFiles"), Is.False);
            Assert.That(GetBool(parsed, "SetupDefender"), Is.False);
            Assert.That(GetBool(parsed, "ScanOnly"), Is.False);
            Assert.That(GetBool(parsed, "NoMft"), Is.False);
            Assert.That(GetBool(parsed, "Summary"), Is.False);
            Assert.That(GetBool(parsed, "Merge"), Is.False);
            Assert.That(GetBool(parsed, "RefreshReport"), Is.True); // default is true
            Assert.That(GetBool(parsed, "Minified"), Is.False);
            Assert.That(GetBool(parsed, "DbSize"), Is.False);
            Assert.That(GetBool(parsed, "AllBranches"), Is.False);
            Assert.That(GetBool(parsed, "Watch"), Is.False);
            Assert.That(GetString(parsed, "FilterPattern"), Is.Null);
            Assert.That(GetString(parsed, "PathsArgument"), Is.Null);
            Assert.That(GetString(parsed, "SavePath"), Is.Null);
            Assert.That(GetString(parsed, "CustomConfigurationPath"), Is.Null);
        });
    }

    // ---------------------------------------------------------------
    // Individual simple flags
    // ---------------------------------------------------------------

    [Test]
    public void ParseCommandLine_VerboseFlag_SetsVerboseMode()
    {
        Parse("-v");
        Assert.That(GitWizardLog.VerboseMode, Is.True);
    }

    [Test]
    public void ParseCommandLine_SilentFlag_SetsSilentMode()
    {
        // Reset first so we can detect the change.
        GitWizardLog.SilentMode = false;
        Parse("-silent");
        Assert.That(GitWizardLog.SilentMode, Is.True);
    }

    [Test]
    public void ParseCommandLine_RebuildReport_SetsRebuildReport()
    {
        var parsed = Parse("-rebuild-report");
        Assert.That(GetBool(parsed, "RebuildReport"), Is.True);
    }

    [Test]
    public void ParseCommandLine_NoRefresh_ClearsRefreshReport()
    {
        var parsed = Parse("-no-refresh");
        Assert.That(GetBool(parsed, "RefreshReport"), Is.False);
    }

    [Test]
    public void ParseCommandLine_RebuildRepoList_SetsRebuildRepositoryList()
    {
        var parsed = Parse("-rebuild-repo-list");
        Assert.That(GetBool(parsed, "RebuildRepositoryList"), Is.True);
    }

    [Test]
    public void ParseCommandLine_RebuildAll_SetsBothRebuildFlags()
    {
        var parsed = Parse("-rebuild-all");
        Assert.Multiple(() =>
        {
            Assert.That(GetBool(parsed, "RebuildReport"), Is.True);
            Assert.That(GetBool(parsed, "RebuildRepositoryList"), Is.True);
        });
    }

    [Test]
    public void ParseCommandLine_SetupDefender_SetsSetupDefender()
    {
        var parsed = Parse("-setup-defender");
        Assert.That(GetBool(parsed, "SetupDefender"), Is.True);
    }

    [Test]
    public void ParseCommandLine_ScanOnly_SetsScanOnlyAndRebuildRepositoryList()
    {
        var parsed = Parse("-scan-only");
        Assert.Multiple(() =>
        {
            Assert.That(GetBool(parsed, "ScanOnly"), Is.True);
            Assert.That(GetBool(parsed, "RebuildRepositoryList"), Is.True);
        });
    }

    [Test]
    public void ParseCommandLine_NoMft_SetsNoMft()
    {
        var parsed = Parse("-no-mft");
        Assert.That(GetBool(parsed, "NoMft"), Is.True);
    }

    [Test]
    public void ParseCommandLine_Summary_SetsSummary()
    {
        var parsed = Parse("-summary");
        Assert.That(GetBool(parsed, "Summary"), Is.True);
    }

    [Test]
    public void ParseCommandLine_Merge_SetsMerge()
    {
        var parsed = Parse("-merge");
        Assert.That(GetBool(parsed, "Merge"), Is.True);
    }

    [Test]
    public void ParseCommandLine_ClearCache_SetsClearCache()
    {
        var parsed = Parse("-clear-cache");
        Assert.That(GetBool(parsed, "ClearCache"), Is.True);
    }

    [Test]
    public void ParseCommandLine_DeleteAllLocalFiles_SetsDeleteAllLocalFiles()
    {
        var parsed = Parse("-delete-all-local-files");
        Assert.That(GetBool(parsed, "DeleteAllLocalFiles"), Is.True);
    }

    [Test]
    public void ParseCommandLine_PrintMinified_SetsMinified()
    {
        var parsed = Parse("-print-minified");
        Assert.That(GetBool(parsed, "Minified"), Is.True);
    }

    [Test]
    public void ParseCommandLine_DbSize_SetsDbSize()
    {
        var parsed = Parse("-db-size");
        Assert.That(GetBool(parsed, "DbSize"), Is.True);
    }

    [Test]
    public void ParseCommandLine_AllBranches_SetsAllBranches()
    {
        var parsed = Parse("-all-branches");
        Assert.That(GetBool(parsed, "AllBranches"), Is.True);
    }

    [Test]
    public void ParseCommandLine_Watch_SetsWatch()
    {
        var parsed = Parse("-watch");
        Assert.That(GetBool(parsed, "Watch"), Is.True);
    }

    // ---------------------------------------------------------------
    // Parameterized arguments
    // ---------------------------------------------------------------

    [Test]
    public void ParseCommandLine_Filter_SetsFilterPattern()
    {
        var parsed = Parse("-filter", "my-pattern");
        Assert.That(GetString(parsed, "FilterPattern"), Is.EqualTo("my-pattern"));
    }

    [Test]
    public void ParseCommandLine_Paths_SetsPathsArgument()
    {
        var parsed = Parse("-paths", "repo1,repo2");
        Assert.That(GetString(parsed, "PathsArgument"), Is.EqualTo("repo1,repo2"));
    }

    [Test]
    public void ParseCommandLine_SavePath_SetsSavePath()
    {
        var parsed = Parse("-save-path", @"C:\reports\output.json");
        Assert.That(GetString(parsed, "SavePath"), Is.EqualTo(@"C:\reports\output.json"));
    }

    [Test]
    public void ParseCommandLine_ConfigPath_SetsCustomConfigurationPath()
    {
        var parsed = Parse("-config-path", @"C:\configs\custom.json");
        Assert.That(GetString(parsed, "CustomConfigurationPath"), Is.EqualTo(@"C:\configs\custom.json"));
    }

    // ---------------------------------------------------------------
    // Missing-argument error paths for parameterized args
    // ---------------------------------------------------------------

    [Test]
    public void ParseCommandLine_FilterWithoutFollowingArg_LeavesFilterNull()
    {
        var parsed = Parse("-filter");
        Assert.That(GetString(parsed, "FilterPattern"), Is.Null);
    }

    [Test]
    public void ParseCommandLine_PathsWithoutFollowingArg_LeavesPathsNull()
    {
        var parsed = Parse("-paths");
        Assert.That(GetString(parsed, "PathsArgument"), Is.Null);
    }

    // ---------------------------------------------------------------
    // Combinations of flags
    // ---------------------------------------------------------------

    [Test]
    public void ParseCommandLine_MultipleSimpleFlags_AllSetCorrectly()
    {
        var parsed = Parse("-summary", "-no-mft", "-print-minified", "-all-branches");
        Assert.Multiple(() =>
        {
            Assert.That(GetBool(parsed, "Summary"), Is.True);
            Assert.That(GetBool(parsed, "NoMft"), Is.True);
            Assert.That(GetBool(parsed, "Minified"), Is.True);
            Assert.That(GetBool(parsed, "AllBranches"), Is.True);
        });
    }

    [Test]
    public void ParseCommandLine_FlagAndParameterCombination_BothApplied()
    {
        var parsed = Parse("-summary", "-filter", "foo", "-no-refresh");
        Assert.Multiple(() =>
        {
            Assert.That(GetBool(parsed, "Summary"), Is.True);
            Assert.That(GetString(parsed, "FilterPattern"), Is.EqualTo("foo"));
            Assert.That(GetBool(parsed, "RefreshReport"), Is.False);
        });
    }

    [Test]
    public void ParseCommandLine_MergeWithPathsAndSavePath_AllSet()
    {
        var parsed = Parse("-merge", "-paths", "C:/repo", "-save-path", "out.json");
        Assert.Multiple(() =>
        {
            Assert.That(GetBool(parsed, "Merge"), Is.True);
            Assert.That(GetString(parsed, "PathsArgument"), Is.EqualTo("C:/repo"));
            Assert.That(GetString(parsed, "SavePath"), Is.EqualTo("out.json"));
        });
    }

    [Test]
    public void ParseCommandLine_AllBooleanFlagsAtOnce_AllTrue()
    {
        var parsed = Parse(
            "-rebuild-report", "-rebuild-repo-list", "-clear-cache",
            "-delete-all-local-files", "-setup-defender", "-scan-only",
            "-no-mft", "-summary", "-merge", "-no-refresh",
            "-print-minified", "-db-size", "-all-branches", "-watch"
        );
        Assert.Multiple(() =>
        {
            Assert.That(GetBool(parsed, "RebuildReport"), Is.True);
            Assert.That(GetBool(parsed, "RebuildRepositoryList"), Is.True);
            Assert.That(GetBool(parsed, "ClearCache"), Is.True);
            Assert.That(GetBool(parsed, "DeleteAllLocalFiles"), Is.True);
            Assert.That(GetBool(parsed, "SetupDefender"), Is.True);
            Assert.That(GetBool(parsed, "ScanOnly"), Is.True);
            Assert.That(GetBool(parsed, "NoMft"), Is.True);
            Assert.That(GetBool(parsed, "Summary"), Is.True);
            Assert.That(GetBool(parsed, "Merge"), Is.True);
            Assert.That(GetBool(parsed, "RefreshReport"), Is.False);
            Assert.That(GetBool(parsed, "Minified"), Is.True);
            Assert.That(GetBool(parsed, "DbSize"), Is.True);
            Assert.That(GetBool(parsed, "AllBranches"), Is.True);
            Assert.That(GetBool(parsed, "Watch"), Is.True);
        });
    }

    // ---------------------------------------------------------------
    // Unknown / unrecognized flags
    // ---------------------------------------------------------------

    [Test]
    public void ParseCommandLine_UnknownFlag_IgnoredGracefully()
    {
        var parsed = Parse("-unknown-flag", "-summary");
        Assert.Multiple(() =>
        {
            // The unknown flag is silently skipped.
            Assert.That(GetBool(parsed, "Summary"), Is.True);
            // All defaults remain for other fields.
            Assert.That(GetBool(parsed, "ClearCache"), Is.False);
        });
    }

    [Test]
    public void ParseCommandLine_NonDashArgument_Ignored()
    {
        var parsed = Parse("someRandomArg");
        Assert.That(GetBool(parsed, "RebuildReport"), Is.False);
    }

    // ---------------------------------------------------------------
    // Edge cases
    // ---------------------------------------------------------------

    [Test]
    public void ParseCommandLine_EmptyArray_ReturnsDefaults()
    {
        var parsed = Parse(Array.Empty<string>());
        Assert.Multiple(() =>
        {
            Assert.That(GetBool(parsed, "RefreshReport"), Is.True);
            Assert.That(GetString(parsed, "FilterPattern"), Is.Null);
        });
    }

    [Test]
    public void ParseCommandLine_ScanOnlyAlsoSetsRebuildRepositoryList()
    {
        // Verify the side-effect: -scan-only implies -rebuild-repo-list
        var parsed = Parse("-scan-only");
        Assert.That(GetBool(parsed, "RebuildRepositoryList"), Is.True);
    }

    [Test]
    public void ParseCommandLine_RebuildAllImpliesBothFlags()
    {
        // Verify -rebuild-all sets both RebuildReport and RebuildRepositoryList
        var parsed = Parse("-rebuild-all");
        Assert.Multiple(() =>
        {
            Assert.That(GetBool(parsed, "RebuildReport"), Is.True);
            Assert.That(GetBool(parsed, "RebuildRepositoryList"), Is.True);
        });
    }

    [Test]
    public void ParseCommandLine_FilterWithValue_DoesNotAffectOtherFields()
    {
        var parsed = Parse("-filter", "pattern");
        Assert.Multiple(() =>
        {
            Assert.That(GetString(parsed, "FilterPattern"), Is.EqualTo("pattern"));
            // Other fields unchanged.
            Assert.That(GetBool(parsed, "Summary"), Is.False);
            Assert.That(GetBool(parsed, "RefreshReport"), Is.True);
        });
    }

    [Test]
    public void ParseCommandLine_DuplicateFlags_LastWins()
    {
        // Passing the same flag twice should not cause errors.
        var parsed = Parse("-summary", "-summary");
        Assert.That(GetBool(parsed, "Summary"), Is.True);
    }

    [Test]
    public void ParseCommandLine_FilterOverwritten_LastValueWins()
    {
        var parsed = Parse("-filter", "first", "-filter", "second");
        Assert.That(GetString(parsed, "FilterPattern"), Is.EqualTo("second"));
    }

    [Test]
    public void ParseCommandLine_VerboseAndSilent_BothApplied()
    {
        // These directly set static properties, so both can be true simultaneously.
        GitWizardLog.VerboseMode = false;
        GitWizardLog.SilentMode = false;
        Parse("-v", "-silent");
        Assert.Multiple(() =>
        {
            Assert.That(GitWizardLog.VerboseMode, Is.True);
            Assert.That(GitWizardLog.SilentMode, Is.True);
        });
    }

    [Test]
    public void ParseCommandLine_ParameterizedArgsWithSpacesInValue_Preserved()
    {
        var parsed = Parse("-filter", "my pattern with spaces");
        Assert.That(GetString(parsed, "FilterPattern"), Is.EqualTo("my pattern with spaces"));
    }

    [Test]
    public void ParseCommandLine_PathsWithCommaList_PreservedAsIs()
    {
        var parsed = Parse("-paths", "C:/a,C:/b,C:/c");
        Assert.That(GetString(parsed, "PathsArgument"), Is.EqualTo("C:/a,C:/b,C:/c"));
    }
}

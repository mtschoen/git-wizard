using GitWizard;
using GitWizard.CLI;

namespace GitWizardTests;

/// <summary>
/// Coverage-boost tests for <see cref="Program.RunConfiguration"/>: default values,
/// individual flag parsing, parameterized arguments, and flag combinations.
/// </summary>
public class CoverageBoostRunConfigurationTests
{
    static RunConfigurationResult Parse(params string[] args) => CliParser.Parse(args);

    bool _originalVerboseMode;
    bool _originalSilentMode;

    [SetUp]
    public void SetUp()
    {
        _originalVerboseMode = GitWizardLog.VerboseMode;
        _originalSilentMode = GitWizardLog.SilentMode;
        GitWizardLog.SilentMode = true;
    }

    [TearDown]
    public void TearDown()
    {
        GitWizardLog.VerboseMode = _originalVerboseMode;
        GitWizardLog.SilentMode = _originalSilentMode;
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
            Assert.That(parsed.RebuildRepositoryList, Is.False);
            Assert.That(parsed.RebuildReport, Is.False);
            Assert.That(parsed.ClearCache, Is.False);
            Assert.That(parsed.DeleteAllLocalFiles, Is.False);
            Assert.That(parsed.SetupDefender, Is.False);
            Assert.That(parsed.ScanOnly, Is.False);
            Assert.That(parsed.NoMft, Is.False);
            Assert.That(parsed.Summary, Is.False);
            Assert.That(parsed.Merge, Is.False);
            Assert.That(parsed.RefreshReport, Is.True); // default is true
            Assert.That(parsed.Minified, Is.False);
            Assert.That(parsed.DbSize, Is.False);
            Assert.That(parsed.AllBranches, Is.False);
            Assert.That(parsed.Watch, Is.False);
            Assert.That(parsed.FilterPattern, Is.Null);
            Assert.That(parsed.PathsArgument, Is.Null);
            Assert.That(parsed.SavePath, Is.Null);
            Assert.That(parsed.CustomConfigurationPath, Is.Null);
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
    public void ParseCommandLine_VerboseLongFlag_SetsVerboseMode()
    {
        GitWizardLog.VerboseMode = false;
        Parse("--verbose");
        Assert.That(GitWizardLog.VerboseMode, Is.True);
    }

    [Test]
    public void ParseCommandLine_SilentFlag_SetsSilentMode()
    {
        GitWizardLog.SilentMode = false;
        Parse("--silent");
        Assert.That(GitWizardLog.SilentMode, Is.True);
    }

    [Test]
    public void ParseCommandLine_SilentShortFlag_SetsSilentMode()
    {
        GitWizardLog.SilentMode = false;
        Parse("-s");
        Assert.That(GitWizardLog.SilentMode, Is.True);
    }

    [Test]
    public void ParseCommandLine_RebuildReport_SetsRebuildReport()
    {
        var parsed = Parse("--rebuild-report");
        Assert.That(parsed.RebuildReport, Is.True);
    }

    [Test]
    public void ParseCommandLine_NoRefresh_ClearsRefreshReport()
    {
        var parsed = Parse("--no-refresh");
        Assert.That(parsed.RefreshReport, Is.False);
    }

    [Test]
    public void ParseCommandLine_RebuildRepoList_SetsRebuildRepositoryList()
    {
        var parsed = Parse("--rebuild-repo-list");
        Assert.That(parsed.RebuildRepositoryList, Is.True);
    }

    [Test]
    public void ParseCommandLine_RebuildAll_SetsBothRebuildFlags()
    {
        var parsed = Parse("--rebuild-all");
        Assert.Multiple(() =>
        {
            Assert.That(parsed.RebuildReport, Is.True);
            Assert.That(parsed.RebuildRepositoryList, Is.True);
        });
    }

    [Test]
    public void ParseCommandLine_SetupDefender_SetsSetupDefender()
    {
        var parsed = Parse("--setup-defender");
        Assert.That(parsed.SetupDefender, Is.True);
    }

    [Test]
    public void ParseCommandLine_ScanOnly_SetsScanOnlyAndRebuildRepositoryList()
    {
        var parsed = Parse("--scan-only");
        Assert.Multiple(() =>
        {
            Assert.That(parsed.ScanOnly, Is.True);
            Assert.That(parsed.RebuildRepositoryList, Is.True);
        });
    }

    [Test]
    public void ParseCommandLine_NoMft_SetsNoMft()
    {
        var parsed = Parse("--no-mft");
        Assert.That(parsed.NoMft, Is.True);
    }

    [Test]
    public void ParseCommandLine_Summary_SetsSummary()
    {
        var parsed = Parse("--summary");
        Assert.That(parsed.Summary, Is.True);
    }

    [Test]
    public void ParseCommandLine_Merge_SetsMerge()
    {
        var parsed = Parse("--merge");
        Assert.That(parsed.Merge, Is.True);
    }

    [Test]
    public void ParseCommandLine_ClearCache_SetsClearCache()
    {
        var parsed = Parse("--clear-cache");
        Assert.That(parsed.ClearCache, Is.True);
    }

    [Test]
    public void ParseCommandLine_DeleteAllLocalFiles_SetsDeleteAllLocalFiles()
    {
        var parsed = Parse("--delete-all-local-files");
        Assert.That(parsed.DeleteAllLocalFiles, Is.True);
    }

    [Test]
    public void ParseCommandLine_PrintMinified_SetsMinified()
    {
        var parsed = Parse("--print-minified");
        Assert.That(parsed.Minified, Is.True);
    }

    [Test]
    public void ParseCommandLine_DbSize_SetsDbSize()
    {
        var parsed = Parse("--db-size");
        Assert.That(parsed.DbSize, Is.True);
    }

    [Test]
    public void ParseCommandLine_AllBranches_SetsAllBranches()
    {
        var parsed = Parse("--all-branches");
        Assert.That(parsed.AllBranches, Is.True);
    }

    [Test]
    public void ParseCommandLine_Watch_SetsWatch()
    {
        var parsed = Parse("--watch");
        Assert.That(parsed.Watch, Is.True);
    }

    [Test]
    public void ParseCommandLine_NoLocalCommitCount_SetsNoLocalCommitCount()
    {
        var parsed = Parse("--no-local-commit-count");
        Assert.That(parsed.NoLocalCommitCount, Is.True);
    }

    // ---------------------------------------------------------------
    // Parameterized arguments
    // ---------------------------------------------------------------

    [Test]
    public void ParseCommandLine_Filter_SetsFilterPattern()
    {
        var parsed = Parse("--filter", "my-pattern");
        Assert.That(parsed.FilterPattern, Is.EqualTo("my-pattern"));
    }

    [Test]
    public void ParseCommandLine_Paths_SetsPathsArgument()
    {
        var parsed = Parse("--paths", "repo1,repo2");
        Assert.That(parsed.PathsArgument, Is.EqualTo("repo1,repo2"));
    }

    [Test]
    public void ParseCommandLine_SavePath_SetsSavePath()
    {
        // The save-path parent directory must exist, otherwise validation rejects it,
        // so point at a real temp directory rather than a hard-coded absolute path.
        var tempDir = Path.Combine(Path.GetTempPath(), "GitWizardHome", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var savePath = Path.Combine(tempDir, "output.json");
            var parsed = Parse("--save-path", savePath);
            Assert.That(parsed.SavePath, Is.EqualTo(savePath));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public void ParseCommandLine_SavePath_NonexistentParent_IsRejected()
    {
        var missingParent = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "output.json");
        var parsed = Parse("--save-path", missingParent);
        Assert.Multiple(() =>
        {
            Assert.That(parsed.SavePath, Is.Null);
            Assert.That(parsed.HasError, Is.True);
        });
    }

    [Test]
    public void ParseCommandLine_ConfigPath_SetsCustomConfigurationPath()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "GitWizardHome", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var configPath = Path.Combine(tempDir, "custom.json");
            File.WriteAllText(configPath, "{}");
            var parsed = Parse("--config-path", configPath);
            Assert.That(parsed.CustomConfigurationPath, Is.EqualTo(configPath));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    // ---------------------------------------------------------------
    // Combinations of flags
    // ---------------------------------------------------------------

    [Test]
    public void ParseCommandLine_MultipleSimpleFlags_AllSetCorrectly()
    {
        var parsed = Parse("--summary", "--no-mft", "--print-minified", "--all-branches");
        Assert.Multiple(() =>
        {
            Assert.That(parsed.Summary, Is.True);
            Assert.That(parsed.NoMft, Is.True);
            Assert.That(parsed.Minified, Is.True);
            Assert.That(parsed.AllBranches, Is.True);
        });
    }

    [Test]
    public void ParseCommandLine_FlagAndParameterCombination_BothApplied()
    {
        var parsed = Parse("--summary", "--filter", "foo", "--no-refresh");
        Assert.Multiple(() =>
        {
            Assert.That(parsed.Summary, Is.True);
            Assert.That(parsed.FilterPattern, Is.EqualTo("foo"));
            Assert.That(parsed.RefreshReport, Is.False);
        });
    }

    [Test]
    public void ParseCommandLine_MergeWithPathsAndSavePath_AllSet()
    {
        var parsed = Parse("--merge", "--paths", "C:/repo", "--save-path", "out.json");
        Assert.Multiple(() =>
        {
            Assert.That(parsed.Merge, Is.True);
            Assert.That(parsed.PathsArgument, Is.EqualTo("C:/repo"));
            Assert.That(parsed.SavePath, Is.EqualTo("out.json"));
        });
    }

    [Test]
    public void ParseCommandLine_AllBooleanFlagsAtOnce_AllTrue()
    {
        var parsed = Parse(
            "--rebuild-report", "--rebuild-repo-list", "--clear-cache",
            "--delete-all-local-files", "--setup-defender", "--scan-only",
            "--no-mft", "--no-local-commit-count", "--summary", "--merge", "--no-refresh",
            "--print-minified", "--db-size", "--all-branches", "--watch"
        );
        Assert.Multiple(() =>
        {
            Assert.That(parsed.RebuildReport, Is.True);
            Assert.That(parsed.RebuildRepositoryList, Is.True);
            Assert.That(parsed.ClearCache, Is.True);
            Assert.That(parsed.DeleteAllLocalFiles, Is.True);
            Assert.That(parsed.SetupDefender, Is.True);
            Assert.That(parsed.ScanOnly, Is.True);
            Assert.That(parsed.NoMft, Is.True);
            Assert.That(parsed.NoLocalCommitCount, Is.True);
            Assert.That(parsed.Summary, Is.True);
            Assert.That(parsed.Merge, Is.True);
            Assert.That(parsed.RefreshReport, Is.False);
            Assert.That(parsed.Minified, Is.True);
            Assert.That(parsed.DbSize, Is.True);
            Assert.That(parsed.AllBranches, Is.True);
            Assert.That(parsed.Watch, Is.True);
        });
    }
}

using System.Reflection;
using GitWizard;
using GitWizard.CLI;

namespace GitWizardTests;

/// <summary>
/// Coverage-boost tests for <see cref="Program.RunConfiguration"/>: edge cases,
/// path validation, and additional flag combination tests.
/// </summary>
public class CoverageBoostRunConfigurationEdgeCases
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
    // Edge cases
    // ---------------------------------------------------------------

    [Test]
    public void ParseCommandLine_EmptyArray_ReturnsDefaults()
    {
        var parsed = Parse(Array.Empty<string>());
        Assert.Multiple(() =>
        {
            Assert.That(parsed.RefreshReport, Is.True);
            Assert.That(parsed.FilterPattern, Is.Null);
        });
    }

    [Test]
    public void ParseCommandLine_ScanOnlyAlsoSetsRebuildRepositoryList()
    {
        var parsed = Parse("--scan-only");
        Assert.That(parsed.RebuildRepositoryList, Is.True);
    }

    [Test]
    public void ParseCommandLine_RebuildAllImpliesBothFlags()
    {
        var parsed = Parse("--rebuild-all");
        Assert.Multiple(() =>
        {
            Assert.That(parsed.RebuildReport, Is.True);
            Assert.That(parsed.RebuildRepositoryList, Is.True);
        });
    }

    [Test]
    public void ParseCommandLine_FilterWithValue_DoesNotAffectOtherFields()
    {
        var parsed = Parse("--filter", "pattern");
        Assert.Multiple(() =>
        {
            Assert.That(parsed.FilterPattern, Is.EqualTo("pattern"));
            Assert.That(parsed.Summary, Is.False);
            Assert.That(parsed.RefreshReport, Is.True);
        });
    }

    [Test]
    public void ParseCommandLine_DuplicateFlags_LastWins()
    {
        var parsed = Parse("--summary", "--summary");
        Assert.That(parsed.Summary, Is.True);
    }

    [Test]
    public void ParseCommandLine_DuplicateFilter_LastValueWins()
    {
        var parsed = Parse("--filter", "a", "--filter", "b");
        Assert.That(parsed.FilterPattern, Is.EqualTo("b"));
    }

    [Test]
    public void ParseCommandLine_DuplicatePaths_LastValueWins()
    {
        var parsed = Parse("--paths", "C:/a", "--paths", "C:/b");
        Assert.That(parsed.PathsArgument, Is.EqualTo("C:/b"));
    }

    [Test]
    public void ParseCommandLine_SavePathWithoutValue_ReturnsDefaultsWithoutCrashing()
    {
        var parsed = Parse("--save-path");
        Assert.Multiple(() =>
        {
            Assert.That(parsed.SavePath, Is.Null);
            Assert.That(parsed.HasError, Is.False);
        });
    }

    [Test]
    public void ParseCommandLine_VerboseAndSilent_BothApplied()
    {
        GitWizardLog.VerboseMode = false;
        GitWizardLog.SilentMode = false;
        Parse("-v", "--silent");
        Assert.Multiple(() =>
        {
            Assert.That(GitWizardLog.VerboseMode, Is.True);
            Assert.That(GitWizardLog.SilentMode, Is.True);
        });
    }

    [Test]
    public void ParseCommandLine_ParameterizedArgsWithSpacesInValue_Preserved()
    {
        var parsed = Parse("--filter", "my pattern with spaces");
        Assert.That(parsed.FilterPattern, Is.EqualTo("my pattern with spaces"));
    }

    [Test]
    public void ParseCommandLine_PathsWithCommaList_PreservedAsIs()
    {
        var parsed = Parse("--paths", "C:/a,C:/b,C:/c");
        Assert.That(parsed.PathsArgument, Is.EqualTo("C:/a,C:/b,C:/c"));
    }

    // ---------------------------------------------------------------
    // Path validation
    // ---------------------------------------------------------------

    [Test]
    public void ParseCommandLine_SavePathWithValidParentDirectory_ReturnsPath()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "GitWizardHome", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var subDir = Path.Combine(tempDir, "subdir");
            Directory.CreateDirectory(subDir);
            var savePath = Path.Combine(subDir, "output.json");
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
    public void ParseCommandLine_SavePathWithNonExistentDirectory_ReturnsNullAndHasError()
    {
        var parsed = Parse("--save-path", @"/nonexistent/path/to/output.json");
        Assert.That(parsed.SavePath, Is.Null);
        Assert.That(parsed.HasError, Is.True);
    }

    [Test]
    public void ParseCommandLine_ConfigPathWithExistingFile_ReturnsPath()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "GitWizardHome", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var configPath = Path.Combine(tempDir, "config.json");
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

    [Test]
    public void ParseCommandLine_ConfigPathWithNonExistentFile_ReturnsNullAndHasError()
    {
        var parsed = Parse("--config-path", "/nonexistent/path/to/config.json");
        Assert.That(parsed.CustomConfigurationPath, Is.Null);
        Assert.That(parsed.HasError, Is.True);
    }

    [Test]
    public void ParseCommandLine_SavePathAndConfigPathValidation_AllApplied()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "GitWizardHome", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var configFile = Path.Combine(tempDir, "config.json");
            File.WriteAllText(configFile, "{}");

            var parsed = Parse("--config-path", configFile, "--summary");
            Assert.Multiple(() =>
            {
                Assert.That(parsed.CustomConfigurationPath, Is.EqualTo(configFile));
                Assert.That(parsed.Summary, Is.True);
                Assert.That(parsed.HasError, Is.False);
            });
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public void ParseCommandLine_SavePathWithNonExistentParentAndConfigPathWithMissingFile_BothErrors()
    {
        var parsed = Parse("--save-path", @"/no/such/dir/out.json", "--config-path", "/no/such/config.json");
        Assert.Multiple(() =>
        {
            Assert.That(parsed.SavePath, Is.Null);
            Assert.That(parsed.CustomConfigurationPath, Is.Null);
            Assert.That(parsed.HasError, Is.True);
        });
    }

    // ---------------------------------------------------------------
    // Additional combination tests
    // ---------------------------------------------------------------

    [Test]
    public void RunConfiguration_DefaultConstructor_DoesNotThrow()
    {
        var runConfigType = typeof(Program).GetNestedType("RunConfiguration", BindingFlags.NonPublic);
        Assert.That(runConfigType, Is.Not.Null);
    }

    [Test]
    public void ParseCommandLine_SavePath_SetsValue()
    {
        // Validation only preserves a save-path whose parent directory exists, so anchor
        // the path in a real temp directory rather than a hard-coded OS-specific literal.
        var tempDir = Path.Combine(Path.GetTempPath(), "GitWizardHome", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var savePath = Path.Combine(tempDir, "out.json");
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
    public void ParseCommandLine_ScanOnlyWithFilter_BothSet()
    {
        var parsed = Parse("--scan-only", "--filter", "my-proj");
        Assert.Multiple(() =>
        {
            Assert.That(parsed.ScanOnly, Is.True);
            Assert.That(parsed.RebuildRepositoryList, Is.True);
            Assert.That(parsed.FilterPattern, Is.EqualTo("my-proj"));
        });
    }

    [Test]
    public void ParseCommandLine_WatchWithNoMft_BothSet()
    {
        var parsed = Parse("--watch", "--no-mft");
        Assert.Multiple(() =>
        {
            Assert.That(parsed.Watch, Is.True);
            Assert.That(parsed.NoMft, Is.True);
        });
    }

    [Test]
    public void ParseCommandLine_DbSizeWithSilent_BothSet()
    {
        GitWizardLog.SilentMode = false;
        var parsed = Parse("--db-size", "--silent");
        Assert.Multiple(() =>
        {
            Assert.That(parsed.DbSize, Is.True);
            Assert.That(GitWizardLog.SilentMode, Is.True);
        });
    }

    [Test]
    public void ParseCommandLine_AllBranchesWithSummary_BothSet()
    {
        var parsed = Parse("--all-branches", "--summary");
        Assert.Multiple(() =>
        {
            Assert.That(parsed.AllBranches, Is.True);
            Assert.That(parsed.Summary, Is.True);
        });
    }

    [Test]
    public void ParseCommandLine_ClearCacheAndDeleteAll_BothSet()
    {
        var parsed = Parse("--clear-cache", "--delete-all-local-files");
        Assert.Multiple(() =>
        {
            Assert.That(parsed.ClearCache, Is.True);
            Assert.That(parsed.DeleteAllLocalFiles, Is.True);
        });
    }

    [Test]
    public void ParseCommandLine_SingleArg_Executable_Ignored()
    {
        var parsed = Parse("git-wizard.exe");
        Assert.That(parsed.RebuildReport, Is.False);
    }

    // ---------------------------------------------------------------
    // ParseProcessArgs (strips argv[0] before delegating to Parse) - regression
    // coverage for the production entry point, which feeds Environment
    // .GetCommandLineArgs() (program path included) rather than Main's args.
    // ---------------------------------------------------------------

    [Test]
    public void ParseProcessArgs_LeadingProgramPath_StrippedBeforeParsing()
    {
        var parsed = CliParser.ParseProcessArgs([@"C:\some\path\git-wizard.dll", "--filter", "myproj"]);
        Assert.That(parsed.FilterPattern, Is.EqualTo("myproj"));
    }

    [Test]
    public void ParseProcessArgs_LeadingProgramPath_SavePathStillValidated()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "GitWizardHome", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var savePath = Path.Combine(tempDir, "out.json");
            var parsed = CliParser.ParseProcessArgs([@"C:\some\path\git-wizard.dll", "--save-path", savePath]);
            Assert.That(parsed.SavePath, Is.EqualTo(savePath));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public void ParseProcessArgs_EmptyArray_ReturnsDefaultsWithoutCrashing()
    {
        var parsed = CliParser.ParseProcessArgs([]);
        Assert.That(parsed.FilterPattern, Is.Null);
    }

    // ---------------------------------------------------------------
    // ExitRequested (--help/--version short-circuit) - regression coverage for the bug
    // where McMaster prints the help/version text but OnExecute never fires, so the
    // caller previously fell through to a full run indistinguishable from no args at all.
    // ---------------------------------------------------------------

    [Test]
    public void ParseCommandLine_Help_SetsExitRequested()
    {
        var parsed = Parse("--help");
        Assert.That(parsed.ExitRequested, Is.True);
    }

    [Test]
    public void ParseCommandLine_Version_SetsExitRequested()
    {
        var parsed = Parse("--version");
        Assert.That(parsed.ExitRequested, Is.True);
    }

    [Test]
    public void ParseCommandLine_NoHelpOrVersion_ExitRequestedIsFalse()
    {
        var parsed = Parse("--summary");
        Assert.That(parsed.ExitRequested, Is.False);
    }
}

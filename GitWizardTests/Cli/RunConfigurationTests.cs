using GitWizard;
using GitWizard.CLI;
using GitWizardTests.TestSupport;

namespace GitWizardTests;

/// <summary>
/// Table-driven checks for <see cref="RunConfiguration"/> parsing: command kind precedence,
/// boolean and value flags, Unicode filters, and regression edge cases.
/// </summary>
[NonParallelizable]
sealed class RunConfigurationTests
{
    bool _originalVerboseMode;
    bool _originalSilentMode;
    Action<string?> _originalLogMethod = null!;

    [SetUp]
    public void SetUp()
    {
        _originalVerboseMode = GitWizardLog.VerboseMode;
        _originalSilentMode = GitWizardLog.SilentMode;
        _originalLogMethod = GitWizardLog.LogMethod;
        GitWizardLog.SilentMode = true;
    }

    [TearDown]
    public void TearDown()
    {
        GitWizardLog.VerboseMode = _originalVerboseMode;
        GitWizardLog.SilentMode = _originalSilentMode;
        GitWizardLog.LogMethod = _originalLogMethod;
    }

    [TestCase(new string[0], CommandKind.Report)]
    [TestCase(new[] { "--json" }, CommandKind.Report)]
    [TestCase(new[] { "--sweep" }, CommandKind.Sweep)]
    [TestCase(new[] { "tags" }, CommandKind.Tags)]
    [TestCase(new[] { "branches" }, CommandKind.Branches)]
    [TestCase(new[] { "--scan-only" }, CommandKind.ScanOnly)]
    [TestCase(new[] { "--merge" }, CommandKind.Merge)]
    [TestCase(new[] { "--watch" }, CommandKind.Watch)]
    [TestCase(new[] { "--db-size" }, CommandKind.DbSize)]
    [TestCase(new[] { "--clear-index-cache" }, CommandKind.ClearIndexCache)]
    [TestCase(new[] { "--index-cache-status" }, CommandKind.IndexCacheStatus)]
    [TestCase(new[] { "--help" }, CommandKind.HelpPrinted)]
    [TestCase(new[] { "--version" }, CommandKind.HelpPrinted)]
    [TestCase(new[] { "tags", "--help" }, CommandKind.HelpPrinted)]
    [TestCase(new[] { "branches", "-h" }, CommandKind.HelpPrinted)]
    [TestCase(new[] { "--sweep", "--merge" }, CommandKind.Sweep)]
    [TestCase(new[] { "--merge", "--scan-only" }, CommandKind.Merge)]
    [TestCase(new[] { "--scan-only", "--watch" }, CommandKind.ScanOnly)]
    [TestCase(new[] { "--watch", "--merge" }, CommandKind.Merge)]
    [TestCase(new[] { "--db-size", "--sweep" }, CommandKind.DbSize)]
    [TestCase(new[] { "--clear-index-cache", "--watch" }, CommandKind.ClearIndexCache)]
    [TestCase(new[] { "--index-cache-status", "--db-size" }, CommandKind.IndexCacheStatus)]
    [TestCase(new[] { "--clear-cache", "--no-refresh" }, CommandKind.Report)]
    [TestCase(new[] { "--delete-all-local-files" }, CommandKind.Report)]
    public void Parse_CommandLine_ResolvesOneCommandKind(string[] args, CommandKind expected)
    {
        Assert.That(CliParser.Parse(args).Command, Is.EqualTo(expected));
    }

    [Test]
    public void Parse_NoArguments_EqualsDefaultRunConfiguration()
    {
        Assert.That(CliParser.Parse([]), Is.EqualTo(new RunConfiguration()));
    }

    [Test]
    public void DefaultRunConfiguration_IsAReportThatRefreshes()
    {
        var configuration = new RunConfiguration();

        Assert.Multiple(() =>
        {
            Assert.That(configuration.Command, Is.EqualTo(CommandKind.Report));
            Assert.That(configuration.RefreshReport, Is.True);
            Assert.That(configuration.HasParseError, Is.False);
        });
    }

    [Test]
    public void Parse_NoArguments_AllDefaultValues()
    {
        var parsed = CliParser.Parse([]);
        Assert.Multiple(() =>
        {
            Assert.That(parsed.RebuildRepositoryList, Is.False);
            Assert.That(parsed.RebuildReport, Is.False);
            Assert.That(parsed.ClearCache, Is.False);
            Assert.That(parsed.DeleteAllLocalFiles, Is.False);
            Assert.That(parsed.SetupDefender, Is.False);
            Assert.That(parsed.Command, Is.EqualTo(CommandKind.Report));
            Assert.That(parsed.NoMft, Is.False);
            Assert.That(parsed.Json, Is.False);
            Assert.That(parsed.RefreshReport, Is.True);
            Assert.That(parsed.Minified, Is.False);
            Assert.That(parsed.AllBranches, Is.False);
            Assert.That(parsed.NoLocalCommitCount, Is.False);
            Assert.That(parsed.FilterPattern, Is.Null);
            Assert.That(parsed.PathsArgument, Is.Null);
            Assert.That(parsed.SavePath, Is.Null);
            Assert.That(parsed.CustomConfigurationPath, Is.Null);
            Assert.That(parsed.HasParseError, Is.False);
        });
    }

    [TestCase("--rebuild-report", nameof(RunConfiguration.RebuildReport), true)]
    [TestCase("--no-refresh", nameof(RunConfiguration.RefreshReport), false)]
    [TestCase("--rebuild-repo-list", nameof(RunConfiguration.RebuildRepositoryList), true)]
    [TestCase("--rebuild-all", nameof(RunConfiguration.RebuildReport), true)]
    [TestCase("--rebuild-all", nameof(RunConfiguration.RebuildRepositoryList), true)]
    [TestCase("--scan-only", nameof(RunConfiguration.RebuildRepositoryList), true)]
    [TestCase("--setup-defender", nameof(RunConfiguration.SetupDefender), true)]
    [TestCase("--no-mft", nameof(RunConfiguration.NoMft), true)]
    [TestCase("--json", nameof(RunConfiguration.Json), true)]
    [TestCase("--clear-cache", nameof(RunConfiguration.ClearCache), true)]
    [TestCase("--delete-all-local-files", nameof(RunConfiguration.DeleteAllLocalFiles), true)]
    [TestCase("--all-branches", nameof(RunConfiguration.AllBranches), true)]
    [TestCase("--no-local-commit-count", nameof(RunConfiguration.NoLocalCommitCount), true)]
    [TestCase("-v", "VerboseMode", true)]
    [TestCase("--verbose", "VerboseMode", true)]
    [TestCase("-s", "SilentMode", true)]
    [TestCase("--silent", "SilentMode", true)]
    public void Parse_BooleanFlag_SetsExpectedValue(string argument, string member, bool expected)
    {
        GitWizardLog.VerboseMode = false;
        GitWizardLog.SilentMode = false;
        var parsed = CliParser.Parse([argument]);
        var actual = member switch
        {
            nameof(RunConfiguration.RebuildReport) => parsed.RebuildReport,
            nameof(RunConfiguration.RefreshReport) => parsed.RefreshReport,
            nameof(RunConfiguration.RebuildRepositoryList) => parsed.RebuildRepositoryList,
            nameof(RunConfiguration.SetupDefender) => parsed.SetupDefender,
            nameof(RunConfiguration.NoMft) => parsed.NoMft,
            nameof(RunConfiguration.Json) => parsed.Json,
            nameof(RunConfiguration.ClearCache) => parsed.ClearCache,
            nameof(RunConfiguration.DeleteAllLocalFiles) => parsed.DeleteAllLocalFiles,
            nameof(RunConfiguration.AllBranches) => parsed.AllBranches,
            nameof(RunConfiguration.NoLocalCommitCount) => parsed.NoLocalCommitCount,
            "VerboseMode" => GitWizardLog.VerboseMode,
            "SilentMode" => GitWizardLog.SilentMode,
            _ => throw new ArgumentOutOfRangeException(nameof(member), member, null)
        };
        Assert.That(actual, Is.EqualTo(expected));
        Assert.That(parsed.HasParseError, Is.False);
    }

    [TestCase("--filter")]
    [TestCase("--paths")]
    [TestCase("--save-path")]
    [TestCase("--config-path")]
    public void Parse_ValueFlag_PreservesValue(string argument)
    {
        using var directory = new TempDirectory();
        var tempDir = directory.Path;
        var value = argument switch
        {
            "--filter" => "my pattern with spaces",
            "--paths" => "/repo/a,/repo/b",
            "--save-path" => Path.Combine(tempDir, "output.json"),
            "--config-path" => Path.Combine(tempDir, "config.json"),
            _ => throw new ArgumentOutOfRangeException(nameof(argument), argument, null)
        };
        if (argument == "--config-path")
        {
            File.WriteAllText(value, "{}");
        }

        var parsed = CliParser.Parse([argument, value]);
        var actual = argument switch
        {
            "--filter" => parsed.FilterPattern,
            "--paths" => parsed.PathsArgument,
            "--save-path" => parsed.SavePath,
            "--config-path" => parsed.CustomConfigurationPath,
            _ => throw new ArgumentOutOfRangeException(nameof(argument), argument, null)
        };

        Assert.Multiple(() =>
        {
            Assert.That(actual, Is.EqualTo(value));
            Assert.That(parsed.HasParseError, Is.False);
            if (argument == "--filter")
            {
                Assert.That(parsed.Json, Is.False);
                Assert.That(parsed.RefreshReport, Is.True);
            }
        });
    }

    [TestCase("m\u00fcnchen")]
    [TestCase("\u043f\u0440\u043e\u0435\u043a\u0442")]
    [TestCase("repo\U0001f525")]
    public void Parse_UnicodeFilter_PreservesValue(string pattern)
    {
        Assert.That(CliParser.Parse(["--filter", pattern]).FilterPattern, Is.EqualTo(pattern));
    }

    [Test]
    public void ParseCommandLine_DuplicateFilter_LastValueWins()
    {
        var parsed = CliParser.Parse(["--filter", "a", "--filter", "b"]);
        Assert.That(parsed.FilterPattern, Is.EqualTo("b"));
    }

    [Test]
    public void ParseCommandLine_DuplicatePaths_LastValueWins()
    {
        var parsed = CliParser.Parse(["--paths", "C:/a", "--paths", "C:/b"]);
        Assert.That(parsed.PathsArgument, Is.EqualTo("C:/b"));
    }

    [Test]
    public void ParseCommandLine_DuplicateFlags_LastWins()
    {
        var parsed = CliParser.Parse(["--json", "--json"]);
        Assert.That(parsed.Json, Is.True);
    }

    [Test]
    public void ParseCommandLine_MergeWithPathsAndSavePath_AllSet()
    {
        var parsed = CliParser.Parse(["--merge", "--paths", "C:/repo", "--save-path", "out.json"]);
        Assert.Multiple(() =>
        {
            Assert.That(parsed.PathsArgument, Is.EqualTo("C:/repo"));
            Assert.That(parsed.SavePath, Is.EqualTo("out.json"));
        });
    }

    [Test]
    public void ParseCommandLine_SavePathWithoutValue_ReportsParseError()
    {
        var parsed = CliParser.Parse(["--save-path"]);
        Assert.Multiple(() =>
        {
            Assert.That(parsed.SavePath, Is.Null);
            Assert.That(parsed.HasParseError, Is.True);
        });
    }

    [Test]
    public void ParseCommandLine_SavePath_NonexistentParent_IsRejected()
    {
        using var files = new TempDirectory();
        var missingParent = Path.Combine(files.Path, "absent", "output.json");
        var parsed = CliParser.Parse(["--save-path", missingParent]);
        Assert.Multiple(() =>
        {
            Assert.That(parsed.SavePath, Is.Null);
            Assert.That(parsed.HasParseError, Is.True);
        });
    }

    [Test]
    public void ParseCommandLine_ConfigPathWithNonExistentFile_ReturnsNullAndHasError()
    {
        var parsed = CliParser.Parse(["--config-path", "/nonexistent/path/to/config.json"]);
        Assert.Multiple(() =>
        {
            Assert.That(parsed.CustomConfigurationPath, Is.Null);
            Assert.That(parsed.HasParseError, Is.True);
        });
    }

    [Test]
    public void ParseCommandLine_SavePathWithNonExistentParentAndConfigPathWithMissingFile_BothErrors()
    {
        var parsed = CliParser.Parse(["--save-path", "/no/such/dir/out.json", "--config-path", "/no/such/config.json"]);
        Assert.Multiple(() =>
        {
            Assert.That(parsed.SavePath, Is.Null);
            Assert.That(parsed.CustomConfigurationPath, Is.Null);
            Assert.That(parsed.HasParseError, Is.True);
        });
    }

    [Test]
    public void Parse_UnknownFlag_ReportsParseErrorWithDefaultCommand()
    {
        var configuration = CliParser.Parse(["--no-such-flag"]);

        Assert.Multiple(() =>
        {
            Assert.That(configuration.HasParseError, Is.True);
            Assert.That(configuration.Command, Is.EqualTo(CommandKind.Report));
        });
    }

    [Test]
    public void ParseCommandLine_SingleDashLongFlag_SetsHasError()
    {
        var parsed = CliParser.Parse(["-save-path"]);
        Assert.Multiple(() =>
        {
            Assert.That(parsed.HasParseError, Is.True);
            Assert.That(parsed.Command, Is.Not.EqualTo(CommandKind.HelpPrinted));
        });
    }

    [Test]
    public void ParseCommandLine_UnknownMixedFlags_SetsHasError()
    {
        var parsed = CliParser.Parse(["--unknown-flag", "-v"]);
        Assert.Multiple(() =>
        {
            Assert.That(parsed.HasParseError, Is.True);
            Assert.That(parsed.Command, Is.Not.EqualTo(CommandKind.HelpPrinted));
        });
    }

    [Test]
    public void ParseProcessArgs_LeadingProgramPath_StrippedBeforeParsing()
    {
        var parsed = CliParser.ParseProcessArgs([@"C:\some\path\git-wizard.dll", "--filter", "myproj"]);
        Assert.That(parsed.FilterPattern, Is.EqualTo("myproj"));
    }

    [Test]
    public void ParseProcessArgs_LeadingProgramPath_SavePathStillValidated()
    {
        using var directory = new TempDirectory();
        var tempDir = directory.Path;
        var savePath = Path.Combine(tempDir, "out.json");
        var parsed = CliParser.ParseProcessArgs([@"C:\some\path\git-wizard.dll", "--save-path", savePath]);
        Assert.That(parsed.SavePath, Is.EqualTo(savePath));
    }

    [Test]
    public void ParseProcessArgs_EmptyArray_ReturnsDefaultsWithoutCrashing()
    {
        var parsed = CliParser.ParseProcessArgs([]);
        Assert.That(parsed.FilterPattern, Is.Null);
    }

    [Test]
    public void ParseCommandLine_VerboseAndSilent_BothApplied()
    {
        GitWizardLog.VerboseMode = false;
        GitWizardLog.SilentMode = false;
        CliParser.Parse(["-v", "--silent"]);
        Assert.Multiple(() =>
        {
            Assert.That(GitWizardLog.VerboseMode, Is.True);
            Assert.That(GitWizardLog.SilentMode, Is.True);
        });
    }

    [Test]
    public void ParseCommandLine_ScanOnlyWithFilter_BothSet()
    {
        var parsed = CliParser.Parse(["--scan-only", "--filter", "my-proj"]);
        Assert.Multiple(() =>
        {
            Assert.That(parsed.Command, Is.EqualTo(CommandKind.ScanOnly));
            Assert.That(parsed.RebuildRepositoryList, Is.True);
            Assert.That(parsed.FilterPattern, Is.EqualTo("my-proj"));
        });
    }

    [Test]
    public void ParseCommandLine_WatchWithNoMft_BothSet()
    {
        var parsed = CliParser.Parse(["--watch", "--no-mft"]);
        Assert.Multiple(() =>
        {
            Assert.That(parsed.Command, Is.EqualTo(CommandKind.Watch));
            Assert.That(parsed.NoMft, Is.True);
        });
    }

    [Test]
    public void ParseCommandLine_AllBooleanFlagsAtOnce_KeepsEveryFlagAndFixedPrecedenceCommand()
    {
        var parsed = CliParser.Parse([
            "--rebuild-report", "--rebuild-repo-list", "--clear-cache",
            "--delete-all-local-files", "--setup-defender", "--scan-only",
            "--no-mft", "--no-local-commit-count", "--json", "--merge", "--no-refresh",
            "--minified", "--db-size", "--all-branches", "--watch"
        ]);
        Assert.Multiple(() =>
        {
            Assert.That(parsed.RebuildReport, Is.True);
            Assert.That(parsed.RebuildRepositoryList, Is.True);
            Assert.That(parsed.ClearCache, Is.True);
            Assert.That(parsed.DeleteAllLocalFiles, Is.True);
            Assert.That(parsed.SetupDefender, Is.True);
            Assert.That(parsed.NoMft, Is.True);
            Assert.That(parsed.NoLocalCommitCount, Is.True);
            Assert.That(parsed.Json, Is.True);
            Assert.That(parsed.RefreshReport, Is.False);
            Assert.That(parsed.Minified, Is.True);
            Assert.That(parsed.Command, Is.EqualTo(CommandKind.DbSize));
            Assert.That(parsed.AllBranches, Is.True);
        });
    }

    [TestCase("--summary")]
    [TestCase("--print-minified")]
    [TestCase("--minified")]
    public void Parse_ObsoleteOrIncompleteReportFormatting_IsError(string argument)
    {
        Assert.That(CliParser.Parse([argument]).HasParseError, Is.True);
    }

    [Test]
    public void Parse_JsonMinified_SelectsExplicitJson()
    {
        var parsed = CliParser.Parse(["--json", "--minified"]);
        Assert.Multiple(() =>
        {
            Assert.That(parsed.HasParseError, Is.False);
            Assert.That(parsed.Json, Is.True);
            Assert.That(parsed.Minified, Is.True);
        });
    }

    [Test]
    public void Parse_ConsumerAliases_NormalizedCorrectly()
    {
        using var directory = new TempDirectory();
        var savePath = Path.Combine(directory.Path, "report.json");
        var parsed = CliParser.Parse(["-paths", "/test/repo", "-save-path", savePath, "-silent"]);
        Assert.Multiple(() =>
        {
            Assert.That(parsed.PathsArgument, Is.EqualTo("/test/repo"));
            Assert.That(parsed.SavePath, Is.EqualTo(savePath));
            Assert.That(parsed.Silent, Is.True);
        });
    }

    [Test]
    public void Parse_PathsWithLiteralDashSilent_PreservesValue()
    {
        var parsed = CliParser.Parse(["--paths", "-silent"]);
        Assert.Multiple(() =>
        {
            Assert.That(parsed.PathsArgument, Is.EqualTo("-silent"));
            Assert.That(parsed.Silent, Is.False);
        });
    }

    [Test]
    public void ParseCommandLine_SingleArg_Executable_Ignored()
    {
        var parsed = CliParser.Parse(["git-wizard.exe"]);
        Assert.That(parsed.RebuildReport, Is.False);
    }

    [Test]
    public void ParseCommandLine_VersionOutput_NoAppNameDuplication()
    {
        using var sw = new StringWriter();
        var originalOut = Console.Out;
        Console.SetOut(sw);
        try
        {
            CliParser.Parse(["--version"]);
            var output = sw.ToString();
            var version = typeof(RunConfiguration).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion.Split('+')[0];
            Assert.Multiple(() =>
            {
                Assert.That(output.Trim(), Is.EqualTo(version));
                Assert.That(output, Does.Not.Match("git-wizard.*git-wizard"));
            });
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [TestCase(CommandKind.HelpPrinted)]
    [TestCase(CommandKind.IndexCacheStatus)]
    [TestCase(CommandKind.ClearIndexCache)]
    [TestCase(CommandKind.DbSize)]
    public void RunCommandCoreAsync_ImmediateExitCommand_IsRejected(CommandKind command)
    {
        var configuration = new RunConfiguration { Command = command };

        Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => Program.RunCommandCoreAsync(configuration, new GitWizardConfiguration()));
    }
}

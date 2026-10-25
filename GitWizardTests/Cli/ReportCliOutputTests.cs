using System.Runtime.Versioning;
using System.Text.Json;
using GitWizard;
using GitWizard.CLI;
using GitWizardTests.TestSupport;

namespace GitWizardTests;

[NonParallelizable]
public class ReportCliOutputTests
{
    LocalFilesSandbox? _sandbox;
    string _temporaryHome = null!;
    TextWriter _originalOut = null!;
    TextWriter _originalError = null!;
    Action<string?> _originalLogMethod = null!;
    Action<string?, GitWizardLog.LogType>? _originalTypedLogMethod;
    bool _originalSilentMode;
    bool _originalVerboseMode;
    StringWriter _consoleOut = null!;
    StringWriter _consoleError = null!;

    [SetUp]
    public void SetUp()
    {
        _sandbox = new LocalFilesSandbox();
        _temporaryHome = _sandbox.Path;
        _originalOut = Console.Out;
        _originalError = Console.Error;
        _originalLogMethod = GitWizardLog.LogMethod;
        _originalTypedLogMethod = GitWizardLog.TypedLogMethod;
        _originalSilentMode = GitWizardLog.SilentMode;
        _originalVerboseMode = GitWizardLog.VerboseMode;
        _consoleOut = new StringWriter();
        _consoleError = new StringWriter();
        Console.SetOut(_consoleOut);
        Console.SetError(_consoleError);
        GitWizardLog.LogMethod = Console.WriteLine;
        GitWizardLog.SilentMode = false;
        GitWizardLog.VerboseMode = false;
    }

    [TearDown]
    public void TearDown()
    {
        Console.SetOut(_originalOut);
        Console.SetError(_originalError);
        GitWizardLog.LogMethod = _originalLogMethod;
        GitWizardLog.TypedLogMethod = _originalTypedLogMethod;
        GitWizardLog.SilentMode = _originalSilentMode;
        GitWizardLog.VerboseMode = _originalVerboseMode;
        _consoleOut.Dispose();
        _consoleError.Dispose();
        _sandbox?.Dispose();
        _sandbox = null;
    }

    [Test]
    public async Task DefaultReport_PrintsAttentionAndOneRunSummary()
    {
        using var repository = TempRepoFixture.CreateWithInitialCommit();
        await File.WriteAllTextAsync(Path.Combine(repository.Path, "pending.txt"), "pending change");
        var configuration = new GitWizardConfiguration
        {
            SearchPaths = { repository.Path },
            IncludeTempDirectories = true
        };
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exitCode = await Program.RunCommandCoreAsync(
            CliParser.Parse(["--no-mft", "--rebuild-report", "--no-local-commit-count"]),
            configuration, stdout: stdout, stderr: stderr);
        var output = stdout.ToString();
        var error = stderr.ToString();
        Assert.Multiple(() =>
        {
            Assert.That(exitCode, Is.EqualTo(1));
            Assert.That(output, Does.Contain(GitWizardApi.NormalizePath(repository.Path)).And.Contain("dirty"));
            Assert.That(output.Split("Run: "), Has.Length.EqualTo(2));
            Assert.That(output, Does.Contain("Run: created=1 refreshed=1 skipped=0"));
            Assert.That(output, Does.Not.Contain("\"Repositories\""));
            Assert.That(error, Does.Contain("Scanning repositories")
                .And.Not.Contain("Session Started")
                .And.Not.Contain("Saving report")
                .And.Not.Contain("[SKIPPED]")
                .And.Not.Contain("[MISSING]")
                .And.Not.Contain("Warning:")
                .And.Not.Contain("Error:"));
        });
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    public async Task JsonReport_StdoutMatchesFileAndCarriesRun(bool suppliedWriters, bool minified)
    {
        using var repository = TempRepoFixture.CreateWithInitialCommit();
        await File.WriteAllTextAsync(Path.Combine(repository.Path, "pending.txt"), "pending change");
        var arguments = new List<string> { "--no-mft", "--rebuild-report", "--no-local-commit-count", "--json" };
        if (minified)
        {
            arguments.Add("--minified");
        }

        var runConfiguration = CliParser.Parse(arguments.ToArray());
        Assert.That(runConfiguration.HasParseError, Is.False);
        var configuration = new GitWizardConfiguration
        {
            SearchPaths = { repository.Path },
            IncludeTempDirectories = true
        };
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await Program.RunCommandCoreAsync(runConfiguration, configuration,
            stdout: suppliedWriters ? stdout : null, stderr: suppliedWriters ? stderr : null);

        var output = suppliedWriters ? stdout.ToString() : _consoleOut.ToString();
        var diagnostics = suppliedWriters ? stderr.ToString() : _consoleError.ToString();
        using var document = JsonDocument.Parse(output);
        using var savedDocument = JsonDocument.Parse(await File.ReadAllTextAsync(GitWizardReport.GetCachedReportPath()));

        var run = document.RootElement.GetProperty("run");
        Assert.Multiple(() =>
        {
            Assert.That(exitCode, Is.EqualTo(1));
            Assert.That(document.RootElement.GetProperty("SchemaVersion").GetString(), Is.EqualTo("2.1"));
            Assert.That(run.GetProperty("created").GetInt32(), Is.EqualTo(1));
            Assert.That(run.GetProperty("refreshed").GetInt32(), Is.EqualTo(1));
            Assert.That(run.GetProperty("skipped").GetInt32(), Is.Zero);
            Assert.That(run.GetProperty("warnings").GetArrayLength(), Is.Zero);
            Assert.That(JsonElement.DeepEquals(document.RootElement, savedDocument.RootElement), Is.True);
            var repositories = document.RootElement.GetProperty("Repositories");
            Assert.That(repositories.EnumerateObject().Count(), Is.EqualTo(1));
            Assert.That(repositories.GetProperty(GitWizardApi.NormalizePath(repository.Path))
                .GetProperty("HasPendingChanges").GetBoolean(), Is.True);
            Assert.That(diagnostics, Does.Contain("Scanning repositories"));
            if (suppliedWriters)
            {
                Assert.That(_consoleOut.ToString(), Is.Empty);
                Assert.That(_consoleError.ToString(), Is.Empty);
            }
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ReportOutput_PreservesOrderingWarnings(bool json)
    {
        var handler = new UpdateHandler();
        handler.OnSubmoduleCreated(new GitWizardRepository("parent"), new GitWizardRepository("child"));
        await using var report = new GitWizardReport();
        report.Run = handler.CaptureRun();
        using var stdout = new StringWriter();
        Program.OutputReport(report, new RunConfiguration { Json = json }, stdout);
        var output = stdout.ToString();
        if (json)
        {
            using var document = JsonDocument.Parse(output);
            Assert.That(document.RootElement.GetProperty("run").GetProperty("warnings")[0].GetString(),
                Does.StartWith("[SKIPPED] Submodule parent not created yet:"));
        }
        else
        {
            Assert.That(output.IndexOf("Run:", StringComparison.Ordinal),
                Is.LessThan(output.IndexOf("[SKIPPED]", StringComparison.Ordinal)));
            Assert.That(output, Does.Contain("skipped=1"));
        }
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public void ReportDiagnostics_RetainWarningsWithoutStderrContamination(bool json, bool silent)
    {
        GitWizardLog.SilentMode = silent;
        var handler = new UpdateHandler();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        using (new ReportLogScope(handler, new RunConfiguration { Json = json, Silent = silent }, stdout, stderr))
        {
            GitWizardLog.Log("visible warning", GitWizardLog.LogType.Warning);
            GitWizardLog.Log("visible error", GitWizardLog.LogType.Error);
            GitWizardLog.LogException(new InvalidOperationException("visible exception"), "visible context");
        }
        Assert.Multiple(() =>
        {
            var warnings = handler.CaptureRun().Warnings;
            Assert.That(warnings, Has.Some.Contains("visible warning"));
            Assert.That(warnings, Has.Some.Contains("visible error"));
            Assert.That(warnings, Has.Some.Contains("visible context"));
            Assert.That(warnings, Has.Some.Contains("visible exception"));
            Assert.That(stderr.ToString(), Is.Empty);
            Assert.That(stdout.ToString(), Is.Empty);
        });
    }

    [Test]
    public async Task NoRefreshReport_LabelsHeadingByActualCacheProvenance()
    {
        using var repository = TempRepoFixture.CreateWithInitialCommit();
        var configuration = new GitWizardConfiguration
        {
            SearchPaths = { repository.Path },
            IncludeTempDirectories = true
        };
        var arguments = new[] { "--no-mft", "--no-refresh", "--no-local-commit-count" };

        using (var missStdout = new StringWriter())
        {
            var missExitCode = await Program.RunCommandCoreAsync(
                CliParser.Parse(arguments), configuration, stdout: missStdout, stderr: TextWriter.Null);
            Assert.Multiple(() =>
            {
                Assert.That(missExitCode, Is.Zero);
                Assert.That(missStdout.ToString(), Does.Contain("Repositories: 1;"));
            });
        }

        using (var hitStdout = new StringWriter())
        {
            var hitExitCode = await Program.RunCommandCoreAsync(
                CliParser.Parse(arguments), configuration, stdout: hitStdout, stderr: TextWriter.Null);
            Assert.Multiple(() =>
            {
                Assert.That(hitExitCode, Is.Zero);
                Assert.That(hitStdout.ToString(), Does.Contain("Cached report: 1;"));
            });
        }
    }

    [Test]
    public async Task DefaultReport_InvalidSearchPath_LogsErrorOnlyToSuppliedStderr()
    {
        var configuration = new GitWizardConfiguration
        {
            SearchPaths = { Path.Combine(_temporaryHome, "missing") }
        };
        var runConfiguration = CliParser.Parse(["--rebuild-report", "--no-mft"]);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await Program.RunCommandCoreAsync(runConfiguration, configuration, stdout: stdout, stderr: stderr);

        Assert.Multiple(() =>
        {
            Assert.That(exitCode, Is.EqualTo(1));
            Assert.That(stdout.ToString(), Does.Contain("Run: created=0 refreshed=0 skipped=0").And.Contain("0 of 1 configured search paths are usable"));
            Assert.That(stderr.ToString(), Is.Empty);
            Assert.That(_consoleOut.ToString(), Is.Empty);
            Assert.That(_consoleError.ToString(), Is.Empty);
        });
    }

    [TestCase(false, "success")]
    [TestCase(true, "success")]
    [TestCase(false, "error")]
    [TestCase(true, "error")]
    [TestCase(false, "exception")]
    [TestCase(true, "exception")]
    public async Task Report_RestoresLogSinkAfterReturning(bool directReport, string outcome)
    {
        var searchPath = Path.Combine(_temporaryHome, "repositories");
        if (outcome != "error")
        {
            Directory.CreateDirectory(searchPath);
        }

        var configuration = new GitWizardConfiguration
        {
            SearchPaths = { searchPath },
            IncludeTempDirectories = true
        };
        var runConfiguration = CliParser.Parse(["--rebuild-report", "--no-mft"]);
        var laterMessages = new List<string?>();
        Action<string?> originalSink = laterMessages.Add;
        var originalTypedSink = GitWizardLog.TypedLogMethod;
        GitWizardLog.LogMethod = originalSink;
        using (var stdout = outcome == "exception" ? new FailingReportWriter() : new StringWriter())
        using (var stderr = new StringWriter())
        {
            var reportTask = directReport
                ? Program.RunDefaultReportCoreAsync(runConfiguration, configuration, stdout: stdout, stderr: stderr)
                : Program.RunCommandCoreAsync(runConfiguration, configuration, stdout: stdout, stderr: stderr);

            if (outcome == "exception")
            {
                Assert.ThrowsAsync<IOException>(async () => await reportTask);
            }
            else
            {
                Assert.That(await reportTask, Is.EqualTo(outcome == "error" ? 1 : 0));
            }

            Assert.That(GitWizardLog.LogMethod, Is.SameAs(originalSink));
            Assert.That(GitWizardLog.TypedLogMethod, Is.SameAs(originalTypedSink));
        }
        GitWizardLog.Log("after report");
        Assert.That(laterMessages, Has.Count.EqualTo(1));
        Assert.That(laterMessages[0], Does.Contain("after report"));
    }

    [Test]
    public async Task ClearCacheFailure_WithRefresh_LogsDiagnosticToStderrAndStdoutStaysJson()
    {
        using var repository = TempRepoFixture.CreateWithInitialCommit();
        GitWizardApi.EnsureLocalFolderExists();
        var cachedReportPath = GitWizardReport.GetCachedReportPath();
        await File.WriteAllTextAsync(cachedReportPath, "{}");
        var localFilesPath = GitWizardApi.GetLocalFilesPath();
        FileStream? lockedReport = null;
        UnixFileMode? originalDirectoryMode = null;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                lockedReport = new FileStream(cachedReportPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            else
            {
                originalDirectoryMode = MakeDirectoryReadOnly(localFilesPath);
                try
                {
                    File.Delete(cachedReportPath);
                    Assert.Inconclusive("The runner can delete from a read-only directory.");
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            var runConfiguration = CliParser.Parse(
                ["--clear-cache", "--no-mft", "--rebuild-report", "--no-local-commit-count", "--json"]);
            var configuration = new GitWizardConfiguration
            {
                SearchPaths = { repository.Path },
                IncludeTempDirectories = true
            };
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            Action<string?> originalSink = _consoleOut.WriteLine;
            GitWizardLog.LogMethod = originalSink;
            var exited = Program.TryHandleImmediateExitFlags(runConfiguration, stderr);
            if (lockedReport != null)
            {
                await lockedReport.DisposeAsync();
            }

            lockedReport = null;
            if (originalDirectoryMode != null && !OperatingSystem.IsWindows())
            {
                RestoreDirectoryMode(localFilesPath, originalDirectoryMode.Value);
            }

            await Program.RunCommandCoreAsync(runConfiguration, configuration, stdout: stdout, stderr: stderr);

            using var document = JsonDocument.Parse(stdout.ToString());
            Assert.Multiple(() =>
            {
                Assert.That(exited, Is.False);
                Assert.That(document.RootElement.GetProperty("SchemaVersion").GetString(), Is.EqualTo("2.1"));
                Assert.That(stderr.ToString(), Does.Contain("Caught exception trying to clear cache"));
                Assert.That(_consoleOut.ToString(), Is.Empty);
                Assert.That(GitWizardLog.LogMethod, Is.SameAs(originalSink));
            });
        }
        finally
        {
            if (lockedReport != null)
            {
                await lockedReport.DisposeAsync();
            }

            if (originalDirectoryMode != null && !OperatingSystem.IsWindows())
            {
                RestoreDirectoryMode(localFilesPath, originalDirectoryMode.Value);
            }
        }
    }

    [UnsupportedOSPlatform("windows")]
    static void RestoreDirectoryMode(string path, UnixFileMode mode) => File.SetUnixFileMode(path, mode);

    [UnsupportedOSPlatform("windows")]
    static UnixFileMode MakeDirectoryReadOnly(string path)
    {
        var original = File.GetUnixFileMode(path);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        return original;
    }

    sealed class FailingReportWriter : StringWriter
    {
        public override void WriteLine(string? value)
        {
            throw new IOException("Report output failed");
        }
    }

    [Test]
    public async Task SavedReport_PreservesRunIncludingZeroCounts()
    {
        await using var report = new GitWizardReport();
        report.Run = new ReportRunSummary { Warnings = ["ordering warning"] };
        await report.SaveAsync(GitWizardReport.GetCachedReportPath());
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(GitWizardReport.GetCachedReportPath()));
        var run = saved.RootElement.GetProperty("run");
        Assert.Multiple(() =>
        {
            Assert.That(run.GetProperty("created").GetInt32(), Is.Zero);
            Assert.That(run.GetProperty("refreshed").GetInt32(), Is.Zero);
            Assert.That(run.GetProperty("skipped").GetInt32(), Is.Zero);
            Assert.That(run.GetProperty("warnings")[0].GetString(), Is.EqualTo("ordering warning"));
        });
    }
    [TestCase(false)]
    [TestCase(true)]
    public async Task CachedJson_PreservesGenerationMetadataWithoutRewriting(bool hasRun)
    {
        GitWizardApi.EnsureLocalFolderExists();
        var cachePath = GitWizardReport.GetCachedReportPath();
        var original = hasRun
            ? """{"SchemaVersion":"2.1","Repositories":{},"run":{"created":7,"refreshed":6,"skipped":1,"warnings":["cached warning"]}}"""
            : """{"SchemaVersion":"2.1","Repositories":{}}""";
        await File.WriteAllTextAsync(cachePath, original);
        TestUtilities.ResetStaticCaches();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var parsed = CliParser.Parse(["--no-refresh", "--json"]);
        Assert.That(parsed.HasParseError, Is.False);
        var exitCode = await Program.RunCommandCoreAsync(parsed, new GitWizardConfiguration(),
            stdout: stdout, stderr: stderr);
        using var document = JsonDocument.Parse(stdout.ToString());
        var run = document.RootElement.GetProperty("run");
        Assert.Multiple(() =>
        {
            Assert.That(exitCode, Is.Zero);
            Assert.That(run.GetProperty("created").GetInt32(), Is.EqualTo(hasRun ? 7 : 0));
            Assert.That(run.GetProperty("refreshed").GetInt32(), Is.EqualTo(hasRun ? 6 : 0));
            Assert.That(run.GetProperty("skipped").GetInt32(), Is.EqualTo(hasRun ? 1 : 0));
            Assert.That(run.GetProperty("warnings").GetArrayLength(), Is.EqualTo(hasRun ? 1 : 0));
            Assert.That(File.ReadAllText(cachePath), Is.EqualTo(original));
            Assert.That(stderr.ToString(), Is.Empty);
        });
    }

    [Test]
    public async Task FilteredJson_KeepsFullRunAndUnfilteredSavedInventory()
    {
        using var first = TempRepoFixture.CreateWithInitialCommit();
        using var second = TempRepoFixture.CreateWithInitialCommit();
        var destination = Path.Combine(_temporaryHome, "filtered-save.json");
        var parsed = CliParser.Parse(["--json", "--minified", "--paths",
            string.Join(",", first.Path, second.Path), "--filter",
            GitWizardApi.NormalizePath(first.Path), "--save-path", destination]);
        Assert.That(parsed.HasParseError, Is.False);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        await Program.RunCommandCoreAsync(parsed, new GitWizardConfiguration(), stdout: stdout, stderr: stderr);
        using var output = JsonDocument.Parse(stdout.ToString());
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(destination));
        using var cached = JsonDocument.Parse(await File.ReadAllTextAsync(GitWizardReport.GetCachedReportPath()));
        Assert.Multiple(() =>
        {
            Assert.That(output.RootElement.GetProperty("Repositories").EnumerateObject().Count(), Is.EqualTo(1));
            Assert.That(saved.RootElement.GetProperty("Repositories").EnumerateObject().Count(), Is.EqualTo(2));
            Assert.That(output.RootElement.GetProperty("run").GetProperty("created").GetInt32(), Is.EqualTo(2));
            Assert.That(JsonElement.DeepEquals(output.RootElement.GetProperty("run"), saved.RootElement.GetProperty("run")), Is.True);
            Assert.That(JsonElement.DeepEquals(output.RootElement.GetProperty("run"), cached.RootElement.GetProperty("run")), Is.True);
            Assert.That(stdout.ToString().TrimEnd(), Does.Not.Contain('\n'));
        });
    }
}

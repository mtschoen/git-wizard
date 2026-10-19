using System.Runtime.Versioning;
using System.Text.Json;
using GitWizard;
using GitWizard.CLI;

namespace GitWizardTests;

[NonParallelizable]
public class ReportCliOutputTests
{
    string _temporaryHome = null!;
    TextWriter _originalOut = null!;
    TextWriter _originalError = null!;
    Action<string?> _originalLogMethod = null!;
    bool _originalSilentMode;
    bool _originalVerboseMode;
    StringWriter _consoleOut = null!;
    StringWriter _consoleError = null!;

    [SetUp]
    public void SetUp()
    {
        _temporaryHome = TestUtilities.RedirectLocalFilesToTemp();
        TestUtilities.ResetStaticCaches();
        _originalOut = Console.Out;
        _originalError = Console.Error;
        _originalLogMethod = GitWizardLog.LogMethod;
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
        GitWizardLog.SilentMode = _originalSilentMode;
        GitWizardLog.VerboseMode = _originalVerboseMode;
        _consoleOut.Dispose();
        _consoleError.Dispose();
        TestUtilities.ResetStaticCaches();
        TestUtilities.ClearLocalFilesRedirect(_temporaryHome);
    }

    [TestCase(false, false, false)]
    [TestCase(true, false, false)]
    [TestCase(false, true, true)]
    public async Task DefaultReport_StdoutContainsOnlyUnchangedJson(bool suppliedWriters, bool summary, bool minified)
    {
        using var repository = TempRepoFixture.CreateWithInitialCommit();
        await File.WriteAllTextAsync(Path.Combine(repository.Path, "pending.txt"), "pending change");
        var arguments = new List<string> { "--no-mft", "--rebuild-report", "--no-local-commit-count" };
        if (summary) arguments.Add("--summary");
        if (minified) arguments.Add("--print-minified");
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
        Assert.Multiple(() =>
        {
            Assert.That(exitCode, Is.EqualTo(1));
            Assert.That(document.RootElement.GetProperty("SchemaVersion").GetString(), Is.EqualTo("2.1"));
            Assert.That(diagnostics, Does.Contain("Session Started").And.Contain("Scanning repositories"));
            if (suppliedWriters)
            {
                Assert.That(_consoleOut.ToString(), Is.Empty);
                Assert.That(_consoleError.ToString(), Is.Empty);
            }
        });

        if (summary)
        {
            Assert.Multiple(() =>
            {
                Assert.That(document.RootElement.GetProperty("TotalRepositories").GetInt32(), Is.EqualTo(1));
                Assert.That(document.RootElement.GetProperty("Dirty").GetInt32(), Is.EqualTo(1));
                Assert.That(document.RootElement.GetProperty("NeedingAttention")[0].GetProperty("Path").GetString(),
                    Is.EqualTo(GitWizardApi.NormalizePath(repository.Path)));
            });
        }
        else
        {
            using var savedDocument = JsonDocument.Parse(await File.ReadAllTextAsync(GitWizardReport.GetCachedReportPath()));
            Assert.Multiple(() =>
            {
                Assert.That(JsonElement.DeepEquals(document.RootElement, savedDocument.RootElement), Is.True);
                var repositories = document.RootElement.GetProperty("Repositories");
                Assert.That(repositories.EnumerateObject().Count(), Is.EqualTo(1));
                Assert.That(repositories.GetProperty(GitWizardApi.NormalizePath(repository.Path))
                    .GetProperty("HasPendingChanges").GetBoolean(), Is.True);
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
            Assert.That(stdout.ToString(), Is.Empty);
            Assert.That(_consoleOut.ToString(), Is.Empty);
            Assert.That(_consoleError.ToString(), Is.Empty);
            Assert.That(stderr.ToString(), Does.Contain("Error:").And.Contain("0 of 1 configured search paths are usable"));
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
            Directory.CreateDirectory(searchPath);
        var configuration = new GitWizardConfiguration
        {
            SearchPaths = { searchPath },
            IncludeTempDirectories = true
        };
        var runConfiguration = CliParser.Parse(["--rebuild-report", "--no-mft"]);
        var laterMessages = new List<string?>();
        Action<string?> originalSink = laterMessages.Add;
        GitWizardLog.LogMethod = originalSink;
        using (var stdout = outcome == "exception" ? new FailingReportWriter() : new StringWriter())
        using (var stderr = new StringWriter())
        {
            var reportTask = directReport
                ? Program.RunDefaultReportCoreAsync(runConfiguration, configuration, stdout: stdout, stderr: stderr)
                : Program.RunCommandCoreAsync(runConfiguration, configuration, stdout: stdout, stderr: stderr);

            if (outcome == "exception")
                Assert.ThrowsAsync<IOException>(async () => await reportTask);
            else
                Assert.That(await reportTask, Is.EqualTo(outcome == "error" ? 1 : 0));

            Assert.That(GitWizardLog.LogMethod, Is.SameAs(originalSink));
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
                ["--clear-cache", "--no-mft", "--rebuild-report", "--no-local-commit-count"]);
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
                await lockedReport.DisposeAsync();
            lockedReport = null;
            if (originalDirectoryMode != null && !OperatingSystem.IsWindows())
                RestoreDirectoryMode(localFilesPath, originalDirectoryMode.Value);
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
                await lockedReport.DisposeAsync();
            if (originalDirectoryMode != null && !OperatingSystem.IsWindows())
                RestoreDirectoryMode(localFilesPath, originalDirectoryMode.Value);
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
}

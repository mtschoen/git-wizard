using System.Diagnostics;
using System.Text.Json;
using GitWizard;

namespace GitWizardTests;

public partial class ProgramEntryPointTests
{
    [Test]
    public async Task Main_Sweep_PrintsIndentedJsonToStandardOutputAndReturnsZero()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddOriginRemoteAndPush();

        var result = await RunCliAsync("--sweep", "--paths", fixture.Path);

        using var document = JsonDocument.Parse(result.StandardOutput);
        var repository = document.RootElement.GetProperty("Repositories")[0];
        Assert.Multiple(() =>
        {
            Assert.That(result.ExitCode, Is.Zero);
            Assert.That(result.StandardError, Is.Empty);
            Assert.That(result.StandardOutput.TrimEnd(), Does.Contain(Environment.NewLine));
            Assert.That(document.RootElement.GetProperty("SchemaVersion").GetString(), Is.EqualTo("1.0"));
            Assert.That(repository.GetProperty("Path").GetString(), Is.EqualTo(fixture.Path));
            Assert.That(repository.GetProperty("DirtyTrackedFiles").GetArrayLength(), Is.Zero);
            Assert.That(repository.GetProperty("UnpushedBranches").GetArrayLength(), Is.Zero);
            Assert.That(repository.GetProperty("StashCount").GetInt32(), Is.Zero);
        });
    }

    [Test]
    public async Task Main_SweepWithPrintMinified_PrintsSingleLineJsonAndReturnsZero()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddOriginRemoteAndPush();

        var result = await RunCliAsync("--sweep", "--print-minified", "--paths", fixture.Path);

        using var document = JsonDocument.Parse(result.StandardOutput);
        Assert.Multiple(() =>
        {
            Assert.That(result.ExitCode, Is.Zero);
            Assert.That(result.StandardError, Is.Empty);
            Assert.That(result.StandardOutput.TrimEnd(), Does.Not.Contain('\n'));
            Assert.That(document.RootElement.GetProperty("Repositories").GetArrayLength(), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Main_SweepWithUnknownOption_ReturnsParseErrorWithoutJson()
    {
        var result = await RunCliAsync("--sweep", "--unknown-sweep-option");

        Assert.Multiple(() =>
        {
            Assert.That(result.ExitCode, Is.EqualTo(2));
            Assert.That(result.StandardOutput, Does.Not.Contain("\"Repositories\""));
        });
    }

    [Test]
    public async Task Main_ScanOnly_PrintsRepositoryPathsToStdoutAndDiagnosticsToStderr()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var tempConfigFile = Path.GetTempFileName();
        try
        {
            var config = new GitWizardConfiguration
            {
                SearchPaths = { fixture.Path }
            };
            await config.SaveAsync(tempConfigFile);

            var result = await RunCliAsync("--config-path", tempConfigFile, "--scan-only", "--no-mft");

            Assert.Multiple(() =>
            {
                Assert.That(result.ExitCode, Is.Zero);
                // Stdout contains ONLY discovered repository paths, no session banner or log lines
                Assert.That(result.StandardOutput.Trim(), Is.EqualTo(fixture.Path).IgnoreCase);
                Assert.That(result.StandardOutput, Does.Not.Contain("Session Start Message"));
                Assert.That(result.StandardOutput, Does.Not.Contain("git-wizard Session Started"));
                // Stderr contains session banner / diagnostics
                Assert.That(result.StandardError, Does.Contain("Session Start Message"));
                Assert.That(result.StandardError, Does.Contain("git-wizard Session Started"));
            });
        }
        finally
        {
            if (File.Exists(tempConfigFile))
                File.Delete(tempConfigFile);
        }
    }

    [Test]
    public async Task Main_ScanOnly_AllInvalidSearchPaths_ExitsNonZeroWithUsableSummary()
    {
        var tempConfigFile = Path.GetTempFileName();
        try
        {
            var config = new GitWizardConfiguration
            {
                SearchPaths = { "/nonexistent/search/one", "/nonexistent/search/two", "/nonexistent/search/three" }
            };
            await config.SaveAsync(tempConfigFile);

            var result = await RunCliAsync("--config-path", tempConfigFile, "--scan-only", "--no-mft");

            Assert.Multiple(() =>
            {
                Assert.That(result.ExitCode, Is.EqualTo(1));
                // Stdout is completely empty
                Assert.That(result.StandardOutput.Trim(), Is.Empty);
                // Stderr contains per-path errors and summary message
                Assert.That(result.StandardError, Does.Contain("0 of 3 configured search paths are usable"));
                Assert.That(result.StandardError, Does.Contain("Could not get repository paths at /nonexistent/search/one"));
            });
        }
        finally
        {
            if (File.Exists(tempConfigFile))
                File.Delete(tempConfigFile);
        }
    }

    [Test]
    public async Task Main_DefaultReportMode_AllInvalidSearchPaths_ExitsNonZero()
    {
        var tempConfigFile = Path.GetTempFileName();
        try
        {
            var config = new GitWizardConfiguration
            {
                SearchPaths = { "/nonexistent/search/one", "/nonexistent/search/two" }
            };
            await config.SaveAsync(tempConfigFile);

            var result = await RunCliAsync("--config-path", tempConfigFile, "--rebuild-report", "--no-mft");

            Assert.Multiple(() =>
            {
                Assert.That(result.ExitCode, Is.EqualTo(1));
                Assert.That(result.StandardOutput, Does.Not.Contain("\"Repositories\""));
            });
        }
        finally
        {
            if (File.Exists(tempConfigFile))
                File.Delete(tempConfigFile);
        }
    }

    [Test]
    public async Task Main_ScanOnly_WithFilter_FiltersStdout()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var tempConfigFile = Path.GetTempFileName();
        try
        {
            var config = new GitWizardConfiguration
            {
                SearchPaths = { fixture.Path }
            };
            await config.SaveAsync(tempConfigFile);

            var result = await RunCliAsync("--config-path", tempConfigFile, "--scan-only", "--filter", "nonexistentfilterpattern", "--no-mft");

            Assert.Multiple(() =>
            {
                Assert.That(result.ExitCode, Is.Zero);
                Assert.That(result.StandardOutput.Trim(), Is.Empty);
                Assert.That(result.StandardError, Does.Contain("Session Start Message"));
            });
        }
        finally
        {
            if (File.Exists(tempConfigFile))
                File.Delete(tempConfigFile);
        }
    }

    [Test]
    public async Task Main_Tags_PrintsIndentedJsonToStandardOutputAndReturnsZero()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddTag("archive/v1");

        var result = await RunCliAsync("tags", "-m", "archive/*", "-p", fixture.Path);

        using var document = JsonDocument.Parse(result.StandardOutput);
        var repository = document.RootElement.GetProperty("Repositories")[0];
        Assert.Multiple(() =>
        {
            Assert.That(result.ExitCode, Is.Zero);
            Assert.That(result.StandardError, Is.Empty);
            Assert.That(result.StandardOutput.TrimEnd(), Does.Contain(Environment.NewLine));
            Assert.That(document.RootElement.GetProperty("SchemaVersion").GetString(), Is.EqualTo("1.0"));
            Assert.That(document.RootElement.GetProperty("MatchPattern").GetString(), Is.EqualTo("archive/*"));
            Assert.That(repository.GetProperty("Path").GetString(), Is.EqualTo(GitWizardApi.NormalizePath(fixture.Path)));
            var tags = repository.GetProperty("Tags");
            Assert.That(tags.GetArrayLength(), Is.EqualTo(1));
            Assert.That(tags[0].GetProperty("Name").GetString(), Is.EqualTo("archive/v1"));
        });
    }

    [Test]
    public async Task Main_TagsWithPrintMinified_PrintsSingleLineJson()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddTag("v1.0.0");

        var result = await RunCliAsync("tags", "--print-minified", "-p", fixture.Path);

        using var document = JsonDocument.Parse(result.StandardOutput);
        Assert.Multiple(() =>
        {
            Assert.That(result.ExitCode, Is.Zero);
            Assert.That(result.StandardError, Is.Empty);
            Assert.That(result.StandardOutput.TrimEnd(), Does.Not.Contain('\n'));
            Assert.That(document.RootElement.GetProperty("Repositories").GetArrayLength(), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Main_Branches_PrintsIndentedJsonToStandardOutputAndReturnsZero()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.CommitOnNewBranch("feature-test", "f.txt");

        var result = await RunCliAsync("branches", "--not-merged", "main", "-p", fixture.Path);

        using var document = JsonDocument.Parse(result.StandardOutput);
        var repository = document.RootElement.GetProperty("Repositories")[0];
        Assert.Multiple(() =>
        {
            Assert.That(result.ExitCode, Is.Zero);
            Assert.That(result.StandardError, Is.Empty);
            Assert.That(result.StandardOutput.TrimEnd(), Does.Contain(Environment.NewLine));
            Assert.That(document.RootElement.GetProperty("SchemaVersion").GetString(), Is.EqualTo("1.0"));
            Assert.That(document.RootElement.GetProperty("BaseBranch").GetString(), Is.EqualTo("main"));
            Assert.That(repository.GetProperty("Path").GetString(), Is.EqualTo(GitWizardApi.NormalizePath(fixture.Path)));
            var branches = repository.GetProperty("Branches");
            Assert.That(branches.GetArrayLength(), Is.EqualTo(1));
            Assert.That(branches[0].GetProperty("Name").GetString(), Is.EqualTo("feature-test"));
            Assert.That(branches[0].GetProperty("UniqueCommitCount").GetInt32(), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Main_BranchesWithPrintMinified_PrintsSingleLineJson()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.CommitOnNewBranch("feature-min", "f.txt");

        var result = await RunCliAsync("branches", "--print-minified", "-p", fixture.Path);

        using var document = JsonDocument.Parse(result.StandardOutput);
        Assert.Multiple(() =>
        {
            Assert.That(result.ExitCode, Is.Zero);
            Assert.That(result.StandardError, Is.Empty);
            Assert.That(result.StandardOutput.TrimEnd(), Does.Not.Contain('\n'));
            Assert.That(document.RootElement.GetProperty("Repositories").GetArrayLength(), Is.EqualTo(1));
        });
    }

    static Task<(int ExitCode, string StandardOutput, string StandardError)> RunCliAsync(params string[] args) =>
        RunCliInHomeAsync(Environment.GetEnvironmentVariable("GITWIZARD_HOME"), args);

    static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunCliInHomeAsync(
        string? home,
        params string[] args)
    {
        var executablePath = GetCliExecutablePath();
        var startInfo = new ProcessStartInfo(executablePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        if (!string.IsNullOrEmpty(home))
            startInfo.Environment["GITWIZARD_HOME"] = home;

        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start CLI at {executablePath}.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return (
            process.ExitCode,
            await standardOutput.ConfigureAwait(false),
            await standardError.ConfigureAwait(false));
    }

    static string GetCliExecutablePath()
    {
        var testOutputDirectory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        var configurationDirectory = testOutputDirectory.Parent
            ?? throw new InvalidOperationException("Could not determine the build configuration directory.");
        var repositoryRoot = configurationDirectory.Parent?.Parent?.Parent
            ?? throw new InvalidOperationException("Could not determine the repository root.");
        var executableName = OperatingSystem.IsWindows() ? "git-wizard.exe" : "git-wizard";

        return Path.Combine(
            repositoryRoot.FullName,
            "git-wizard",
            "bin",
            configurationDirectory.Name,
            testOutputDirectory.Name,
            executableName);
    }
}

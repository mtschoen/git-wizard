using System.Diagnostics;

namespace GitWizardTests;

internal static class CliProcess
{
    internal static Task<(int ExitCode, string StandardOutput, string StandardError)> RunCliAsync(params string[] args) =>
        RunCliInHomeAsync(Environment.GetEnvironmentVariable("GITWIZARD_HOME"), args);

    internal static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunCliInHomeAsync(
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
        {
            startInfo.Environment["GITWIZARD_HOME"] = home;
        }

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

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

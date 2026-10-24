using System.Diagnostics;
using System.Text;
using LibGit2Sharp;

namespace GitWizard;

/// <summary>
/// Outcome of one git CLI invocation. <see cref="Succeeded"/> is false when git failed to
/// start, timed out or exited non-zero; the failure has already been logged.
/// </summary>
internal sealed record GitCliResult(bool Succeeded, string Output);

/// <summary>
/// Runs the git CLI against a repository for operations LibGit2Sharp does not offer
/// (patch-id comparison, worktree listing, branch and stash deletion).
/// </summary>
internal static class GitCli
{
    const int TimeoutMilliseconds = 30000;

    /// <summary>
    /// Run git in the repository's working directory (or via --git-dir for a bare repository),
    /// logging any failure with the command line and git's stderr.
    /// </summary>
    public static GitCliResult Run(Repository repository, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        var workingDirectory = repository.Info.WorkingDirectory;
        if (!string.IsNullOrEmpty(workingDirectory))
        {
            startInfo.ArgumentList.Add("-C");
            startInfo.ArgumentList.Add(workingDirectory);
        }
        else
        {
            startInfo.ArgumentList.Add("--git-dir");
            startInfo.ArgumentList.Add(Path.GetFullPath(repository.Info.Path));
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var commandLine = $"git {string.Join(' ', arguments)} ({workingDirectory ?? repository.Info.Path})";
        try
        {
            return RunProcess(startInfo, commandLine);
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, $"Exception running {commandLine}");
            return new GitCliResult(false, string.Empty);
        }
    }

    static GitCliResult RunProcess(ProcessStartInfo startInfo, string commandLine)
    {
        using var process = Process.Start(startInfo);
        if (process == null)
        {
            GitWizardLog.Log($"Failed to start {commandLine}", GitWizardLog.LogType.Warning);
            return new GitCliResult(false, string.Empty);
        }

        // Drain both pipes while git runs so a large output cannot block it until the timeout.
        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();
        process.OutputDataReceived += (_, args) => AppendLine(outputBuilder, args.Data);
        process.ErrorDataReceived += (_, args) => AppendLine(errorBuilder, args.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var timedOut = !process.WaitForExit(TimeoutMilliseconds);
        if (timedOut)
        {
            process.Kill();
        }

        // The untimed overload also waits for the output callbacks, which the timed one can outrun.
        process.WaitForExit();
        var output = outputBuilder.ToString();
        var error = errorBuilder.ToString();

        if (timedOut)
        {
            GitWizardLog.Log($"{commandLine} timed out", GitWizardLog.LogType.Warning);
            return new GitCliResult(false, output);
        }

        if (process.ExitCode != 0)
        {
            GitWizardLog.Log($"{commandLine} exited {process.ExitCode}: {error.Trim()}", GitWizardLog.LogType.Warning);
            return new GitCliResult(false, output);
        }

        return new GitCliResult(true, output);
    }

    static void AppendLine(StringBuilder builder, string? line)
    {
        if (line == null)
        {
            return;
        }

        builder.Append(line).Append('\n');
    }
}

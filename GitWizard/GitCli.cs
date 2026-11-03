using System.Diagnostics;
using System.Text;
using LibGit2Sharp;

namespace GitWizard;

/// <summary>
/// Outcome of one git CLI invocation. <see cref="Succeeded"/> is false when git failed to
/// start, timed out or exited non-zero. Status callers own their fallback warning;
/// other operations log failures here according to their invocation mode.
/// </summary>
internal sealed record GitCliResult(bool Succeeded, string Output);

/// <summary>
/// Runs the git CLI for repository operations and large-repository status queries.
/// </summary>
internal static class GitCli
{
    const int TimeoutMilliseconds = 30000;

    // Git environment variables that redirect repository, worktree, index, object-store or config
    // lookup. Clearing them ensures child git commands operate on the target repository rather than
    // inheriting overrides from a hook, alias or parent worktree environment.
    static readonly string[] RepositoryEnvironmentVariables =
    [
        "GIT_DIR",
        "GIT_WORK_TREE",
        "GIT_INDEX_FILE",
        "GIT_COMMON_DIR",
        "GIT_OBJECT_DIRECTORY",
        "GIT_ALTERNATE_OBJECT_DIRECTORIES",
        "GIT_IMPLICIT_WORK_TREE",
        "GIT_PREFIX",
        "GIT_GRAFT_FILE",
        "GIT_SHALLOW_FILE",
        "GIT_NO_REPLACE_OBJECTS",
        "GIT_REPLACE_REF_BASE",
        "GIT_CONFIG",
        "GIT_CONFIG_PARAMETERS",
        "GIT_CONFIG_COUNT"
    ];

    internal static void ClearRepositoryEnvironment(ProcessStartInfo startInfo)
    {
        foreach (var variable in RepositoryEnvironmentVariables)
        {
            startInfo.Environment.Remove(variable);
        }
    }

    /// <summary>
    /// Run git in the repository's working directory (or via --git-dir for a bare repository),
    /// logging any failure with the command line and git's stderr.
    /// </summary>
    public static GitCliResult Run(Repository repository, params string[] arguments) =>
        RunCore(repository, logNonZeroExit: true, arguments);

    /// <summary>
    /// Run git like <see cref="Run"/>, but do not log a warning when git exits non-zero.
    /// </summary>
    public static GitCliResult RunQuiet(Repository repository, params string[] arguments) =>
        RunCore(repository, logNonZeroExit: false, arguments);

    /// <summary>
    /// Preserve NULs and embedded newlines for porcelain status. The repository caller owns
    /// the once-per-repository fallback warning, including start failures and timeouts.
    /// </summary>
    internal static GitCliResult RunStatus(Repository repository, params string[] arguments) =>
        RunCore(repository, logNonZeroExit: false, arguments, statusOutput: true);

    internal static GitCliResult RunIndexRefresh(Repository repository) =>
        RunCore(repository, logNonZeroExit: true, ["update-index", "--refresh"], indexRefresh: true);

    static GitCliResult RunCore(Repository repository, bool logNonZeroExit, string[] arguments,
        bool statusOutput = false, bool indexRefresh = false)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };
        ClearRepositoryEnvironment(startInfo);

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
            // Synchronous refresh owns this process until exit; all internal awaits use
            // ConfigureAwait(false), so no caller synchronization context is required.
            return RunProcess(startInfo, commandLine, logNonZeroExit, statusOutput, indexRefresh).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            if (!statusOutput)
            {
                GitWizardLog.LogException(exception, $"Exception running {commandLine}");
            }
            return new GitCliResult(false, string.Empty);
        }
    }

    static async Task<GitCliResult> RunProcess(ProcessStartInfo startInfo, string commandLine, bool logNonZeroExit,
        bool statusOutput, bool indexRefresh)
    {
        using var process = Process.Start(startInfo);
        if (process == null)
        {
            if (!statusOutput)
            {
                GitWizardLog.Log($"Failed to start {commandLine}", GitWizardLog.LogType.Warning);
            }
            return new GitCliResult(false, string.Empty);
        }

        // Drain both pipes while git runs so a large output cannot block it until the timeout.
        var outputBuilder = new StringBuilder();
        using var timeout = new CancellationTokenSource(TimeoutMilliseconds);
        var errorOutput = process.StandardError.ReadToEndAsync(timeout.Token);
        Task<string>? rawOutput = null;
        if (statusOutput)
        {
            rawOutput = process.StandardOutput.ReadToEndAsync(timeout.Token);
        }
        else
        {
            process.OutputDataReceived += (_, args) => AppendLine(outputBuilder, args.Data);
            process.BeginOutputReadLine();
        }

        string output;
        try
        {
            var exitAndErrors = Task.WhenAll(process.WaitForExitAsync(timeout.Token), errorOutput);
            var raw = await ReadProcessOutputAsync(exitAndErrors, rawOutput ?? Task.FromResult(string.Empty),
                timeout.Token).ConfigureAwait(false);
            output = statusOutput ? raw : outputBuilder.ToString();
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            if (!statusOutput)
            {
                GitWizardLog.Log($"{commandLine} timed out", GitWizardLog.LogType.Warning);
            }

            // Do not wait again: an exited parent can leave descendants holding its pipes.
            return new GitCliResult(false, string.Empty);
        }

        var error = await errorOutput.ConfigureAwait(false);
        var expectedNeedsUpdate = indexRefresh && process.ExitCode == 1 && string.IsNullOrWhiteSpace(error);
        if (process.ExitCode != 0 && !expectedNeedsUpdate)
        {
            if (logNonZeroExit)
            {
                GitWizardLog.Log($"{commandLine} exited {process.ExitCode}: {error.Trim()}", GitWizardLog.LogType.Warning);
            }

            return new GitCliResult(false, output);
        }

        return new GitCliResult(true, output);
    }

    internal static async Task<string> ReadProcessOutputAsync(Task processExit, Task<string> output,
        CancellationToken cancellationToken)
    {
        // One budget covers both process completion and inherited output handles.
        await Task.WhenAll(processExit, output).WaitAsync(cancellationToken).ConfigureAwait(false);
        return await output.ConfigureAwait(false);
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

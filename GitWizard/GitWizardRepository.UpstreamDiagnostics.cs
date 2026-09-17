using System.Collections.Concurrent;
using LibGit2Sharp;

namespace GitWizard;

public partial class GitWizardRepository
{
    // git-wizard#185 and its includeIf follow-up: branch.<name>.remote may hold a URL (git's
    // "fetch from this URL" form) or a stale remote name instead of naming a configured remote,
    // and either can also be true only through a "gitdir:" conditional include that a
    // standalone, repository-less config read never evaluates. ResolveTrackedBranch below is the
    // single source of truth for the DECISION (native Branch.TrackedBranch resolution, which
    // reads the repository's own config and therefore does evaluate includeIf); this standalone
    // read exists only to name the offending value in the diagnostic. Neither Branch.RemoteName
    // nor Repository.Config may be used: in LibGit2Sharp 0.31.0 the first access to
    // Repository.Config builds a configuration from <Info.Path>/config and installs it on the
    // native repository, which for a linked worktree (Info.Path = <common>/worktrees/<name>)
    // drops the shared config and with it every remote and tracking entry. Configuration.BuildFrom
    // creates a standalone configuration that is never attached to the repository handle, so it
    // reads the shared config with git's own parser and leaves the repository untouched.
    static string? GetUnresolvedUpstreamRemote(Repository repository, Branch branch)
    {
        var configurationPath = Path.Combine(GetCommonRepositoryDirectory(repository), "config");
        if (!File.Exists(configurationPath))
            return null;

        string? configuredRemote;
        using (var configuration = Configuration.BuildFrom(configurationPath))
        {
            configuredRemote = configuration.Get<string>("branch", branch.FriendlyName, "remote")?.Value;
        }

        // Unset, empty, and "." (local-to-local tracking) all mean "no remote upstream".
        if (string.IsNullOrEmpty(configuredRemote) || configuredRemote == ".")
            return null;

        // Compare against remote NAMES by enumeration. Do not index Remotes[configuredRemote]:
        // for a URL value that native lookup also throws InvalidSpecificationException.
        return repository.Network.Remotes.Any(remote => remote.Name == configuredRemote)
            ? null
            : configuredRemote;
    }

    /// <summary>
    /// Resolve <paramref name="branch"/>'s upstream the way git itself would: through native
    /// <see cref="Branch.TrackedBranch"/> resolution, which reads through the repository's own
    /// config and therefore evaluates <c>includeIf</c> conditional includes. This is the single
    /// decision point <see cref="CollectBranches"/> and <see cref="RefreshRemoteDivergence"/>
    /// both call, so the two callers cannot drift:
    /// <list type="bullet">
    /// <item><description><see cref="Branch.TrackedBranch"/> throwing
    /// <see cref="InvalidSpecificationException"/> means either the effective
    /// <c>branch.&lt;name&gt;.remote</c> is a URL/unresolvable remote name, or the branch's merge
    /// ref is malformed. <see cref="GetEffectiveUpstreamRemoteViaGit"/> asks git itself (which
    /// also evaluates <c>includeIf</c>) for the effective remote value. Only the first case is
    /// this method's to report: when that value is non-empty, not <c>"."</c>, and does not name a
    /// configured remote, the diagnostic below is logged with that value and this method returns
    /// <c>null</c>. Otherwise - the effective remote is fine, is <c>"."</c>, or the git read
    /// itself failed - the exception is rethrown so the caller's existing exception diagnostics
    /// report the real problem (a malformed merge ref, most commonly).</description></item>
    /// <item><description><see cref="Branch.TrackedBranch"/> returning <c>null</c> means no
    /// usable upstream. The stale-name diagnostic is logged only when the standalone read shows
    /// a non-empty, non-"." value that does not name a configured remote.
    /// </description></item>
    /// <item><description><see cref="Branch.TrackedBranch"/> resolving to a branch means the
    /// branch is tracked, whatever the standalone read says.</description></item>
    /// </list>
    /// </summary>
    static Branch? ResolveTrackedBranch(Repository repository, Branch branch)
    {
        try
        {
            var trackedBranch = branch.TrackedBranch;
            if (trackedBranch == null)
            {
                var configuredRemote = GetUnresolvedUpstreamRemote(repository, branch);
                if (configuredRemote != null)
                    LogUnresolvedUpstreamRemoteOnce(repository, branch.FriendlyName, configuredRemote);
            }

            return trackedBranch;
        }
        catch (InvalidSpecificationException)
        {
            var effectiveRemote = GetEffectiveUpstreamRemoteViaGit(repository, branch);
            var namesConfiguredRemote = effectiveRemote != null &&
                repository.Network.Remotes.Any(remote => remote.Name == effectiveRemote);
            if (string.IsNullOrEmpty(effectiveRemote) || effectiveRemote == "." || namesConfiguredRemote)
                throw;

            LogUnresolvedUpstreamRemoteOnce(repository, branch.FriendlyName, effectiveRemote);
            return null;
        }
    }

    // Runs only on the InvalidSpecificationException path above, which is rare, so a subprocess
    // is acceptable here (unlike the per-branch GetUnresolvedUpstreamRemote read, which runs for
    // every untracked branch and stays a standalone Configuration.BuildFrom read). git itself
    // evaluates includeIf, and in a linked worktree it reads the shared config, so this runs in
    // the repository's working directory (or via --git-dir for a bare repository / no working
    // directory) rather than duplicating git's config-layering logic in managed code.
    static string? GetEffectiveUpstreamRemoteViaGit(Repository repository, Branch branch)
    {
        var workingDirectory = repository.Info.WorkingDirectory;
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        if (!string.IsNullOrEmpty(workingDirectory))
        {
            psi.ArgumentList.Add("-C");
            psi.ArgumentList.Add(workingDirectory);
        }
        else
        {
            psi.ArgumentList.Add("--git-dir");
            psi.ArgumentList.Add(Path.GetFullPath(repository.Info.Path));
        }

        psi.ArgumentList.Add("config");
        psi.ArgumentList.Add("--null");
        psi.ArgumentList.Add("--get");
        psi.ArgumentList.Add($"branch.{branch.FriendlyName}.remote");

        try
        {
            using var process = System.Diagnostics.Process.Start(psi);
            if (process == null)
                return null;

            process.BeginErrorReadLine(); // drained but unused: only to keep a full stderr buffer from blocking WaitForExit.

            // Drain stdout while git runs: a value larger than the pipe buffer otherwise blocks
            // git until the timeout below kills it (a 131,096-char value stalled 5 s).
            var standardOutput = process.StandardOutput;
            var outputTask = Task.Run(standardOutput.ReadToEnd);

            var timedOut = !process.WaitForExit(5000);
            if (timedOut)
                process.Kill();

            // Waits for the error-stream callback, which the timed overload can outrun. Once git
            // has exited or been killed its end of the pipe is closed, so the stdout read ends
            // too and is collected here, before the process is disposed.
            process.WaitForExit();
            var output = outputTask.GetAwaiter().GetResult();

            if (timedOut || process.ExitCode != 0)
                return null;

            // --null ends the value with one NUL, so it is read raw and never trimmed: git already
            // dropped unquoted whitespace, and a quoted " origin " must not match a real "origin".
            var value = output.EndsWith('\0') ? output[..^1] : output;
            return value.Length > 0 ? value : null;
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception,
                $"Exception reading effective branch.{branch.FriendlyName}.remote via git for {workingDirectory ?? repository.Info.Path}");
            return null;
        }
    }

    // One diagnostic per (common repository, branch, value) per refresh: every worktree of a
    // repository shares one config, so a whole-machine refresh otherwise logs this once per
    // scanned worktree path (3,698 records from one refresh in the incident). The set belongs to
    // the outermost refresh on the current logical call flow: AsyncLocal flows into the
    // Parallel.ForEach workers that refresh repositories, worktrees, and submodules, so nested
    // refreshes share it, and the next refresh starts empty and logs a still-broken branch again.
    static readonly AsyncLocal<ConcurrentDictionary<string, byte>?> UnresolvedUpstreamRemoteDiagnostics = new();

    /// <summary>
    /// The per-refresh diagnostic scope. <see cref="Open"/> starts a fresh set unless one is
    /// already open on this call flow; disposing clears it only if this instance opened it.
    /// </summary>
    internal readonly struct UnresolvedUpstreamDiagnosticScope : IDisposable
    {
        readonly bool _openedSet;

        UnresolvedUpstreamDiagnosticScope(bool openedSet) => _openedSet = openedSet;

        internal static UnresolvedUpstreamDiagnosticScope Open()
        {
            if (UnresolvedUpstreamRemoteDiagnostics.Value != null)
                return new UnresolvedUpstreamDiagnosticScope(false);

            UnresolvedUpstreamRemoteDiagnostics.Value = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
            return new UnresolvedUpstreamDiagnosticScope(true);
        }

        public void Dispose()
        {
            if (_openedSet)
                UnresolvedUpstreamRemoteDiagnostics.Value = null;
        }
    }

    static void LogUnresolvedUpstreamRemoteOnce(Repository repository, string branchName, string configuredRemote)
    {
        var commonDirectory = GetCommonRepositoryDirectory(repository);
        var loggedKeys = UnresolvedUpstreamRemoteDiagnostics.Value;
        if (loggedKeys != null && !loggedKeys.TryAdd($"{commonDirectory}|{branchName}|{configuredRemote}", 0))
            return;

        GitWizardLog.Log(
            $"Branch '{branchName}' has branch.{branchName}.remote set to '{configuredRemote}', which does not name a configured remote; reporting the branch as untracked. Common repository: {commonDirectory}.",
            GitWizardLog.LogType.Warning);
    }

    // LibGit2Sharp exposes only Info.Path: the common git directory for the main worktree, or
    // <common>/worktrees/<name>/ for a linked worktree. A linked worktree's gitdir holds a
    // `commondir` file naming the common directory (relative to the gitdir, usually "../..", or
    // absolute), which is git's own answer and does not misfire on a checkout that merely lives
    // under a directory named "worktrees". Both branches return a full path with forward
    // slashes and no trailing separator, so every worktree of one repository shares one key.
    static string GetCommonRepositoryDirectory(Repository repository)
    {
        var gitDirectory = Path.GetFullPath(repository.Info.Path);
        var commonDirectoryFile = Path.Combine(gitDirectory, "commondir");
        if (File.Exists(commonDirectoryFile))
        {
            var commonDirectory = File.ReadAllText(commonDirectoryFile).Trim();
            gitDirectory = Path.GetFullPath(Path.Combine(gitDirectory, commonDirectory));
        }

        return gitDirectory.Replace('\\', '/').TrimEnd('/');
    }
}

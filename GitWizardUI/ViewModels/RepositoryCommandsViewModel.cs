using System.Diagnostics;
using System.Text;
using GitWizard;
using GitWizardUI.ViewModels.Services;

namespace GitWizardUI.ViewModels;

public sealed class RepositoryCommandsViewModel
{
    readonly IUiDispatcher _ui;
    readonly IUserDialogs _dialogs;
    readonly IClipboardService _clipboard;
    readonly TimeProvider _timeProvider = TimeProvider.System;
    readonly RepositoryListViewModel _repositoryList;
    readonly IUpdateHandler _updateHandler;
    readonly ErrorLogViewModel _errorLog;

    internal TimeProvider TimeProvider
    {
        init => _timeProvider = value;
    }

    internal RepositoryCommandsViewModel(IUiDispatcher ui, IUserDialogs dialogs,
        IClipboardService clipboard, RepositoryListViewModel repositoryList,
        IUpdateHandler updateHandler, ErrorLogViewModel errorLog)
    {
        _ui = ui;
        _dialogs = dialogs;
        _clipboard = clipboard;
        _repositoryList = repositoryList;
        _updateHandler = updateHandler;
        _errorLog = errorLog;
    }

    public void OpenInExplorer(RepositoryNodeViewModel? node)
    {
        if (node == null)
        {
            return;
        }

        if (node.IsGroupHeader)
        {
            _repositoryList.ToggleGroupExpand(node);
            return;
        }

        if (string.IsNullOrEmpty(node.WorkingDirectory))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = node.WorkingDirectory,
                UseShellExecute = true,
                Verb = "open"
            });
        }
        catch (Exception exception)
        {
            ShowAlert("Error", $"Could not open folder: {exception.Message}");
            _errorLog.PostError(_ui, $"Could not open folder: {exception.Message}", node.WorkingDirectory);
        }
    }

    public async Task OpenInForkAsync(RepositoryNodeViewModel? node)
    {
        if (node == null || string.IsNullOrEmpty(node.WorkingDirectory))
        {
            return;
        }

        if (!Directory.Exists(node.WorkingDirectory))
        {
            ShowAlert("Error", "Invalid repository path");
            return;
        }

        var configuration = await GitWizardConfiguration.GetGlobalConfigurationAsync().ConfigureAwait(false);
        var forkPath = configuration.ForkPath;

        if (string.IsNullOrWhiteSpace(forkPath))
        {
            forkPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Fork", "Fork.exe");
        }
        else
        {
            // A user-configured path may contain environment-variable references (e.g.
            // %LocalAppData%\Fork\Fork.exe) or a ~ home-directory alias, same as SearchPaths/
            // IgnoredPaths. Expand it the same way before checking for/launching the exe.
            forkPath = GitWizardApi.PrettyPrintPath(forkPath);
        }

        if (!File.Exists(forkPath))
        {
            ShowAlert("Error", $"Fork not found at: {forkPath}\n\nPlease ensure Fork is installed.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = forkPath,
                Arguments = $"\"{node.WorkingDirectory}\"",
                UseShellExecute = false,
                CreateNoWindow = false
            });
        }
        catch (Exception exception)
        {
            ShowAlert("Error", $"Could not launch Fork: {exception.Message}");
            _errorLog.PostError(_ui, $"Could not launch Fork: {exception.Message}", node.WorkingDirectory);
        }
    }

    // How long the per-row "✓ Copied" indicator stays lit after a copy before it clears.
    const int CopiedIndicatorMilliseconds = 1500;

    /// <summary>
    /// Copies the node's working directory to the clipboard, then lights the row's transient "copied"
    /// indicator for <see cref="CopiedIndicatorMilliseconds"/> ms before clearing it (this replaced
    /// the old modal "Copied" alert). A clipboard failure propagates to the caller (code-behind
    /// observes faults via <c>RunAndLogFaults</c>); on failure the indicator is never lit because the
    /// awaited write throws before it is set. Public so the behavior is awaitable in tests. The
    /// indicator flag lives on the VM node, so it is set/reset on the UI thread via <c>_ui.Post</c>.
    /// </summary>
    public async Task CopyToClipboardAsync(RepositoryNodeViewModel? node)
    {
        if (node == null || string.IsNullOrEmpty(node.WorkingDirectory))
        {
            return;
        }

        await _clipboard.SetPlainTextAsync(node.WorkingDirectory).ConfigureAwait(false);
        _ui.Post(() => node.JustCopied = true);
        await Task.Delay(TimeSpan.FromMilliseconds(CopiedIndicatorMilliseconds), _timeProvider)
            .ConfigureAwait(false);
        _ui.Post(() => node.JustCopied = false);
    }

    public void DeepRefreshRepository(RepositoryNodeViewModel? node)
    {
        if (node?.IsGroupHeader != false)
        {
            return;
        }

        node.Status = RefreshStatus.Refreshing;
        Task.Run(() =>
        {
            var stopwatch = Stopwatch.StartNew();
            node.Repository.Refresh(_updateHandler, fetchRemotes: true, deepRefresh: true);
            stopwatch.Stop();
            node.Repository.RefreshTimeSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 1);
        });
    }

    public void CheckoutMatchingBranch(RepositoryNodeViewModel? node)
    {
        if (node?.IsGroupHeader != false)
        {
            return;
        }

        if (string.IsNullOrEmpty(node.MatchingBranchName))
        {
            return;
        }

        var branchName = node.MatchingBranchName;
        Task.Run(() =>
        {
            try
            {
                node.Repository.CheckoutBranch(branchName);
                _ui.Post(node.Update);
            }
            catch (Exception exception)
            {
                var message = $"Could not check out branch '{node.MatchingBranchName}': {exception.Message}";
                ShowAlert("Checkout Failed", message);
                _errorLog.PostError(_ui, message, node.WorkingDirectory);
            }
        });
    }

    /// <summary>
    /// The row's Clean button: runs a full-tier <see cref="Pruner"/> dry run for the repository off
    /// the UI thread (this includes the slow patch-id and squash tiers that refresh skips), asks
    /// for confirmation listing every would-delete branch and stash, then deletes exactly those,
    /// logs each restore command and refreshes the repository.
    /// </summary>
    public async Task CleanDownstreamBranchesAsync(RepositoryNodeViewModel? node)
    {
        if (node?.IsGroupHeader != false)
        {
            return;
        }

        var workingDirectory = node.WorkingDirectory;
        if (string.IsNullOrEmpty(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            await _ui.InvokeAsync(() => _dialogs.ShowAlertAsync("Error", "Invalid repository path"));
            return;
        }

        var dryRun = await Task.Run(() => Pruner.Run([workingDirectory], new PruneOptions(), TextWriter.Null));
        if (await ReportPruneErrorsAsync(dryRun, workingDirectory))
        {
            return;
        }

        var candidates = dryRun.Candidates.Where(candidate => candidate.Action == PruneCandidate.WouldDelete).ToList();
        var skippedCount = dryRun.Candidates.Count - candidates.Count;
        if (candidates.Count == 0)
        {
            await _ui.InvokeAsync(() => _dialogs.ShowAlertAsync("Nothing to Clean",
                $"No merged branches or stashes in {workingDirectory} ({skippedCount} skipped)."));
            return;
        }

        var confirmed = false;
        await _ui.InvokeAsync(async () => confirmed = await _dialogs.ConfirmAsync(
            "Clean Repository", DescribeCleanCandidates(workingDirectory, candidates, skippedCount), "Delete", "Cancel"));
        if (!confirmed)
        {
            return;
        }

        await ApplyCleanAsync(node, workingDirectory, candidates);
    }

    async Task ApplyCleanAsync(RepositoryNodeViewModel node, string workingDirectory, List<PruneCandidate> confirmed)
    {
        var options = new PruneOptions(Apply: true, OnlyShas: confirmed.Select(candidate => candidate.Sha).ToHashSet(StringComparer.Ordinal));
        using var restoreHints = new StringWriter();
        var applied = await Task.Run(() => Pruner.Run([workingDirectory], options, restoreHints));
        foreach (var hint in restoreHints.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            GitWizardLog.Log(hint);
        }

        await Task.Run(() => node.Repository.Refresh(_updateHandler));
        _ui.Post(node.Update);
        if (await ReportPruneErrorsAsync(applied, workingDirectory))
        {
            return;
        }

        var deleted = applied.Candidates.Count(candidate => candidate.Action == PruneCandidate.Deleted);
        var failed = applied.Candidates
            .Where(candidate => candidate.Action == PruneCandidate.Skipped && confirmed.Any(c => c.Sha == candidate.Sha))
            .Select(candidate => $"{candidate.Name} ({candidate.SkipReason})")
            .ToList();
        if (failed.Count > 0)
        {
            _errorLog.PostError(_ui, $"Could not delete: {string.Join(", ", failed)}", workingDirectory);
        }

        var summary = failed.Count == 0
            ? $"Deleted {deleted} item(s). Restore commands are in the log."
            : $"Deleted {deleted} item(s). Could not delete: {string.Join(", ", failed)}";
        await _ui.InvokeAsync(() => _dialogs.ShowAlertAsync("Done", summary));
    }

    async Task<bool> ReportPruneErrorsAsync(PruneReport report, string workingDirectory)
    {
        if (report.Errors.Count == 0)
        {
            return false;
        }

        var message = string.Join("\n", report.Errors.Select(error => error.Error));
        _errorLog.PostError(_ui, $"Could not clean: {message}", workingDirectory);
        await _ui.InvokeAsync(() => _dialogs.ShowAlertAsync("Clean Failed", message));
        return true;
    }

    internal static string DescribeCleanCandidates(string workingDirectory, List<PruneCandidate> candidates, int skippedCount)
    {
        var builder = new StringBuilder();
        builder.Append("Delete ").Append(candidates.Count).Append(" item(s) from ").Append(workingDirectory).AppendLine("?").AppendLine();
        foreach (var candidate in candidates)
        {
            builder.Append(candidate.Kind).Append(' ').Append(candidate.Name).Append(" (").Append(candidate.MergedBy).Append(')');
            if (!string.IsNullOrEmpty(candidate.Message))
            {
                builder.Append(" \"").Append(candidate.Message).Append('"');
            }

            builder.AppendLine();
        }

        if (skippedCount > 0)
        {
            builder.AppendLine().Append(skippedCount).Append(" other branch(es)/stash(es) skipped (not merged, checked out, or the base branch).");
        }

        return builder.ToString();
    }

    // Marshal alerts to the UI thread and record dialog failures without propagating them.
    void ShowAlert(string title, string message)
        => _ui.Post(() => _ = ShowAlertSafeAsync(title, message));

    async Task ShowAlertSafeAsync(string title, string message)
    {
        try
        {
            await _dialogs.ShowAlertAsync(title, message);
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, "Failed to display alert dialog.");
            _errorLog.PostError(_ui, $"Failed to display alert dialog: {exception.Message}");
        }
    }
}

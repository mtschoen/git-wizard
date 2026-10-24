using System.Diagnostics;
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

    public async Task CleanDownstreamBranchesAsync(RepositoryNodeViewModel? node)
    {
        if (node?.IsGroupHeader != false)
        {
            return;
        }

        var downstream = node.Repository.Branches?.Where(branch => branch.IsMerged).ToList();
        if (downstream == null || downstream.Count == 0)
        {
            return;
        }

        var branchNames = string.Join(", ", downstream.Select(branch => $"'{branch.Name}'"));
        var message = $"Delete {downstream.Count} downstream branch(es)?\n\n{branchNames}";

        await _ui.InvokeAsync(async () =>
        {
            await _dialogs.ShowAlertAsync(
                "Delete Downstream Branches",
                message + "\n\nClick OK to proceed with deletion.",
                "Delete");

            var workingDirectory = node.WorkingDirectory;
            if (string.IsNullOrEmpty(workingDirectory) || !Directory.Exists(workingDirectory))
            {
                await _dialogs.ShowAlertAsync("Error", "Invalid repository path");
                return;
            }

            var (success, failed) = await DeleteBranchesAsync(workingDirectory, downstream);
            await ReportDownstreamDeletionResultAsync(node, downstream, success, failed);
        });
    }

    static async Task<(bool Success, List<string> Failed)> DeleteBranchesAsync(string workingDirectory, List<BranchInfo> downstream)
    {
        var success = true;
        var failed = new List<string>();

        foreach (var branch in downstream)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = $"branch -d \"{branch.Name}\"",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            try
            {
                using var process = Process.Start(startInfo);
                if (process != null)
                {
                    using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    var timedOut = false;
                    try
                    {
                        await process.WaitForExitAsync(exitTimeout.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        timedOut = true;
                    }

                    if (timedOut)
                    {
                        process.Kill();
                        failed.Add(branch.Name);
                        success = false;
                    }
                    else if (process.ExitCode != 0)
                    {
                        var error = await process.StandardError.ReadToEndAsync();
                        if (!string.IsNullOrEmpty(error) && !error.Contains("not fully merged"))
                        {
                            GitWizardLog.Log($"git branch -d {branch.Name}: {error}", GitWizardLog.LogType.Warning);
                        }
                        if (process.ExitCode != 0)
                        {
                            failed.Add(branch.Name);
                            success = false;
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                GitWizardLog.LogException(exception, $"Exception deleting branch {branch.Name} in {workingDirectory}");
                failed.Add(branch.Name);
                success = false;
            }
        }

        return (success, failed);
    }

    async Task ReportDownstreamDeletionResultAsync(RepositoryNodeViewModel node, List<BranchInfo> downstream,
        bool success, List<string> failed)
    {
        if (success)
        {
            var deletedCount = downstream.Count;
            node.Repository.Branches?.RemoveAll(branch => branch.IsMerged);
            node.UpdateDisplayText();
            await _dialogs.ShowAlertAsync("Done", $"Deleted {deletedCount} branch(es)");
        }
        else if (failed.Count > 0)
        {
            // Remove successfully deleted branches from Branches so UI doesn't show stale data
            node.Repository.Branches?.RemoveAll(branch => branch.IsMerged && !failed.Contains(branch.Name));
            var failedList = string.Join(", ", failed);
            _errorLog.PostError(_ui, $"Could not delete downstream branch(es): {failedList}", node.WorkingDirectory);
            await _dialogs.ShowAlertAsync(
                "Partial Success",
                $"Could not delete: {failedList}\n\nThese branches may not be fully merged or are protected.");
        }
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

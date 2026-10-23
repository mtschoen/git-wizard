using System.Diagnostics;
using GitWizard;

namespace GitWizardUI.ViewModels;

public partial class MainViewModel
{
    public void OpenInExplorer(RepositoryNodeViewModel? node)
    {
        if (node == null)
        {
            return;
        }

        if (node.IsGroupHeader)
        {
            RepositoryList.ToggleGroupExpand(node);
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
        catch (Exception ex)
        {
            ShowAlert("Error", $"Could not open folder: {ex.Message}");
            LogError($"Could not open folder: {ex.Message}", node.WorkingDirectory);
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
        catch (Exception ex)
        {
            ShowAlert("Error", $"Could not launch Fork: {ex.Message}");
            LogError($"Could not launch Fork: {ex.Message}", node.WorkingDirectory);
        }
    }

    // How long the per-row "✓ Copied" indicator stays lit after a copy before it clears.
    const int CopiedIndicatorMilliseconds = 1500;

    /// <summary>
    /// Copies the node's working directory to the clipboard, then lights the row's transient "copied"
    /// indicator for <see cref="CopiedIndicatorMilliseconds"/> ms before clearing it (this replaced
    /// the old modal "Copied" alert). A clipboard failure propagates to the caller (the code-behind
    /// fires and forgets); on failure the indicator is never lit because the awaited write throws
    /// before it is set. Public so the behavior is awaitable in tests. The indicator flag lives on
    /// the VM node, so it is set/reset on the UI thread via <c>_ui.Post</c>.
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
            node.Repository.Refresh(this, fetchRemotes: true, deepRefresh: true);
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
            catch (Exception ex)
            {
                var message = $"Could not check out branch '{node.MatchingBranchName}': {ex.Message}";
                ShowAlert("Checkout Failed", message);
                LogError(message, node.WorkingDirectory);
            }
        });
    }

    public async Task CleanDownstreamBranchesAsync(RepositoryNodeViewModel? node)
    {
        if (node?.IsGroupHeader != false)
        {
            return;
        }

        var downstream = node.Repository.Branches?.Where(b => b.IsMerged).ToList();
        if (downstream == null || downstream.Count == 0)
        {
            return;
        }

        var branchNames = string.Join(", ", downstream.Select(b => $"'{b.Name}'"));
        var message = $"Delete {downstream.Count} downstream branch(es)?\n\n{branchNames}";

        await _ui.InvokeAsync(async () =>
        {
            await _dialogs.ShowAlertAsync(
                "Delete Downstream Branches",
                message + "\n\nClick OK to proceed with deletion.",
                "Delete");

            var workingDir = node.WorkingDirectory;
            if (string.IsNullOrEmpty(workingDir) || !Directory.Exists(workingDir))
            {
                await _dialogs.ShowAlertAsync("Error", "Invalid repository path");
                return;
            }

            var (success, failed) = await DeleteBranchesAsync(workingDir, downstream);
            await ReportDownstreamDeletionResultAsync(node, downstream, success, failed);
        });
    }

    static async Task<(bool Success, List<string> Failed)> DeleteBranchesAsync(string workingDir, List<BranchInfo> downstream)
    {
        var success = true;
        var failed = new List<string>();

        foreach (var branch in downstream)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = $"branch -d \"{branch.Name}\"",
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            try
            {
                using var process = Process.Start(psi);
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
            catch (Exception ex)
            {
                GitWizardLog.LogException(ex, $"Exception deleting branch {branch.Name} in {workingDir}");
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
            node.Repository.Branches?.RemoveAll(b => b.IsMerged);
            node.UpdateDisplayText();
            await _dialogs.ShowAlertAsync("Done", $"Deleted {deletedCount} branch(es)");
        }
        else if (failed.Count > 0)
        {
            // Remove successfully deleted branches from Branches so UI doesn't show stale data
            node.Repository.Branches?.RemoveAll(b => b.IsMerged && !failed.Contains(b.Name));
            var failedList = string.Join(", ", failed);
            LogError($"Could not delete downstream branch(es): {failedList}", node.WorkingDirectory);
            await _dialogs.ShowAlertAsync(
                "Partial Success",
                $"Could not delete: {failedList}\n\nThese branches may not be fully merged or are protected.");
        }
    }

    // Drains the UI command queue in one pass. Every RepositoryCreated/WorktreeCreated command in
    // the drained slice is collected into a single batch and applied with one AddRepositoriesBatch
    // call; every other command is staged and processed afterwards, in arrival order. A refresh
    // worker enqueues RepositoryCreated, refreshes, then enqueues RefreshCompleted, so with
    // parallel workers the queue alternates create/complete after the initial burst. Flushing the
    // pending batch at each interleaved completion (the earlier drain) sent each resulting one-row
    // batch back through AddRepository's per-row insertion, which shifts up to the whole displayed
    // list on every call: O(n^2) element movement across n interleaved arrivals (issue #245,
    // round-3 pr-crew review of PR #241/#243). Coalescing across the whole slice ensures each drain
    // merges its creates in a single sorted pass rather than repeated flushes. Processing the
    // staged commands after the batch is safe for ordering: a repository's create command is always
    // enqueued before its own completion, so its row exists by the time the completion is applied.
    // Internal so tests can drive the real queue/drain interaction synchronously.
    internal void DrainUICommands()
    {
        List<GitWizardRepository>? pendingRepositoryBatch = null;
        List<RepositoryUICommand>? stagedCommands = null;

        while (_uiCommands.TryDequeue(out var command))
        {
            if (command is { Type: RepositoryUICommandType.RepositoryCreated or RepositoryUICommandType.WorktreeCreated, Repository: not null })
            {
                pendingRepositoryBatch ??= new List<GitWizardRepository>();
                pendingRepositoryBatch.Add(command.Repository);
                continue;
            }

            stagedCommands ??= new List<RepositoryUICommand>();
            stagedCommands.Add(command);
        }

        if (pendingRepositoryBatch != null)
        {
            AddRepositoriesBatch(pendingRepositoryBatch);
        }

        if (stagedCommands == null)
        {
            return;
        }

        foreach (var command in stagedCommands)
        {
            ProcessUICommand(command);
        }
    }

    void ProcessUICommand(RepositoryUICommand command)
    {
        switch (command.Type)
        {
            case RepositoryUICommandType.RepositoryCreated:
                if (command.Repository != null)
                {
                    AddRepository(command.Repository);
                }

                break;
            case RepositoryUICommandType.SubmoduleCreated:
                if (command.ParentRepository != null && command.Repository != null)
                {
                    AddSubmodule(command.ParentRepository, command.Repository);
                }

                break;
            case RepositoryUICommandType.WorktreeCreated:
                if (command.Repository != null)
                {
                    AddRepository(command.Repository);
                }

                break;
            case RepositoryUICommandType.UninitializedSubmoduleCreated:
                if (command.ParentRepository != null && command.SubmodulePath != null)
                {
                    AddUninitializedSubmodule(command.ParentRepository, command.SubmodulePath);
                }

                break;
            case RepositoryUICommandType.RefreshCompleted:
                if (command.Repository != null)
                {
                    UpdateCompletedRepository(command.Repository);
                }

                break;
        }
    }

}

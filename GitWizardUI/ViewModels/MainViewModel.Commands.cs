using System.Diagnostics;
using GitWizard;

namespace GitWizardUI.ViewModels;

public partial class MainViewModel
{
    void OpenInExplorer(RepositoryNodeViewModel? node)
    {
        if (node == null)
            return;

        if (node.IsGroupHeader)
        {
            ToggleGroupExpand(node);
            return;
        }

        if (string.IsNullOrEmpty(node.WorkingDirectory))
            return;

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
        }
    }

    void OpenInFork(RepositoryNodeViewModel? node)
    {
        if (node == null || string.IsNullOrEmpty(node.WorkingDirectory))
            return;

        if (!Directory.Exists(node.WorkingDirectory))
        {
            ShowAlert("Error", "Invalid repository path");
            return;
        }

        var configuration = GitWizardConfiguration.GetGlobalConfiguration();
        string? forkPath = configuration.ForkPath;

        if (string.IsNullOrWhiteSpace(forkPath))
        {
            forkPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Fork", "Fork.exe");
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
        }
    }

    // How long the per-row "✓ Copied" indicator stays lit after a copy before it clears.
    const int CopiedIndicatorMilliseconds = 1500;

    /// <summary>
    /// Copies the node's working directory to the clipboard, then lights the row's transient "copied"
    /// indicator for <see cref="CopiedIndicatorMilliseconds"/> ms before clearing it (this replaced
    /// the old modal "Copied" alert). Wired as the target of an <see cref="AsyncRelayCommand{T}"/>, so
    /// a clipboard failure is logged by that wrapper rather than going unobserved; on failure the
    /// indicator is never lit because the awaited write throws before it is set. Public so the
    /// behavior is awaitable in tests. The indicator flag lives on the VM node, so it is set/reset on
    /// the UI thread via <c>_ui.Post</c>.
    /// </summary>
    public async Task CopyToClipboardAsync(RepositoryNodeViewModel node)
    {
        if (string.IsNullOrEmpty(node.WorkingDirectory))
            return;

        await _clipboard.SetPlainTextAsync(node.WorkingDirectory).ConfigureAwait(false);
        _ui.Post(() => node.JustCopied = true);
        await Task.Delay(CopiedIndicatorMilliseconds).ConfigureAwait(false);
        _ui.Post(() => node.JustCopied = false);
    }

    void DeepRefreshRepository(RepositoryNodeViewModel? node)
    {
        if (node == null || node.IsGroupHeader)
            return;

        node.Status = RefreshStatus.Refreshing;
        Task.Run(() =>
        {
            var stopwatch = Stopwatch.StartNew();
            node.Repository.Refresh(this, fetchRemotes: true, deepRefresh: true);
            stopwatch.Stop();
            node.Repository.RefreshTimeSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 1);
        });
    }

    void CheckoutMatchingBranch(RepositoryNodeViewModel? node)
    {
        if (node == null || node.IsGroupHeader)
            return;

        if (string.IsNullOrEmpty(node.MatchingBranchName))
            return;

        var branchName = node.MatchingBranchName;
        Task.Run(() =>
        {
            try
            {
                node.Repository.CheckoutBranch(branchName);
                _ui.Post(() => node.Update());
            }
            catch (Exception ex)
            {
                ShowAlert("Checkout Failed", $"Could not check out branch '{node.MatchingBranchName}': {ex.Message}");
            }
        });
    }

    async Task CleanDownstreamBranchesAsync(RepositoryNodeViewModel? node)
    {
        if (node == null || node.IsGroupHeader)
            return;

        var downstream = node.Repository.Branches?.Where(b => b.IsMerged).ToList();
        if (downstream == null || downstream.Count == 0)
            return;

        var branchNames = string.Join(", ", downstream.Select(b => $"'{b.Name}'"));
        var message = $"Delete {downstream.Count} downstream branch(es)?\n\n{branchNames}";

        await _ui.InvokeAsync(async () =>
        {
            await _dialogs.DisplayAlertAsync(
                "Delete Downstream Branches",
                message + "\n\nClick OK to proceed with deletion.",
                "Delete");

            var workingDir = node.WorkingDirectory;
            if (string.IsNullOrEmpty(workingDir) || !Directory.Exists(workingDir))
            {
                await _dialogs.DisplayAlertAsync("Error", "Invalid repository path");
                return;
            }

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
                        if (!process.WaitForExit(15000))
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

            if (success)
            {
                var deletedCount = downstream.Count;
                node.Repository.Branches?.RemoveAll(b => b.IsMerged);
                node.UpdateDisplayText();
                await _dialogs.DisplayAlertAsync("Done", $"Deleted {deletedCount} branch(es)");
            }
            else if (failed.Count > 0)
            {
                // Remove successfully deleted branches from Branches so UI doesn't show stale data
                node.Repository.Branches?.RemoveAll(b => b.IsMerged && !failed.Contains(b.Name));
                var failedList = string.Join(", ", failed);
                await _dialogs.DisplayAlertAsync(
                    "Partial Success",
                    $"Could not delete: {failedList}\n\nThese branches may not be fully merged or are protected.");
            }
        });
    }

    void ToggleGroupExpand(RepositoryNodeViewModel? node)
    {
        if (node == null || !node.IsGroupHeader)
            return;

        var index = Repositories.IndexOf(node);
        if (index < 0)
            return;

        if (node.IsExpanded)
        {
            // Collapse: remove children after the header
            node.IsExpanded = false;
            while (index + 1 < Repositories.Count && !Repositories[index + 1].IsGroupHeader)
            {
                Repositories.RemoveAt(index + 1);
            }
        }
        else
        {
            // Expand: insert children after the header
            node.IsExpanded = true;
            var insertIndex = index + 1;
            foreach (var child in node.Children)
            {
                Repositories.Insert(insertIndex++, child);
            }
        }

        node.UpdateDisplayText();
        ScrollToRequest?.Invoke(node);
    }

    void StartUIUpdateThread()
    {
        // Daemon loop for the app's lifetime: drains the UI command queue every 250ms and pushes
        // progress updates onto the UI thread. Never returns by design - it ends with the process.
        // ReSharper disable once FunctionNeverReturns
        Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(250);

                // Drain all pending commands in one UI dispatch to minimize layout passes
                if (_uiCommands.TryPeek(out _))
                {
                    await _ui.InvokeAsync(() =>
                    {
                        while (_uiCommands.TryDequeue(out var command))
                        {
                            ProcessUICommand(command);
                        }
                    });
                }

                await _ui.InvokeAsync(() =>
                {
                    if (_progressCount.HasValue && _progressTotal.HasValue && _progressTotal.Value > 0)
                    {
                        ProgressValue = (double)_progressCount / _progressTotal.Value;
                        ProgressText = $"{_progressDescription} {_progressCount} / {_progressTotal}";
                        IsProgressVisible = true;

                        if (_progressTotal.Value == _progressCount)
                        {
                            IsProgressVisible = false;
                        }
                    }
                });
            }
        });
    }

    void ProcessUICommand(RepositoryUICommand command)
    {
        switch (command.Type)
        {
            case RepositoryUICommandType.RepositoryCreated:
                if (command.Repository != null)
                    AddRepository(command.Repository);
                break;
            case RepositoryUICommandType.SubmoduleCreated:
                if (command.ParentRepository != null && command.Repository != null)
                    AddSubmodule(command.ParentRepository, command.Repository);
                break;
            case RepositoryUICommandType.WorktreeCreated:
                if (command.Repository != null)
                    AddRepository(command.Repository);
                break;
            case RepositoryUICommandType.UninitializedSubmoduleCreated:
                if (command.ParentRepository != null && command.SubmodulePath != null)
                    AddUninitializedSubmodule(command.ParentRepository, command.SubmodulePath);
                break;
            case RepositoryUICommandType.RefreshCompleted:
                if (command.Repository != null)
                    UpdateCompletedRepository(command.Repository);
                break;
        }
    }

}

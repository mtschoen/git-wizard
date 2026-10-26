namespace GitWizard.CLI;

sealed class UpdateHandler : IUpdateHandler
{
    readonly TextWriter _stderr;
    readonly object _progressLock = new();
    readonly Lock _stateLock = new();
    readonly HashSet<string> _createdPaths = new(StringComparer.Ordinal);
    readonly List<string> _warnings = [];
    int _progressTotal;
    string? _progressDescription;
    int _created;
    int _refreshed;
    int _skipped;

    public UpdateHandler(TextWriter? stderr = null)
    {
        _stderr = stderr ?? Console.Error;
    }

    internal ReportRunSummary CaptureRun()
    {
        lock (_stateLock)
        {
            return new ReportRunSummary
            {
                Created = _created,
                Refreshed = _refreshed,
                Skipped = _skipped,
                Warnings = [.. _warnings]
            };
        }
    }

    internal void RecordWarning(string message)
    {
        lock (_stateLock)
        {
            _warnings.Add(message);
        }
    }

    void RecordCreated(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        _createdPaths.Add(path);
        _created++;
    }

    bool AcceptParent(string? parentPath, string kind)
    {
        if (!string.IsNullOrEmpty(parentPath) && _createdPaths.Contains(parentPath))
        {
            return true;
        }

        _skipped++;
        _warnings.Add(string.IsNullOrEmpty(parentPath)
            ? $"[SKIPPED] {kind} parent has null path"
            : $"[SKIPPED] {kind} parent not created yet: {parentPath}");
        return false;
    }

    public void SendUpdateMessage(string? message)
    {
        if (message == null)
        {
            GitWizardLog.LogException(new ArgumentException("Tried to log a null message", nameof(message)));
            return;
        }

        GitWizardLog.Log(message, GitWizardLog.LogType.Verbose);
    }

    public void OnRepositoryCreated(GitWizardRepository gitWizardRepository)
    {
        lock (_stateLock)
        {
            RecordCreated(gitWizardRepository.WorkingDirectory);
        }

        GitWizardLog.Log($"[CREATED] {gitWizardRepository.WorkingDirectory}", GitWizardLog.LogType.Verbose);
    }

    public void StartProgress(string description, int total)
    {
        lock (_progressLock)
        {
            _progressDescription = description;
            _progressTotal = total;
            PrintProgress(0, total);
        }
    }

    public void UpdateProgress(int count)
    {
        lock (_progressLock)
        {
            PrintProgress(count, _progressTotal);
        }
    }

    void PrintProgress(int count, int total)
    {
        if (total <= 0)
        {
            return;
        }

        var fraction = (double)count / total;
        const int barWidth = 20;
        var filled = (int)Math.Round(fraction * barWidth);
        if (filled > barWidth)
        {
            filled = barWidth;
        }

        if (filled < 0)
        {
            filled = 0;
        }

        var bar = new string('█', filled) + new string('░', barWidth - filled);
        var percent = (int)Math.Round(fraction * 100);
        var progressLine = $"\r  [{bar}] {percent,3}%  {_progressDescription}  {count}/{total}  ";
        _stderr.Write(progressLine);

        if (count >= total)
        {
            _stderr.WriteLine();
        }
    }

    public void OnSubmoduleCreated(GitWizardRepository parent, GitWizardRepository submodule)
    {
        lock (_stateLock)
        {
            if (AcceptParent(parent.WorkingDirectory, "Submodule"))
            {
                RecordCreated(submodule.WorkingDirectory);
            }
        }
        GitWizardLog.Log($"[SUBMODULE] {submodule.WorkingDirectory} (Parent={parent.WorkingDirectory})",
            GitWizardLog.LogType.Verbose);
    }

    public void OnWorktreeCreated(GitWizardRepository worktree)
    {
        lock (_stateLock)
        {
            RecordCreated(worktree.WorkingDirectory);
        }

        GitWizardLog.Log($"[WORKTREE] {worktree.WorkingDirectory}", GitWizardLog.LogType.Verbose);
    }

    public void OnUninitializedSubmoduleCreated(GitWizardRepository parent, string submodulePath)
    {
        lock (_stateLock)
        {
            AcceptParent(parent.WorkingDirectory, "Uninitialized submodule");
        }

        GitWizardLog.Log($"[UNINITIALIZED SUBMODULE] {submodulePath} (Parent={parent.WorkingDirectory})",
            GitWizardLog.LogType.Verbose);
    }

    public void OnRepositoryRefreshCompleted(GitWizardRepository gitWizardRepository)
    {
        lock (_stateLock)
        {
            var path = gitWizardRepository.WorkingDirectory;
            if (string.IsNullOrEmpty(path) || !_createdPaths.Contains(path))
            {
                _skipped++;
                _warnings.Add($"[MISSING] Refresh completed but item not created: {path} (IsRefreshing={gitWizardRepository.IsRefreshing})");
            }
            else
            {
                _refreshed++;
            }
        }
        GitWizardLog.Log($"[COMPLETED] {gitWizardRepository.WorkingDirectory} (IsRefreshing={gitWizardRepository.IsRefreshing})",
            GitWizardLog.LogType.Verbose);
    }
}

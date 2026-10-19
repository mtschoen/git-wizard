namespace GitWizard.CLI;

sealed class UpdateHandler(TextWriter? stderr = null) : IUpdateHandler
{
    readonly TextWriter _stderr = stderr ?? Console.Error;
    readonly object _progressLock = new();
    int _progressTotal;
    string? _progressDescription;

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
            return;

        double fraction = (double)count / total;
        int barWidth = 20;
        int filled = (int)Math.Round(fraction * barWidth);
        if (filled > barWidth) filled = barWidth;
        if (filled < 0) filled = 0;

        var bar = new string('█', filled) + new string('░', barWidth - filled);
        int percent = (int)Math.Round(fraction * 100);
        string progressLine = $"\r  [{bar}] {percent,3}%  {_progressDescription}  {count}/{total}  ";
        _stderr.Write(progressLine);

        if (count >= total)
            _stderr.WriteLine();
    }

    public void OnSubmoduleCreated(GitWizardRepository parent, GitWizardRepository submodule)
    {
        GitWizardLog.Log($"[SUBMODULE] {submodule.WorkingDirectory} (Parent={parent.WorkingDirectory})",
            GitWizardLog.LogType.Verbose);
    }

    public void OnWorktreeCreated(GitWizardRepository worktree)
    {
        GitWizardLog.Log($"[WORKTREE] {worktree.WorkingDirectory}", GitWizardLog.LogType.Verbose);
    }

    public void OnUninitializedSubmoduleCreated(GitWizardRepository parent, string submodulePath)
    {
        GitWizardLog.Log($"[UNINITIALIZED SUBMODULE] {submodulePath} (Parent={parent.WorkingDirectory})",
            GitWizardLog.LogType.Verbose);
    }

    public void OnRepositoryRefreshCompleted(GitWizardRepository gitWizardRepository)
    {
        GitWizardLog.Log($"[COMPLETED] {gitWizardRepository.WorkingDirectory} (IsRefreshing={gitWizardRepository.IsRefreshing})",
            GitWizardLog.LogType.Verbose);
    }
}

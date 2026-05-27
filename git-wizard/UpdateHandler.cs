using System.Collections.Concurrent;

namespace GitWizard.CLI;

class UpdateHandler : IUpdateHandler
{
    enum CommandType
    {
        RepositoryCreated,
        SubmoduleCreated,
        WorktreeCreated,
        UninitializedSubmoduleCreated,
        RefreshCompleted
    }

    struct Command
    {
        public CommandType Type;
        public GitWizardRepository? Repository;
        public GitWizardRepository? ParentRepository;
    }

    readonly ConcurrentQueue<Command> _commands = new();
    readonly HashSet<string> _createdPaths = new();
    int _totalCreated;
    int _totalCompleted;
    int _skippedCommands;

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
        _commands.Enqueue(new Command
        {
            Type = CommandType.RepositoryCreated,
            Repository = gitWizardRepository
        });
    }

    int _progressTotal;
    string? _progressDescription;
    readonly object _progressLock = new();

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
        Console.Write(progressLine);

        // Print a newline when progress reaches 100%
        if (count >= total)
        {
            Console.WriteLine();
        }
    }

    public void OnSubmoduleCreated(GitWizardRepository parent, GitWizardRepository submodule)
    {
        _commands.Enqueue(new Command
        {
            Type = CommandType.SubmoduleCreated,
            ParentRepository = parent,
            Repository = submodule
        });
    }

    public void OnWorktreeCreated(GitWizardRepository worktree)
    {
        _commands.Enqueue(new Command
        {
            Type = CommandType.WorktreeCreated,
            Repository = worktree
        });
    }

    public void OnUninitializedSubmoduleCreated(GitWizardRepository parent, string submodulePath)
    {
        _commands.Enqueue(new Command
        {
            Type = CommandType.UninitializedSubmoduleCreated,
            ParentRepository = parent
        });
    }

    public void OnRepositoryRefreshCompleted(GitWizardRepository gitWizardRepository)
    {
        _commands.Enqueue(new Command
        {
            Type = CommandType.RefreshCompleted,
            Repository = gitWizardRepository
        });
    }

    public void ProcessCommands()
    {
        while (_commands.TryDequeue(out var command))
        {
            ProcessCommand(command);
        }
    }

    void ProcessCommand(Command command)
    {
        switch (command.Type)
        {
            case CommandType.RepositoryCreated:
                if (command.Repository != null)
                {
                    var path = command.Repository.WorkingDirectory;
                    if (!string.IsNullOrEmpty(path))
                    {
                        _createdPaths.Add(path);
                        _totalCreated++;
                        GitWizardLog.Log($"[CREATED] {path}", GitWizardLog.LogType.Verbose);
                    }
                }
                break;

            case CommandType.SubmoduleCreated:
                if (command.ParentRepository != null && command.Repository != null)
                {
                    var parentPath = command.ParentRepository.WorkingDirectory;
                    var submodulePath = command.Repository.WorkingDirectory;

                    if (string.IsNullOrEmpty(parentPath))
                    {
                        _skippedCommands++;
                        GitWizardLog.Log($"[SKIPPED] Submodule parent has null path");
                        return;
                    }

                    if (!_createdPaths.Contains(parentPath))
                    {
                        _skippedCommands++;
                        GitWizardLog.Log($"[SKIPPED] Submodule parent not created yet: {parentPath}");
                        return;
                    }

                    if (!string.IsNullOrEmpty(submodulePath))
                    {
                        _createdPaths.Add(submodulePath);
                        _totalCreated++;
                    }
                }
                break;

            case CommandType.WorktreeCreated:
                if (command.Repository != null)
                {
                    var path = command.Repository.WorkingDirectory;
                    if (!string.IsNullOrEmpty(path))
                    {
                        _createdPaths.Add(path);
                        _totalCreated++;
                    }
                }
                break;

            case CommandType.UninitializedSubmoduleCreated:
                if (command.ParentRepository != null)
                {
                    var parentPath = command.ParentRepository.WorkingDirectory;
                    if (string.IsNullOrEmpty(parentPath))
                    {
                        _skippedCommands++;
                        return;
                    }

                    if (!_createdPaths.Contains(parentPath))
                    {
                        _skippedCommands++;
                        GitWizardLog.Log($"[SKIPPED] Uninitialized submodule parent not created yet: {parentPath}");
                    }
                }
                break;

            case CommandType.RefreshCompleted:
                if (command.Repository != null)
                {
                    var path = command.Repository.WorkingDirectory;
                    if (string.IsNullOrEmpty(path))
                    {
                        _skippedCommands++;
                        return;
                    }

                    if (!_createdPaths.Contains(path))
                    {
                        _skippedCommands++;
                        GitWizardLog.Log($"[MISSING] Refresh completed but item not created: {path} (IsRefreshing={command.Repository.IsRefreshing})");
                        return;
                    }

                    _totalCompleted++;
                    GitWizardLog.Log($"[COMPLETED] {path} (IsRefreshing={command.Repository.IsRefreshing})", GitWizardLog.LogType.Verbose);
                }
                break;
        }
    }

    public void PrintSummary()
    {
        Console.WriteLine();
        Console.WriteLine($"\n=== Command Processing Summary ===");
        Console.WriteLine($"Total repositories created: {_totalCreated}");
        Console.WriteLine($"Total refresh completed: {_totalCompleted}");
        Console.WriteLine($"Skipped commands: {_skippedCommands}");
        Console.WriteLine($"Missing items (refresh completed before created): {_totalCompleted > _totalCreated}");
    }
}
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
        public string? SubmodulePath;
    }

    readonly ConcurrentQueue<Command> _commands = new();
    readonly HashSet<string> _createdPaths = new();
    int _totalCreated = 0;
    int _totalCompleted = 0;
    int _skippedCommands = 0;

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

    public void StartProgress(string description, int total)
    {
        // TODO: Persistent progress bar
    }

    public void UpdateProgress(int count)
    {
        // TODO: Persistent progress bar
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
            ParentRepository = parent,
            SubmodulePath = submodulePath
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
                        GitWizardLog.Log($"[SKIPPED] Submodule parent has null path", GitWizardLog.LogType.Info);
                        return;
                    }

                    if (!_createdPaths.Contains(parentPath))
                    {
                        _skippedCommands++;
                        GitWizardLog.Log($"[SKIPPED] Submodule parent not created yet: {parentPath}", GitWizardLog.LogType.Info);
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
                        GitWizardLog.Log($"[SKIPPED] Uninitialized submodule parent not created yet: {parentPath}", GitWizardLog.LogType.Info);
                        return;
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
                        GitWizardLog.Log($"[MISSING] Refresh completed but item not created: {path} (IsRefreshing={command.Repository.IsRefreshing})", GitWizardLog.LogType.Info);
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
        Console.WriteLine($"\n=== Command Processing Summary ===");
        Console.WriteLine($"Total repositories created: {_totalCreated}");
        Console.WriteLine($"Total refresh completed: {_totalCompleted}");
        Console.WriteLine($"Skipped commands: {_skippedCommands}");
        Console.WriteLine($"Missing items (refresh completed before created): {_totalCompleted > _totalCreated}");
    }
}
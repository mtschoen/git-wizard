using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Serialization;
using LibGit2Sharp;

namespace GitWizard;

[JsonConverter(typeof(JsonStringEnumConverter<RepositoryIndexRefreshReason>))]
public enum RepositoryIndexRefreshReason
{
    DeadIndexLock = 1,
    StaleIndex = 2
}

public sealed class RepositoryIndexRefreshService(TimeProvider? timeProvider = null)
{
    public static readonly TimeSpan DeadLockAge = TimeSpan.FromMinutes(10);
    public const double SlowStatusSeconds = 1;

    readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    internal Func<bool>? GitProcessRunning { get; init; }
    internal Func<string, bool>? CanOpenLockExclusively { get; init; }
    internal Func<Repository, bool>? StaleStatEvidence { get; init; }

    internal RepositoryIndexRefreshReason? Detect(Repository repository, double statusTimeSeconds)
    {
        if (repository.Info.IsBare)
        {
            return null;
        }

        // Info.Path resolves gitdir files and the private git directory of a linked worktree.
        var lockPath = Path.Combine(repository.Info.Path, "index.lock");
        if (IsOlderThan(lockPath, DeadLockAge))
        {
            return RepositoryIndexRefreshReason.DeadIndexLock;
        }

        if (statusTimeSeconds > SlowStatusSeconds &&
            (StaleStatEvidence ?? RepositoryIndexStatEvidence.HasStaleEntries)(repository))
        {
            return RepositoryIndexRefreshReason.StaleIndex;
        }

        return null;
    }

    bool IsOlderThan(string path, TimeSpan age) =>
        File.Exists(path) && _timeProvider.GetUtcNow() - File.GetLastWriteTimeUtc(path) > age;

    public void Refresh(GitWizardRepository repository) => repository.RefreshIndex(this);

    internal void RefreshLocked(GitWizardRepository repository)
    {
        using (var library = new Repository(repository.WorkingDirectory))
        {
            var lockPath = Path.Combine(library.Info.Path, "index.lock");
            if (File.Exists(lockPath))
            {
                if (!IsOlderThan(lockPath, DeadLockAge))
                {
                    throw new InvalidOperationException("The index lock is 10 minutes old or newer. Try again after Git finishes.");
                }

                // Git holds a pathname lock, not a portable advisory file lock. Any git
                // process is a conservative veto, including a writer that closed its handle.
                if ((GitProcessRunning ?? HasGitProcess)() ||
                    !(CanOpenLockExclusively ?? CanOpenLock)(lockPath))
                {
                    throw new InvalidOperationException(
                        "The index lock was preserved because a git process may be using this repository. Try again after Git finishes.");
                }

                File.Delete(lockPath);
            }
            else if (repository.IndexRefreshReason == null && Detect(library, repository.StatusTimeSeconds) == null)
            {
                repository.Refresh();
                return;
            }

            if (!GitCli.RunIndexRefresh(library).Succeeded)
            {
                throw new InvalidOperationException("Git could not refresh the index. See the error log for details.");
            }
        }

        repository.Refresh();
    }

    static bool HasGitProcess()
    {
        try
        {
            var processes = Process.GetProcessesByName("git");
            foreach (var process in processes)
            {
                process.Dispose();
            }
            return processes.Length != 0;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            // Inability to inspect processes is not evidence that the writer is gone.
            return true;
        }
    }

    static bool CanOpenLock(string path)
    {
        try
        {
            // Unix sharing is advisory; the process veto is required on every platform.
            using var probe = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

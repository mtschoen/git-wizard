namespace GitWizard;

static class GiantPackRefreshGate
{
    const long GiantPackIndexThresholdBytes = 100L * 1024 * 1024;
    static readonly SemaphoreSlim PackRefreshSemaphore = new(1, 1);

    internal static IDisposable? Acquire(string? workingDirectory, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(workingDirectory) || !HasGiantPackIndex(workingDirectory))
        {
            return null;
        }

        PackRefreshSemaphore.Wait(cancellationToken);
        return new Releaser(PackRefreshSemaphore);
    }

    static bool HasGiantPackIndex(string workingDirectory)
    {
        var gitDirectory = ResolveGitDirectory(workingDirectory);
        if (gitDirectory == null)
        {
            return false;
        }

        var commonDirectoryFile = Path.Combine(gitDirectory, "commondir");
        if (File.Exists(commonDirectoryFile))
        {
            var commonDirectory = File.ReadAllText(commonDirectoryFile).Trim();
            gitDirectory = Path.GetFullPath(Path.Combine(gitDirectory, commonDirectory));
        }

        var packDirectory = Path.Combine(gitDirectory, "objects", "pack");
        if (!Directory.Exists(packDirectory))
        {
            return false;
        }

        foreach (var path in Directory.EnumerateFiles(packDirectory, "*.idx"))
        {
            if (new FileInfo(path).Length > GiantPackIndexThresholdBytes)
            {
                return true;
            }
        }

        return false;
    }

    static string? ResolveGitDirectory(string workingDirectory)
    {
        var dotGitPath = Path.Combine(workingDirectory, ".git");
        if (Directory.Exists(dotGitPath))
        {
            return dotGitPath;
        }

        if (!File.Exists(dotGitPath))
        {
            return workingDirectory;
        }

        const string prefix = "gitdir:";
        var contents = File.ReadAllText(dotGitPath).Trim();
        if (!contents.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Path.GetFullPath(Path.Combine(workingDirectory, contents[prefix.Length..].Trim()));
    }

    sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        public void Dispose() => semaphore.Release();
    }
}

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
        var packDirectory = Path.Combine(workingDirectory, ".git", "objects", "pack");
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

    sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        public void Dispose() => semaphore.Release();
    }
}

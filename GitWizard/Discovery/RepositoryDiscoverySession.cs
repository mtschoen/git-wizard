namespace GitWizard.Discovery;

sealed class RepositoryDiscoverySession(
    MftIndexSession? indexSession,
    Task<IReadOnlyDictionary<string, long>>? repositorySizes = null,
    CancellationTokenSource? repositorySizeCancellation = null)
{
    MftIndexSession? _indexSession = indexSession;
    CancellationTokenSource? _repositorySizeCancellation = repositorySizeCancellation;

    public MftIndexSession? IndexSession => _indexSession;

    public Task<IReadOnlyDictionary<string, long>>? RepositorySizes { get; } = repositorySizes;

    public async Task WaitForRepositorySizesAsync()
    {
        if (RepositorySizes is null)
        {
            return;
        }

        try
        {
            await RepositorySizes.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            GitWizardLog.Log("Repository size calculation was cancelled.", GitWizardLog.LogType.Debug);
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, "Exception calculating repository sizes from the discovery index.");
        }
    }

    public MftIndexSession? DetachIndexSession()
    {
        var cancellation = Interlocked.Exchange(ref _repositorySizeCancellation, null);
        // Detachment transfers a live index, so the size pass must finish before the session changes owner.
        WaitForRepositorySizesAsync().GetAwaiter().GetResult();
        cancellation?.Dispose();
        return Interlocked.Exchange(ref _indexSession, null);
    }

    public async ValueTask DisposeAsync()
    {
        var cancellation = Interlocked.Exchange(ref _repositorySizeCancellation, null);
        if (cancellation is not null)
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
        }

        await WaitForRepositorySizesAsync().ConfigureAwait(false);
        cancellation?.Dispose();
        var session = Interlocked.Exchange(ref _indexSession, null);
        if (session is not null)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }
}

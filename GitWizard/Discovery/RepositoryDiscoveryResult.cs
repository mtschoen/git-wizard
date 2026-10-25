namespace GitWizard.Discovery;

/// <summary>
/// Owns the discovery index session until transferred or disposed.
/// </summary>
public sealed class RepositoryDiscoveryResult(
    DiscoveryInventory inventory,
    MftIndexSession? indexSession = null) : IAsyncDisposable
{
    MftIndexSession? _indexSession = indexSession;

    public DiscoveryInventory Inventory { get; } = inventory;

    public MftIndexSession? DetachIndexSession() => Interlocked.Exchange(ref _indexSession, null);

    public async ValueTask DisposeAsync()
    {
        var session = DetachIndexSession();
        if (session is not null)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }
}

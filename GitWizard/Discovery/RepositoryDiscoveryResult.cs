namespace GitWizard.Discovery;

/// <summary>
/// Owns the discovery index session and its pending repository size pass until transferred or disposed.
/// </summary>
public sealed class RepositoryDiscoveryResult : IAsyncDisposable
{
    RepositoryDiscoverySession? _session;

    public RepositoryDiscoveryResult(DiscoveryInventory inventory, MftIndexSession? indexSession = null)
        : this(inventory, indexSession is null ? null : new RepositoryDiscoverySession(indexSession))
    {
    }

    internal RepositoryDiscoveryResult(DiscoveryInventory inventory, RepositoryDiscoverySession? session)
    {
        Inventory = inventory;
        _session = session;
        RepositorySizes = session?.RepositorySizes ??
            Task.FromResult<IReadOnlyDictionary<string, long>>(new Dictionary<string, long>());
    }

    public DiscoveryInventory Inventory { get; }

    public Task<IReadOnlyDictionary<string, long>> RepositorySizes { get; }

    public MftIndexSession? DetachIndexSession() => _session?.DetachIndexSession();

    internal RepositoryDiscoverySession? DetachSession() => Interlocked.Exchange(ref _session, null);

    public async ValueTask DisposeAsync()
    {
        var session = DetachSession();
        if (session is not null)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }
}

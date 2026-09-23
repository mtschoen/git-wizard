using System.Text.Json.Serialization;
using MFTLib.Index;

namespace GitWizard;

/// <summary>
/// Owns the discovery index session until transferred or disposed.
/// </summary>
public sealed class RepositoryDiscoveryResult : IAsyncDisposable
{
    MftIndexSession? _indexSession;

    public DiscoveryInventory Inventory { get; }

    [JsonIgnore]
    public FileIndex? Index => _indexSession?.Index;

    public RepositoryDiscoveryResult(
        DiscoveryInventory inventory,
        MftIndexSession? indexSession = null)
    {
        Inventory = inventory;
        _indexSession = indexSession;
    }

    public MftIndexSession? DetachIndexSession() => Interlocked.Exchange(ref _indexSession, null);

    public async ValueTask DisposeAsync()
    {
        var session = DetachIndexSession();
        if (session is not null)
            await session.DisposeAsync().ConfigureAwait(false);
    }
}

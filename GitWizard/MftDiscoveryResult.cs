using System.Text.Json.Serialization;
using MFTLib.Index;

namespace GitWizard;

/// <summary>Owns the discovery index session until ownership is transferred to a consumer.</summary>
public sealed class MftDiscoveryResult : IAsyncDisposable
{
    MftIndexSession? _indexSession;
    public bool Success { get; }
    [JsonIgnore]
    public FileIndex? Index => _indexSession?.Index;

    public MftDiscoveryResult(bool success, MftIndexSession? indexSession = null)
    {
        Success = success;
        _indexSession = indexSession;
    }

    public MftIndexSession? DetachIndexSession() => Interlocked.Exchange(ref _indexSession, null);

    public async ValueTask DisposeAsync()
    {
        var owner = DetachIndexSession();
        if (owner is not null)
            await owner.DisposeAsync().ConfigureAwait(false);
    }
}

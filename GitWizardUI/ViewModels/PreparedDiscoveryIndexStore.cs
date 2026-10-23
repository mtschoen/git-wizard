using GitWizard;
using GitWizard.Watch;

namespace GitWizardUI.ViewModels;

public sealed class PreparedDiscoveryIndexStore : IAsyncDisposable
{
    readonly Lock _preparedIndexLock = new();
    MftIndexSession? _preparedDiscoveryIndex;

    internal MftIndexSession? Current
    {
        get
        {
            lock (_preparedIndexLock)
            {
                return _preparedDiscoveryIndex;
            }
        }
    }

    internal MftIndexSession? Take()
    {
        lock (_preparedIndexLock)
        {
            var session = _preparedDiscoveryIndex;
            _preparedDiscoveryIndex = null;
            return session;
        }
    }

    internal async Task SetAsync(MftIndexSession? next)
    {
        MftIndexSession? oldIndex;
        lock (_preparedIndexLock)
        {
            oldIndex = _preparedDiscoveryIndex;
            _preparedDiscoveryIndex = next;
        }

        if (oldIndex != null && !ReferenceEquals(oldIndex, next))
        {
            await oldIndex.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal IReadOnlyList<MftActiveCacheSession> CaptureActiveCacheSessions(
        IndexVolumeChangeSource? liveSource)
    {
        lock (_preparedIndexLock)
        {
            var sessions = new List<MftActiveCacheSession>();
            if (liveSource?.CaptureCacheStatus() is { } live)
            {
                sessions.Add(live);
            }

            if (_preparedDiscoveryIndex?.CaptureCacheStatus() is { } prepared)
            {
                sessions.Add(prepared);
            }

            return sessions.AsReadOnly();
        }
    }

    public ValueTask DisposeAsync() => new(SetAsync(null));
}

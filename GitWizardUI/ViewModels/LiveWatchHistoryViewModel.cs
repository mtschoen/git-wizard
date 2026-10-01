using System.Collections.ObjectModel;
using GitWizard.Watch;

namespace GitWizardUI.ViewModels;

public sealed class LiveWatchHistoryViewModel : IDisposable
{
    readonly Action<Action> _post;
    readonly TimeProvider _clock;
    readonly Dictionary<(Guid Session, string Volume), long> _versions = [];
    ITimer? _timer;
    int _timerGeneration;
    bool _disposed;
    public ObservableCollection<VolumeWatchTransition> Events { get; } = new();
    public event Action? Changed;

    public LiveWatchHistoryViewModel(Action<Action> post, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(post);
        _post = post;
        _clock = timeProvider ?? TimeProvider.System;
    }

    public void Apply(IReadOnlyList<VolumeWatchTransition> transitions)
    {
        if (_disposed)
            return;
        var cutoff = _clock.GetUtcNow() - TimeSpan.FromSeconds(30);
        foreach (var item in transitions)
        {
            var key = (item.SessionId, item.Volume.ToUpperInvariant());
            if (_versions.TryGetValue(key, out var version) && item.Version <= version)
                continue;
            _versions[key] = item.Version;
            if (item.ObservedAt <= cutoff)
                continue;
            Events.Add(item);
            if (Events.Count > 64)
                Events.RemoveAt(0);
        }
        Expire();
        if (Events.Count > 0 && _timer is null)
            StartTimer();
        Changed?.Invoke();
    }

    public void Expire()
    {
        if (_disposed)
            return;
        var cutoff = _clock.GetUtcNow() - TimeSpan.FromSeconds(30);
        for (var index = Events.Count - 1; index >= 0; index--)
            if (Events[index].ObservedAt <= cutoff)
                Events.RemoveAt(index);
        if (Events.Count == 0)
            StopTimer();
        Changed?.Invoke();
    }

    void StartTimer()
    {
        var generation = ++_timerGeneration;
        _timer = _clock.CreateTimer(_ => _post(() =>
        {
            if (!_disposed && generation == _timerGeneration)
                Expire();
        }), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    void StopTimer()
    {
        ++_timerGeneration;
        _timer?.Dispose();
        _timer = null;
    }

    public void Clear()
    {
        StopTimer();
        Events.Clear();
        _versions.Clear();
        Changed?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Clear();
    }
}

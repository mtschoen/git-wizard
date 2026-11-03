namespace GitWizard;

internal sealed class NonRepositoryProbeCache
{
    internal const int DefaultCapacity = 4096;

    readonly SortedDictionary<string, NonRepositoryProbeEntry> _entries;
    readonly TimeProvider _clock;
    readonly Func<string, bool> _directoryExists;
    readonly Func<string, DateTimeOffset> _lastWriteTimeUtc;
    readonly int _capacity;
    readonly object _gate;

    // Paths this refresh confirmed or skipped as non-repositories. Independent of the bounded
    // retention above, so eviction cannot change what the current refresh prunes.
    readonly HashSet<string> _currentDecisions = new(GitWizardApi.PathComparer);

    internal NonRepositoryProbeCache(
        SortedDictionary<string, NonRepositoryProbeEntry> entries,
        TimeProvider clock,
        Func<string, bool> directoryExists,
        Func<string, DateTimeOffset> lastWriteTimeUtc,
        int capacity = DefaultCapacity)
    {
        _entries = entries;
        _clock = clock;
        _directoryExists = directoryExists;
        _lastWriteTimeUtc = lastWriteTimeUtc;
        _capacity = capacity;
        _gate = entries;
    }

    internal bool ShouldSkip(string path, out DateTimeOffset lastWriteTimeUtc)
    {
        lastWriteTimeUtc = _lastWriteTimeUtc(path).ToUniversalTime();
        var key = GitWizardApi.NormalizePath(path);
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                return false;
            }

            if (entry.PolicyVersion != NonRepositoryProbeEntry.CurrentPolicyVersion ||
                entry.LastWriteTimeUtc != lastWriteTimeUtc)
            {
                // Stale: the re-probe decides again, so a cancelled or transient probe must not leave this behind.
                _entries.Remove(key);
                return false;
            }

            _currentDecisions.Add(key);

            _entries[key] = new NonRepositoryProbeEntry
            {
                LastWriteTimeUtc = entry.LastWriteTimeUtc,
                LastUsedUtc = _clock.GetUtcNow(),
                PolicyVersion = NonRepositoryProbeEntry.CurrentPolicyVersion,
            };
            return true;
        }
    }

    internal void Record(string path, DateTimeOffset lastWriteTimeUtc)
    {
        var key = GitWizardApi.NormalizePath(path);
        lock (_gate)
        {
            _currentDecisions.Add(key);
            _entries[key] = new NonRepositoryProbeEntry
            {
                LastWriteTimeUtc = lastWriteTimeUtc.ToUniversalTime(),
                LastUsedUtc = _clock.GetUtcNow(),
                PolicyVersion = NonRepositoryProbeEntry.CurrentPolicyVersion,
            };

            while (_entries.Count > _capacity)
            {
                var oldestKey = _entries.MinBy(pair => pair.Value.LastUsedUtc).Key;
                _entries.Remove(oldestKey);
            }
        }
    }

    internal void Remove(string path)
    {
        lock (_gate)
        {
            var key = GitWizardApi.NormalizePath(path);
            _entries.Remove(key);
            _currentDecisions.Remove(key);
        }
    }

    internal void DropMissingPaths()
    {
        lock (_gate)
        {
            foreach (var missingPath in _entries.Keys.Where(path => !_directoryExists(path)).ToArray())
            {
                _entries.Remove(missingPath);
            }
        }
    }

    internal bool IsNonRepositoryThisRefresh(string path)
    {
        lock (_gate)
        {
            return _currentDecisions.Contains(GitWizardApi.NormalizePath(path));
        }
    }

    internal bool Contains(string path)
    {
        lock (_gate)
        {
            return _entries.ContainsKey(GitWizardApi.NormalizePath(path));
        }
    }
}

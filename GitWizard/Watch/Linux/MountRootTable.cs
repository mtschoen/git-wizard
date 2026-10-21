namespace GitWizard.Watch.Linux;

/// <summary>
/// Remembers the marked root paths of each filesystem, grouped by the filesystem identifier fanotify reports
/// with each event. <c>open_by_handle_at</c> resolves a handle against the filesystem of the descriptor it is
/// given, so a batch opens a short-lived descriptor on a currently reachable root of the event's filesystem and
/// closes it when the batch is decoded. No descriptor is held between batches, so the watch never keeps a
/// watched filesystem busy and a plain <c>umount</c> succeeds.
/// </summary>
sealed class MountRootTable
{
    readonly IFanotifyNative _native;
    readonly Lock _gate = new();
    readonly Dictionary<long, List<string>> _roots = [];
    readonly Dictionary<long, string> _chosenRoots = [];

    public MountRootTable(IFanotifyNative native) => _native = native;

    /// <summary>
    /// Opens <paramref name="path"/> once to read its filesystem identifier, closes it, and registers the path.
    /// </summary>
    /// <returns>A failure message, or <see langword="null"/> when the root is registered.</returns>
    public string? TryAdd(string path)
    {
        var openResult = _native.OpenDirectory(path);
        if (openResult.Value < 0)
        {
            return $"Could not open a descriptor on {path} (errno {openResult.ErrorNumber}).";
        }

        using var descriptor = new SafeUnixFileDescriptor(openResult.Value, _native);
        if (!_native.TryReadFilesystemIdentifier(descriptor.Value, out var filesystemId))
        {
            return $"Could not read the filesystem identifier of {path}.";
        }

        lock (_gate)
        {
            if (!_roots.TryGetValue(filesystemId, out var paths))
            {
                _roots.Add(filesystemId, paths = []);
            }

            paths.Add(path);
        }

        return null;
    }

    /// <summary>Starts a decoding batch. Dispose it to close every descriptor the batch opened.</summary>
    public MountBatch OpenBatch() => new(this);

    public void Clear()
    {
        lock (_gate)
        {
            _roots.Clear();
            _chosenRoots.Clear();
        }
    }

    List<string> RootsOf(long filesystemId)
    {
        lock (_gate)
        {
            return _roots.TryGetValue(filesystemId, out var paths) ? [.. paths] : [];
        }
    }

    /// <summary>Records the root a batch resolved through and reports whether it differs from the previous choice.</summary>
    bool RecordChoice(long filesystemId, string? root)
    {
        lock (_gate)
        {
            var hadPrevious = _chosenRoots.TryGetValue(filesystemId, out var previous);
            if (root is null)
            {
                _chosenRoots.Remove(filesystemId);
                return hadPrevious;
            }

            _chosenRoots[filesystemId] = root;
            return hadPrevious && !string.Equals(previous, root, StringComparison.Ordinal);
        }
    }

    /// <summary>The descriptors one decoded batch resolves through, opened on demand and closed on disposal.</summary>
    internal sealed class MountBatch : IDisposable
    {
        readonly MountRootTable _table;
        readonly Dictionary<long, SafeUnixFileDescriptor?> _descriptors = [];

        internal MountBatch(MountRootTable table) => _table = table;

        /// <summary>
        /// Gives the descriptor for <paramref name="filesystemId"/>, opening one on the first registered root that
        /// currently reaches that filesystem. A root that was renamed, removed, overmounted or unmounted is skipped
        /// because opening its path no longer reaches the filesystem.
        /// </summary>
        /// <param name="filesystemId">The filesystem identifier fanotify reported with the event.</param>
        /// <param name="descriptor">The open descriptor, valid until the batch is disposed.</param>
        /// <param name="mountChanged">
        /// <see langword="true"/> when the filesystem is reached through a different root than the previous batch
        /// used, or is no longer reachable. Paths resolved through the earlier mount must then be discarded.
        /// </param>
        public bool TryGetDescriptor(long filesystemId, out int descriptor, out bool mountChanged)
        {
            if (_descriptors.TryGetValue(filesystemId, out var known))
            {
                mountChanged = false;
                descriptor = known?.Value ?? -1;
                return known is not null;
            }

            string? chosenRoot = null;
            SafeUnixFileDescriptor? opened = null;
            foreach (var root in _table.RootsOf(filesystemId))
            {
                opened = OpenReaching(root, filesystemId);
                if (opened is null)
                {
                    continue;
                }

                chosenRoot = root;
                break;
            }

            _descriptors[filesystemId] = opened;
            mountChanged = _table.RecordChoice(filesystemId, chosenRoot);
            descriptor = opened?.Value ?? -1;
            return opened is not null;
        }

        public void Dispose()
        {
            foreach (var descriptor in _descriptors.Values)
            {
                descriptor?.Dispose();
            }

            _descriptors.Clear();
        }

        SafeUnixFileDescriptor? OpenReaching(string root, long filesystemId)
        {
            var openResult = _table._native.OpenDirectory(root);
            if (openResult.Value < 0)
            {
                return null;
            }

            var candidate = new SafeUnixFileDescriptor(openResult.Value, _table._native);
            if (_table._native.TryReadFilesystemIdentifier(candidate.Value, out var reached) && reached == filesystemId)
            {
                return candidate;
            }

            candidate.Dispose();
            return null;
        }
    }
}

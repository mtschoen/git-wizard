using System.Runtime.CompilerServices;
using System.Threading.Channels;
using MFTLib.Index;

namespace GitWizard.Watch;

/// <summary>Owns an index session and translates its live changes into repository-watch batches.</summary>
public sealed partial class IndexVolumeChangeSource : IVolumeChangeSource, IVolumeChangeDiagnostics, IUsnJournalGrower, IVolumeScanProgress
{
    readonly Func<IReadOnlyList<string>, IProgress<IndexScanProgress>, CancellationToken, Task<MftIndexSession>> _openSession;
    readonly Channel<VolumeChangeBatch> _output = Channel.CreateUnbounded<VolumeChangeBatch>();
    readonly Lock _lifecycleLock = new();
    readonly Lock _faultLock = new();
    readonly HashSet<Exception> _reportedFaults = new(ReferenceEqualityComparer.Instance);
    readonly HashSet<char> _usableDrives = [];
    readonly Dictionary<char, string> _driveRealRoots = new();
    MftIndexSession? _owner;
    Task<VolumeArmResult>? _arming;
    TaskCompletionSource? _armFinished;
    Task? _stopping;
    Task? _disposal;
    bool _disposed;
    bool _watchClaimed;
    bool _watchStarted;
    FileIndex? _watchedIndex;
    int _deathReported;

    public event Action<string>? SourceDied;
    public event Action<string, string>? DriveFailed;
    public event Action<JournalWarning>? JournalWarningDetected;
    public event Action<VolumeScanProgress>? ScanProgress;

    public IndexVolumeChangeSource(
        Func<IReadOnlyList<string>, IProgress<IndexScanProgress>, CancellationToken, Task<MftIndexSession>> openSession,
        MftIndexSession? preparedIndex = null)
    {
        ArgumentNullException.ThrowIfNull(openSession);
        _openSession = openSession;
        _owner = preparedIndex;
    }

    public UsnJournalSettings QueryUsnJournalSettings(char driveLetter)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("USN journals require Windows.");
        var owner = _owner ?? throw new InvalidOperationException("No index session available.");
        return owner.Index.QueryUsnJournalSettings(driveLetter);
    }

    public Task<UsnJournalSettings> GrowUsnJournalAsync(char driveLetter, long maximumSize, long allocationDelta, CancellationToken cancellationToken)
    {
        var broker = _owner?.GetBrokerClient();
        if (broker == null)
            throw new InvalidOperationException("No broker connection available.");
        return broker.GrowUsnJournalAsync(driveLetter, maximumSize, allocationDelta, cancellationToken);
    }

    public Task<VolumeArmResult> ArmAndCatchUpAsync(IReadOnlyCollection<string> volumes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(volumes);
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_arming is not null)
                throw new InvalidOperationException("This source has already been armed.");
            var armFinished = _armFinished =
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _arming = ArmCoreAsync(volumes.ToArray(), armFinished, ct);
        }
    }

    async Task<VolumeArmResult> ArmCoreAsync(IReadOnlyCollection<string> volumes,
        TaskCompletionSource armFinished, CancellationToken cancellationToken)
    {
        // Yield so the lifecycle task is published before opening can invoke caller code.
        await Task.Yield();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requested = NormalizeDrives(volumes);
            if (_owner is null || _owner.IsDisposed || _owner.IsFaulted ||
                !requested.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(NormalizeDrives(_owner.Drives)))
            {
                var prepared = _owner;
                _owner = null;
                if (prepared is not null)
                    await prepared.DisposeAsync().ConfigureAwait(false);
                _owner = await _openSession(requested, new ArmScanProgress(requested, ReportScanProgress),
                    cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var errors = new Dictionary<string, string>(_owner.ScanErrors, StringComparer.OrdinalIgnoreCase);
            foreach (var status in _owner.CaptureDriveStatuses())
            {
                if (status.IdentityFailure is not null)
                    errors[status.Drive] = status.IdentityFailure;
                else if (status.IndexStatus?.MftProducerFailureMessage is { Length: > 0 } msg)
                    errors[status.Drive] = msg;
                else if (status.IndexStatus?.State is DriveState.Failed or DriveState.Offline)
                    errors[status.Drive] = $"Drive is {status.IndexStatus.State}.";
                else
                    errors.Remove(status.Drive);
            }
            var warnings = new List<JournalWarning>();
            var configuration = await GitWizardConfiguration.GetGlobalConfigurationAsync(cancellationToken).ConfigureAwait(false);

            foreach (var drive in _owner.Index.Drives)
                ProcessArmedDrive(drive, configuration, errors, warnings, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            if (_usableDrives.Count > 0)
            {
                await EnsureWatchStartedAsync(_owner, cancellationToken).ConfigureAwait(false);

                var catchUpDrives = _usableDrives.ToArray();
                var catchUpTasks = catchUpDrives.Select(d => WaitDriveAsync(_owner, d, cancellationToken)).ToArray();
                await Task.WhenAll(catchUpTasks).ConfigureAwait(false);

                foreach (var d in catchUpDrives)
                {
                    var driveStatus = _owner.CaptureDriveStatuses().FirstOrDefault(s =>
                        string.Equals(s.Drive, d.ToString(), StringComparison.OrdinalIgnoreCase));
                    if (driveStatus?.IndexStatus is { State: DriveState.Failed or DriveState.Offline } failed)
                    {
                        var msg = failed.MftProducerFailureMessage ?? $"Drive is {failed.State}.";
                        RecordStartupDriveFailure(d, msg);
                    }
                }

                lock (_faultLock)
                {
                    foreach (var (drv, msg) in _startupErrors)
                    {
                        errors[drv.ToString()] = msg;
                    }
                }
            }

            return new VolumeArmResult(errors, warnings);
        }
        catch
        {
            var owner = _owner;
            _owner = null;
            if (owner is not null)
                await owner.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            armFinished.TrySetResult();
        }
    }

    Task EnsureWatchStartedAsync(MftIndexSession owner, CancellationToken cancellationToken)
    {
        lock (_lifecycleLock)
        {
            if (_watchStarted)
                return Task.CompletedTask;
            _watchStarted = true;
            return StartWatch(owner, cancellationToken);
        }
    }

    static string[] NormalizeDrives(IEnumerable<string> volumes) => volumes
        .Select(volume => (WatchPath.Parse(volume.Trim()).Volume ?? volume.Trim().TrimEnd(':', '\\', '/'))
            .ToUpperInvariant())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public async IAsyncEnumerable<VolumeChangeBatch> WatchAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var claimed = false;
        try
        {
            bool hasWatchableDrives;
            lock (_lifecycleLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_arming?.IsCompletedSuccessfully != true)
                    throw new InvalidOperationException("ArmAndCatchUpAsync must finish before WatchAsync.");
                if (_watchClaimed)
                    throw new InvalidOperationException("This source already has a watch reader.");
                _watchClaimed = claimed = true;
                var owner = _owner;
                hasWatchableDrives = owner is not null && _usableDrives.Count > 0;
            }
            if (!hasWatchableDrives)
            {
                var reason = _owner?.ScanErrors.Values.FirstOrDefault() is { Length: > 0 } error
                    ? $"No requested drives could be watched: {error}"
                    : "No requested drives could be watched.";
                ReportDeath(reason);
                yield break;
            }
            await foreach (var batch in _output.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return batch;
        }
        finally
        {
            if (claimed)
                await StopOnceAsync().ConfigureAwait(false);
        }
    }

    Task StartWatch(MftIndexSession owner, CancellationToken cancellationToken)
    {
        var index = owner.Index;
        index.Changed += OnChanged;
        index.WatchFaulted += OnWatchFaulted;
        _watchedIndex = index;
        // Starting means the library pump was scheduled, not that catch-up has drained.
        return index.StartWatchingAsync(cancellationToken);
    }

    void OnChanged(FileChange change)
    {
        // FileEntry.Id.DriveLetter is the stable drive key MFTLib assigns, unlike change.Path,
        // which renders the drive's real indexed root directory and only reads back as a
        // "<letter>:\" volume when that root directory happens to be the drive root (MFTLib #143).
        var letter = char.ToUpperInvariant(change.Entry.Id.DriveLetter);
        var rewritten = _driveRealRoots.TryGetValue(letter, out var realRoot)
            ? change with
            {
                Path = RewriteToDriveLetterRoot(change.Path, realRoot, letter),
                PreviousPath = change.PreviousPath is null
                    ? null
                    : RewriteToDriveLetterRoot(change.PreviousPath, realRoot, letter)
            }
            : change;
        var batch = new VolumeChangeBatch(letter.ToString(), IndexChangeMapping.Map(rewritten));
        lock (_startupBufferLock)
        {
            if (_startupBuffering && _startupBuffer is not null)
            {
                _startupBuffer.Add(batch);
                return;
            }
        }
        _output.Writer.TryWrite(batch);
    }

    /// <summary>
    /// Rewrites a real path rooted at <paramref name="realRoot" /> back onto
    /// <paramref name="driveLetter" />'s own "&lt;letter&gt;:\" form, so every path this source
    /// hands to a consumer (<see cref="RepositoryWatchService" /> among them) stays in the
    /// drive-letter-rooted coordinate space those consumers expect. A no-op on a real Windows
    /// volume, where the indexed root directory already is "&lt;letter&gt;:\".
    /// </summary>
    static string RewriteToDriveLetterRoot(string realPath, string realRoot, char driveLetter)
    {
        if (!realPath.StartsWith(realRoot, StringComparison.OrdinalIgnoreCase))
            return realPath;
        if (realPath.Length > realRoot.Length &&
            !realRoot.EndsWith('/') && !realRoot.EndsWith('\\') &&
            realPath[realRoot.Length] is not ('/' or '\\'))
        {
            return realPath;
        }
        var relative = realPath[realRoot.Length..].TrimStart('\\', '/').Replace('/', '\\');
        return relative.Length == 0 ? $"{driveLetter}:\\" : $"{driveLetter}:\\{relative}";
    }

    void OnWatchFaulted(WatchFault fault)
    {
        bool terminal;
        lock (_faultLock)
        {
            _reportedFaults.Add(fault.Exception);
            if (fault.Kind == WatchFaultKind.Subscriber)
            {
                // A handler error reaches the reader as an error, never as a broker death.
                _output.Writer.TryComplete(fault.Exception);
                return;
            }
            if (fault.DriveLetter is { } drive)
                _usableDrives.Remove(char.ToUpperInvariant(drive));
            terminal = fault.DriveLetter is null || _usableDrives.Count == 0;
        }
        try
        {
            if (fault.DriveLetter is { } drive)
            {
                RaiseFaultJournalWarning(char.ToUpperInvariant(drive));
                DriveFailed?.Invoke(drive.ToString(), fault.Exception.Message);
            }
        }
        catch (Exception exception)
        {
            // MFTLib discards fault-reporter exceptions, so route them to our reader explicitly.
            _output.Writer.TryComplete(exception);
        }
        if (terminal)
            ReportDeath(fault.Exception.Message);
    }

    void ReportDeath(string reason)
    {
        if (Interlocked.Exchange(ref _deathReported, 1) != 0)
            return;
        try
        {
            SourceDied?.Invoke(reason);
        }
        catch (Exception exception)
        {
            _output.Writer.TryComplete(exception);
        }
        finally
        {
            _output.Writer.TryComplete();
        }
    }

    Task StopOnceAsync()
    {
        lock (_lifecycleLock)
            return _stopping ??= StopCoreAsync();
    }

    async Task StopCoreAsync()
    {
        // Never await a pump while holding the lock that publishes lifecycle tasks.
        await Task.Yield();
        var index = _watchedIndex;
        if (index is null)
            return;
        index.Changed -= OnChanged;
        index.WatchFaulted -= OnWatchFaulted;
        try
        {
            await index.StopWatchingAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (WasReported(exception))
        {
            // Stop rethrows the exact fault already delivered through diagnostics or the reader.
        }
        finally
        {
            _output.Writer.TryComplete();
        }
    }

    bool WasReported(Exception exception)
    {
        lock (_faultLock)
            return _reportedFaults.Contains(exception);
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifecycleLock)
        {
            _disposed = true;
            _output.Writer.TryComplete();
            return new ValueTask(_disposal ??= DisposeCoreAsync(_armFinished?.Task));
        }
    }

    async Task DisposeCoreAsync(Task? armFinished)
    {
        await Task.Yield();
        if (armFinished is not null)
            await armFinished.ConfigureAwait(false);
        try
        {
            await StopOnceAsync().ConfigureAwait(false);
        }
        finally
        {
            if (_owner is not null)
                await _owner.DisposeAsync().ConfigureAwait(false);
        }
    }

    void ReportScanProgress(VolumeScanProgress sample)
    {
        try
        {
            ScanProgress?.Invoke(sample);
        }
        catch (Exception exception)
        {
            // Progress is best-effort; a subscriber fault must never fail a scan.
            GitWizardLog.LogException(exception, "A Live scan-progress subscriber failed.");
        }
    }

    // Reports synchronously on the broker client's receive thread, so samples reach subscribers
    // in frame order; marshalling to a UI thread belongs to the subscriber, per MFTLib's
    // IProgress<T> convention. The broker throttles ScanProgress frames to one per 250 ms, so
    // no further coalescing happens here.
    sealed class ArmScanProgress(IReadOnlyList<string> requestedDrives, Action<VolumeScanProgress> sink)
        : IProgress<IndexScanProgress>
    {
        public void Report(IndexScanProgress value)
        {
            var letter = char.ToUpperInvariant(value.DriveLetter).ToString();
            var ordinal = 0;
            for (var i = 0; i < requestedDrives.Count; i++)
            {
                if (string.Equals(requestedDrives[i], letter, StringComparison.OrdinalIgnoreCase))
                {
                    ordinal = i + 1;
                    break;
                }
            }

            // A sample naming a drive outside the requested set should not exist; clamp rather
            // than report a nonsensical "0 of N".
            sink(new VolumeScanProgress(letter, Math.Max(ordinal, 1), requestedDrives.Count,
                value.RowsWritten, value.TotalRows));
        }
    }
}

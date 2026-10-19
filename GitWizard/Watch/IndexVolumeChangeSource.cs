using System.Runtime.CompilerServices;
using System.Threading.Channels;
using MFTLib;
using MFTLib.Index;

namespace GitWizard.Watch;

/// <summary>Owns an index session and translates its live changes into repository-watch batches.</summary>
public sealed partial class IndexVolumeChangeSource : IVolumeChangeSource, IVolumeChangeDiagnostics, IUsnJournalGrower, IVolumeScanProgress, IPumpLifecycle
{
    readonly Func<IReadOnlyList<string>, IProgress<IndexScanProgress>, CancellationToken, Task<MftIndexSession>> _openSession;
    readonly Channel<VolumeChangeBatch> _output = Channel.CreateUnbounded<VolumeChangeBatch>();
    readonly Lock _lifecycleLock = new();
    readonly Lock _faultLock = new();
    readonly Lock _usableDrivesLock = new();
    readonly HashSet<char> _usableDrives = [];
    readonly Dictionary<char, string> _driveRealRoots = new();
    readonly TaskCompletionSource _pumpStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
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

    /// <summary>
    /// Completes once <c>index.StartWatchingAsync</c> has returned during arming - well before
    /// WatchAsync can be called, since arming is awaited to completion first. A source that is
    /// armed but never becomes watchable (no usable drives) never starts a pump, so this stays
    /// incomplete for that run.
    /// </summary>
    public Task PumpStarted => _pumpStarted.Task;

    public IndexVolumeChangeSource(
        Func<IReadOnlyList<string>, IProgress<IndexScanProgress>, CancellationToken, Task<MftIndexSession>> openSession,
        MftIndexSession? preparedIndex = null)
    {
        ArgumentNullException.ThrowIfNull(openSession);
        _openSession = openSession;
        _owner = preparedIndex;
    }

    public UsnJournalSettings QueryUsnJournalSettings(DriveKey drive)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("USN journals require Windows.");
        var owner = _owner ?? throw new InvalidOperationException("No index session available.");
        return owner.Index.QueryUsnJournalSettings(drive);
    }

    public Task<UsnJournalSettings> GrowUsnJournalAsync(DriveKey drive, UsnJournalSettings target, CancellationToken cancellationToken)
    {
        // The session launches the broker on first use, so a grow never needs a connected process up front.
        var owner = _owner ?? throw new InvalidOperationException("No index session available.");
        var brokerSession = owner.BrokerSession
            ?? throw new InvalidOperationException("The index session has no broker session.");
        return brokerSession.GrowUsnJournalAsync(drive, target, cancellationToken);
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
                else if (status.IndexStatus?.Failure?.Message is { Length: > 0 } message)
                    errors[status.Drive] = message;
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
            PublishArmedDrives(_owner, requested, errors);

            if (HasUsableDrives())
            {
                await EnsureWatchStartedAsync(_owner, cancellationToken).ConfigureAwait(false);

                var catchUpDrives = CaptureUsableDrives();
                var catchUpTasks = catchUpDrives.Select(d => WaitDriveAsync(_owner, d, cancellationToken)).ToArray();
                await Task.WhenAll(catchUpTasks).ConfigureAwait(false);

                foreach (var d in catchUpDrives)
                {
                    var driveStatus = _owner.CaptureDriveStatuses().FirstOrDefault(s =>
                        string.Equals(s.Drive, d.ToString(), StringComparison.OrdinalIgnoreCase));
                    if (driveStatus?.IndexStatus is { State: DriveState.Failed or DriveState.Offline } failed)
                    {
                        var message = failed.Failure?.Message ?? $"Drive is {failed.State}.";
                        RecordStartupDriveFailure(d, message);
                    }
                }
                ExcludeDrivesWithFailedWatch(_owner, catchUpDrives);

                lock (_faultLock)
                {
                    foreach (var (drv, msg) in _startupErrors)
                    {
                        errors[drv.ToString()] = msg;
                    }
                }
            }

            // The warnings above were built before watching started: report the drives as they are now.
            warnings.Clear();
            warnings.AddRange(CollectArmedJournalWarnings(_owner, configuration));
            return new VolumeArmResult(errors, warnings);
        }
        catch
        {
            // Stop a watch this arm started before its index is disposed under it.
            await StopOnceAsync().ConfigureAwait(false);
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
            MftIndexSession? owner;
            lock (_lifecycleLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_arming?.IsCompletedSuccessfully != true)
                    throw new InvalidOperationException("ArmAndCatchUpAsync must finish before WatchAsync.");
                if (_watchClaimed)
                    throw new InvalidOperationException("This source already has a watch reader.");
                _watchClaimed = claimed = true;
                owner = _owner;
            }
            var hasWatchableDrives = owner is not null && HasUsableDrives();
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
        index.WatchStateChanged += ApplyWatchState;
        _watchedIndex = index;
        SeedWatchStates(index);
        // Starting means the library pump was scheduled, not that catch-up has drained.
        return StartWatchAndSignalAsync(index, cancellationToken);
    }

    async Task StartWatchAndSignalAsync(FileIndex index, CancellationToken cancellationToken)
    {
        var results = await index.StartWatchingAsync(index.Drives.Select(status => status.Drive), cancellationToken).ConfigureAwait(false);
        foreach (var result in results.Where(result => result.Outcome == DriveOperationOutcome.Failed))
            RecordStartupDriveFailure(result.Drive.Letter, result.Failure?.Message ?? "The drive watch could not start.");
        _pumpStarted.TrySetResult();
    }

    void OnChanged(FileChange change)
    {
        lock (_deliveryLock)
            DeliverChange(change);
    }

    void DeliverChange(FileChange change)
    {
        // FileEntry.Drive is the stable drive key MFTLib assigns, unlike change.Path,
        // which renders the drive's real indexed root directory and only reads back as a
        // "<letter>:\" volume when that root directory happens to be the drive root (MFTLib #143).
        var letter = change.Entry.Drive.Letter;
        var rewritten = _driveRealRoots.TryGetValue(letter, out var realRoot)
            ? change with
            {
                Path = RewriteToDriveLetterRoot(change.Path, realRoot, letter),
                PreviousPath = change.PreviousPath is null
                    ? null
                    : RewriteToDriveLetterRoot(change.PreviousPath, realRoot, letter)
            }
            : change;
        Deliver(new VolumeChangeBatch(letter.ToString(), IndexChangeMapping.Map(rewritten)));
    }

    // Startup buffers every batch until discovery has reconciled membership; afterwards batches
    // go straight to the reader.
    void Deliver(VolumeChangeBatch batch)
    {
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

    // A drive MFTLib is recovering (Drive, Apply, or a lost catch-up it is still retrying) stays
    // usable and is reported quietly; every other fault ends that drive's watch until the user
    // restarts it, and the source dies once no usable drive is left.
    void OnWatchFaulted(WatchFault fault)
    {
        if (fault.Kind == WatchFaultKind.Subscriber)
        {
            // A handler error reaches the reader as an error, never as a broker death.
            _output.Writer.TryComplete(fault.Exception);
            return;
        }
        var drive = fault.Drive.Letter;
        if (fault.IsRecovering)
        {
            try
            {
                CaptureRecoveryBaseline(drive);
                GitWizardLog.Log($"Live watch on {drive}: {fault.Kind} fault, MFTLib is rescanning the drive: " +
                                 fault.Exception.Message, GitWizardLog.LogType.Verbose);
                RaiseFaultJournalWarning(drive);
            }
            catch (Exception exception)
            {
                // A failed baseline cannot be reconciled, even if the library's rescan succeeds.
                FailWatchState(drive, exception);
            }
            return;
        }
        bool terminal;
        var owner = _owner;
        var origin = owner is null ? VolumeBlockOrigin.Unknown : CurrentOrigin(owner, drive);
        lock (_watchStateLock)
        {
            terminal = !RemoveUsableDrive(drive);
            _recoveryBaselines.Remove(drive);
            long? observedVersion = _watchStateVersions.TryGetValue(drive, out var value) ? value : null;
            PublishVolumeState(VolumeWatchStatus.Excluded(drive.ToString(), origin, fault.Exception.Message)
                with
            { WatchStateVersion = observedVersion });
            PulseWatchStateLocked();
        }
        try
        {
            RaiseFaultJournalWarning(drive);
            DriveFailed?.Invoke(drive.ToString(), fault.Exception.Message);
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
}

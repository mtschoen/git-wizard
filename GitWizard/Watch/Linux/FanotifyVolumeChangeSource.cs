using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Threading.Channels;

namespace GitWizard.Watch.Linux;

/// <summary>
/// Linux <see cref="IVolumeChangeSource"/> implementation backed by the kernel <c>fanotify</c>
/// subsystem with <c>FAN_REPORT_DFID_NAME</c>. Marks the specified volume filesystems, runs an
/// asynchronous file descriptor read loop, decodes incoming events into <see cref="VolumeChangeBatch"/>
/// instances, and resolves file paths via directory file handles and <c>/proc/self/fd/</c>.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed partial class FanotifyVolumeChangeSource : IVolumeChangeSource, IVolumeReadiness, IPumpLifecycle
{
    readonly IFanotifyNative _native;
    readonly MountRootTable _mounts;
    readonly object _handleCacheLock = new();
    readonly Dictionary<FileHandleKey, string> _handleCache = [];
    readonly Lock _readinessGate = new();
    VolumeReadinessSnapshot _readiness = VolumeReadinessSnapshot.Empty;

    SafeUnixFileDescriptor? _descriptor;
    List<string> _watchedVolumes = [];
    CancellationTokenSource? _activePumpCts;
    Task? _activePumpTask;
    TaskCompletionSource _pumpStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public event Action<string>? SourceDied;
    public event Action<VolumeReadinessSnapshot>? ReadinessChanged;

    public WatchReadinessMode Mode => WatchReadinessMode.FilesystemMarks;
    public VolumeReadinessSnapshot Readiness { get { lock (_readinessGate) return _readiness; } }

    /// <summary>Completes once <see cref="PumpEventsAsync"/> has actually begun executing on its pool thread.</summary>
    public Task PumpStarted => Volatile.Read(ref _pumpStarted).Task;

    void PublishVolumeState(VolumeWatchStatus status)
    {
        lock (_readinessGate)
        {
            var snapshot = _readiness = _readiness.With(status);
            try { ReadinessChanged?.Invoke(snapshot); }
            catch (Exception exception) { GitWizardLog.LogException(exception, "A Live readiness subscriber failed."); }
        }
    }

    void ReportSourceDeath(string reason)
    {
        foreach (var volume in _watchedVolumes)
            PublishVolumeState(VolumeWatchStatus.Excluded(volume, VolumeBlockOrigin.Unknown, reason));
        SourceDied?.Invoke(reason);
    }

    public FanotifyVolumeChangeSource()
        : this(FanotifyNative.Instance)
    {
    }

    internal FanotifyVolumeChangeSource(IFanotifyNative native)
    {
        _native = native;
        _mounts = new MountRootTable(native);
    }

    public async Task<VolumeArmResult> ArmAndCatchUpAsync(
        IReadOnlyCollection<string> volumes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(volumes);
        ct.ThrowIfCancellationRequested();

        await StopActivePumpAsync().ConfigureAwait(false);

        _descriptor?.Dispose();
        _descriptor = null;
        _watchedVolumes = [];
        lock (_handleCacheLock)
        {
            _handleCache.Clear();
        }
        _mounts.Clear();

        lock (_readinessGate)
            _readiness = VolumeReadinessSnapshot.Empty;

        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (volumes.Count == 0)
            return new VolumeArmResult(errors, Array.Empty<JournalWarning>());

        var initializeResult = _native.Initialize(
            FanotifyConstants.CloseOnExecute | FanotifyConstants.ReportDirectoryFileIdAndName,
            FanotifyConstants.ReadOnly);

        if (initializeResult.Value < 0)
        {
            var message = $"fanotify_init failed (errno {initializeResult.ErrorNumber}).";
            foreach (var volume in volumes)
            {
                errors[volume] = message;
                PublishVolumeState(VolumeWatchStatus.Excluded(volume, VolumeBlockOrigin.Unknown, message));
            }

            return new VolumeArmResult(errors, Array.Empty<JournalWarning>());
        }

        _descriptor = new SafeUnixFileDescriptor(initializeResult.Value, _native);

        var watched = new List<string>();
        foreach (var volume in volumes)
        {
            if (string.IsNullOrWhiteSpace(volume))
            {
                errors[volume] = "Volume path cannot be null or whitespace.";
                PublishVolumeState(VolumeWatchStatus.Excluded(volume, VolumeBlockOrigin.Unknown, errors[volume]));
            }
            else if (!Directory.Exists(volume))
            {
                errors[volume] = $"Directory does not exist: {volume}";
                PublishVolumeState(VolumeWatchStatus.Excluded(volume, VolumeBlockOrigin.Unknown, errors[volume]));
            }
            else
            {
                var markResult = _native.Mark(
                    _descriptor.Value,
                    FanotifyConstants.MarkAdd | FanotifyConstants.MarkFilesystem,
                    FanotifyConstants.ProbeEventMask,
                    FanotifyConstants.CurrentWorkingDirectory,
                    volume);

                if (markResult.Value < 0)
                {
                    errors[volume] = $"fanotify_mark failed on {volume} (errno {markResult.ErrorNumber}).";
                    PublishVolumeState(VolumeWatchStatus.Excluded(volume, VolumeBlockOrigin.Unknown, errors[volume]));
                }
                else if (_mounts.TryAdd(volume) is { } mountFailure)
                {
                    errors[volume] = mountFailure;
                    PublishVolumeState(VolumeWatchStatus.Excluded(volume, VolumeBlockOrigin.Unknown, mountFailure));
                }
                else
                {
                    watched.Add(volume);
                    PublishVolumeState(VolumeWatchStatus.CaughtUp(volume, VolumeBlockOrigin.Unknown));
                }
            }
        }

        _watchedVolumes = watched.OrderByDescending(volume => volume.Length).ToList();
        return new VolumeArmResult(errors, Array.Empty<JournalWarning>());
    }

    public async IAsyncEnumerable<VolumeChangeBatch> WatchAsync([EnumeratorCancellation] CancellationToken ct)
    {
        if (_descriptor is null)
            throw new InvalidOperationException("ArmAndCatchUpAsync must be called before WatchAsync.");

        if (_watchedVolumes.Count == 0)
            yield break;

        var pumpStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _pumpStarted, pumpStarted);

        var channel = Channel.CreateUnbounded<VolumeChangeBatch>();
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pumpCancellationToken = linkedCts.Token;
        var pumpTask = Task.Run(() => PumpEventsAsync(channel.Writer, pumpStarted, pumpCancellationToken), CancellationToken.None);

        _activePumpCts = linkedCts;
        _activePumpTask = pumpTask;

        try
        {
            await foreach (var batch in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return batch;
        }
        finally
        {
            try
            {
                await linkedCts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // Expected if CTS was already disposed.
            }

            try
            {
                await pumpTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when pump terminates on cancellation.
            }
            finally
            {
                if (ReferenceEquals(_activePumpTask, pumpTask))
                {
                    _activePumpTask = null;
                    _activePumpCts = null;
                }
                linkedCts.Dispose();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopActivePumpAsync().ConfigureAwait(false);
        lock (_handleCacheLock)
        {
            _handleCache.Clear();
        }
        _mounts.Clear();
        _descriptor?.Dispose();
        _descriptor = null;
    }

    async Task StopActivePumpAsync()
    {
        using var pumpCts = Interlocked.Exchange(ref _activePumpCts, null);
        if (pumpCts is not null)
        {
            try
            {
                await pumpCts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // Expected if WatchAsync's finally already disposed the linked CTS.
            }
        }

        var pumpTask = Interlocked.Exchange(ref _activePumpTask, null);
        if (pumpTask is not null)
        {
            try
            {
                await pumpTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when pump task is cancelled during disposal or re-arm.
            }
        }
    }

    async Task PumpEventsAsync(ChannelWriter<VolumeChangeBatch> writer, TaskCompletionSource pumpStarted, CancellationToken cancellationToken)
    {
        var descriptor = _descriptor;
        if (descriptor is null || descriptor.IsInvalid || descriptor.IsClosed)
        {
            // The pump never actually began reading; leave pumpStarted incomplete so a consumer
            // racing on it never mistakes a dead descriptor for an active pump.
            writer.TryComplete();
            return;
        }

        // The pump is now running on its own pool thread and about to read - this is the
        // earliest point that is both true and observable by a consumer waiting on PumpStarted.
        pumpStarted.TrySetResult();

        try
        {
            var buffer = new byte[8192];
            while (!cancellationToken.IsCancellationRequested)
            {
                if (descriptor.IsClosed || descriptor.IsInvalid)
                    break;

                var pollResult = _native.Poll(descriptor.Value, 250);
                if (pollResult < 0)
                {
                    if (!cancellationToken.IsCancellationRequested)
                        ReportSourceDeath("fanotify poll failed.");
                    break;
                }

                if (pollResult == 0)
                    continue;

                if (descriptor.IsClosed || descriptor.IsInvalid)
                    break;

                var bytesRead = _native.Read(descriptor.Value, buffer, buffer.Length);
                if (bytesRead <= 0)
                {
                    if (!cancellationToken.IsCancellationRequested)
                        ReportSourceDeath("fanotify read returned no bytes.");
                    break;
                }

                var (entries, queueOverflow) = DecodeEvents(buffer, bytesRead);
                if (entries.Count > 0)
                    await writer.WriteAsync(new VolumeChangeBatch(LinuxWatchRouting.Volume, entries), cancellationToken).ConfigureAwait(false);

                if (queueOverflow)
                {
                    if (!cancellationToken.IsCancellationRequested)
                        ReportSourceDeath("fanotify event queue overflowed.");
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on cancellation: the watch pump terminates cleanly.
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            ReportSourceDeath($"fanotify pump faulted: {exception.Message}");
        }
        finally
        {
            writer.TryComplete();
        }
    }

}

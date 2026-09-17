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
public sealed partial class FanotifyVolumeChangeSource : IVolumeChangeSource
{
    readonly IFanotifyNative _native;
    readonly object _handleCacheLock = new();
    readonly Dictionary<FileHandleKey, string> _handleCache = [];

    SafeUnixFileDescriptor? _descriptor;
    List<string> _watchedVolumes = [];
    CancellationTokenSource? _activePumpCts;
    Task? _activePumpTask;

    public event Action<string>? SourceDied;

    public FanotifyVolumeChangeSource()
        : this(FanotifyNative.Instance)
    {
    }

    internal FanotifyVolumeChangeSource(IFanotifyNative native) => _native = native;

    public async Task<VolumeArmResult> ArmAndCatchUpAsync(
        IReadOnlyCollection<string> volumes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(volumes);
        ct.ThrowIfCancellationRequested();

        await StopActivePumpAsync().ConfigureAwait(false);

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
                errors[volume] = message;

            return new VolumeArmResult(errors, Array.Empty<JournalWarning>());
        }

        _descriptor?.Dispose();
        _descriptor = new SafeUnixFileDescriptor(initializeResult.Value, _native);
        lock (_handleCacheLock)
        {
            _handleCache.Clear();
        }

        var watched = new List<string>();
        foreach (var volume in volumes)
        {
            if (string.IsNullOrWhiteSpace(volume))
                errors[volume] = "Volume path cannot be null or whitespace.";
            else if (!Directory.Exists(volume))
                errors[volume] = $"Directory does not exist: {volume}";
            else
            {
                var markResult = _native.Mark(
                    _descriptor.Value,
                    FanotifyConstants.MarkAdd | FanotifyConstants.MarkFilesystem,
                    FanotifyConstants.ProbeEventMask,
                    FanotifyConstants.CurrentWorkingDirectory,
                    volume);

                if (markResult.Value < 0)
                    errors[volume] = $"fanotify_mark failed on {volume} (errno {markResult.ErrorNumber}).";
                else
                    watched.Add(volume);
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

        var channel = Channel.CreateUnbounded<VolumeChangeBatch>();
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pumpCancellationToken = linkedCts.Token;
        var pumpTask = Task.Run(() => PumpEventsAsync(channel.Writer, pumpCancellationToken), CancellationToken.None);

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

    async Task PumpEventsAsync(ChannelWriter<VolumeChangeBatch> writer, CancellationToken cancellationToken)
    {
        var descriptor = _descriptor;
        if (descriptor is null || descriptor.IsInvalid || descriptor.IsClosed)
        {
            writer.TryComplete();
            return;
        }

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
                        SourceDied?.Invoke("fanotify poll failed.");
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
                        SourceDied?.Invoke("fanotify read returned no bytes.");
                    break;
                }

                var (entriesByVolume, queueOverflow) = DecodeEvents(buffer, bytesRead);
                foreach (var (volume, entries) in entriesByVolume)
                {
                    if (entries.Count > 0)
                        await writer.WriteAsync(new VolumeChangeBatch(volume, entries), cancellationToken).ConfigureAwait(false);
                }

                if (queueOverflow)
                {
                    if (!cancellationToken.IsCancellationRequested)
                        SourceDied?.Invoke("fanotify event queue overflowed.");
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
            SourceDied?.Invoke($"fanotify pump faulted: {exception.Message}");
        }
        finally
        {
            writer.TryComplete();
        }
    }

}

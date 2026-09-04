using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Channels;

namespace GitWizard.Watch.Linux;

/// <summary>
/// Linux <see cref="IVolumeChangeSource"/> implementation backed by the kernel <c>fanotify</c>
/// subsystem with <c>FAN_REPORT_DFID_NAME</c>. Marks the specified volume filesystems, runs an
/// asynchronous file descriptor read loop, decodes incoming events into <see cref="VolumeChangeBatch"/>
/// instances, and resolves file paths via directory file handles and <c>/proc/self/fd/</c>.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class FanotifyVolumeChangeSource : IVolumeChangeSource
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
            return new VolumeArmResult(errors);

        var initializeResult = _native.Initialize(
            FanotifyConstants.CloseOnExecute | FanotifyConstants.ReportDirectoryFileIdAndName,
            FanotifyConstants.ReadOnly);

        if (initializeResult.Value < 0)
        {
            var message = $"fanotify_init failed (errno {initializeResult.ErrorNumber}).";
            foreach (var volume in volumes)
                errors[volume] = message;

            return new VolumeArmResult(errors);
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
        return new VolumeArmResult(errors);
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

    (Dictionary<string, List<VolumeChangeEntry>> EntriesByVolume, bool QueueOverflow) DecodeEvents(
        byte[] buffer, int bytesRead)
    {
        var result = new Dictionary<string, List<VolumeChangeEntry>>(StringComparer.Ordinal);
        var offset = 0;
        var queueOverflow = false;
        const int metadataHeaderLength = 24;

        while (offset + metadataHeaderLength <= bytesRead)
        {
            var eventLength = (int)BitConverter.ToUInt32(buffer, offset);
            if (eventLength < metadataHeaderLength || offset + eventLength > bytesRead)
                break;

            var version = buffer[offset + 4];
            if (version != 3)
            {
                offset += eventLength;
                continue;
            }

            var mask = BitConverter.ToUInt64(buffer, offset + 8);
            if ((mask & FanotifyConstants.QueueOverflow) != 0)
            {
                queueOverflow = true;
                offset += eventLength;
                continue;
            }

            var kind = ClassifyKind(mask);
            var shouldInvalidate = (mask & (FanotifyConstants.OnDirectory | FanotifyConstants.MovedFrom)) != 0
                && kind is VolumeEntryKind.Deleted;
            var infoOffset = offset + metadataHeaderLength;
            var eventEnd = offset + eventLength;

            while (infoOffset + 4 <= eventEnd)
            {
                var infoType = buffer[infoOffset];
                var infoLength = (int)BitConverter.ToUInt16(buffer, infoOffset + 2);
                if (infoLength < 4 || infoOffset + infoLength > eventEnd)
                    break;

                if (infoType is FanotifyConstants.EventInfoTypeDfidName
                    or FanotifyConstants.EventInfoTypeOldDfidName
                    or FanotifyConstants.EventInfoTypeNewDfidName
                    or FanotifyConstants.EventInfoTypeDfid
                    or FanotifyConstants.EventInfoTypeFid)
                {
                    var entry = DecodeFidRecord(buffer, infoOffset, infoLength, kind);
                    if (entry is not null)
                    {
                        if (shouldInvalidate)
                            InvalidateCacheForPath(entry.Value.FullPath);

                        var volume = ResolveVolumeForPath(entry.Value.FullPath);
                        if (!result.TryGetValue(volume, out var list))
                            result[volume] = list = [];

                        list.Add(entry.Value);
                    }
                }

                infoOffset += infoLength;
            }

            offset += eventLength;
        }

        return (result, queueOverflow);
    }

    VolumeChangeEntry? DecodeFidRecord(byte[] buffer, int infoOffset, int infoLength, VolumeEntryKind kind)
    {
        if (infoLength < 20)
            return null;

        var handleBytes = (int)BitConverter.ToUInt32(buffer, infoOffset + 12);
        if (infoLength < 20 + handleBytes)
            return null;

        var handleTotalSize = 8 + handleBytes;
        var fileHandlePayload = new byte[handleTotalSize];
        Buffer.BlockCopy(buffer, infoOffset + 12, fileHandlePayload, 0, handleTotalSize);

        var fileName = string.Empty;
        var nameOffset = infoOffset + 20 + handleBytes;
        if (nameOffset < infoOffset + infoLength)
        {
            var maxNameBytes = infoOffset + infoLength - nameOffset;
            var nullIndex = Array.IndexOf(buffer, (byte)0, nameOffset, maxNameBytes);
            var nameLength = nullIndex >= 0 ? nullIndex - nameOffset : maxNameBytes;
            if (nameLength > 0)
                fileName = Encoding.UTF8.GetString(buffer, nameOffset, nameLength);
        }

        var directoryPath = ResolveDirectoryPath(fileHandlePayload);
        if (directoryPath is null)
            return null;

        string fullPath;
        if (string.IsNullOrEmpty(fileName) || fileName == ".")
            fullPath = directoryPath;
        else if (directoryPath == "/")
            fullPath = "/" + fileName;
        else
            fullPath = Path.Combine(directoryPath, fileName);

        return new VolumeChangeEntry(fullPath, kind);
    }

    string? ResolveDirectoryPath(byte[] fileHandlePayload)
    {
        var key = new FileHandleKey(fileHandlePayload);
        lock (_handleCacheLock)
        {
            if (_handleCache.TryGetValue(key, out var cachedPath))
                return cachedPath;
        }

        var directoryDescriptor = _native.OpenByHandle(
            FanotifyConstants.CurrentWorkingDirectory,
            fileHandlePayload,
            FanotifyConstants.OpenDirectoryFlags);

        if (directoryDescriptor < 0)
            return null;

        try
        {
            var resolvedPath = _native.ReadLink($"/proc/self/fd/{directoryDescriptor}");
            if (!string.IsNullOrEmpty(resolvedPath))
            {
                lock (_handleCacheLock)
                {
                    _handleCache[key] = resolvedPath;
                }
                return resolvedPath;
            }
        }
        finally
        {
            _native.Close(directoryDescriptor);
        }

        return null;
    }

    void InvalidateCacheForPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return;

        var prefix = path.EndsWith('/') ? path : path + "/";
        lock (_handleCacheLock)
        {
            var keysToRemove = new List<FileHandleKey>();
            foreach (var (key, cachedPath) in _handleCache)
            {
                if (cachedPath.Equals(path, StringComparison.Ordinal) ||
                    cachedPath.StartsWith(prefix, StringComparison.Ordinal))
                {
                    keysToRemove.Add(key);
                }
            }

            foreach (var key in keysToRemove)
                _handleCache.Remove(key);
        }
    }

    string ResolveVolumeForPath(string fullPath)
    {
        foreach (var volume in _watchedVolumes)
        {
            if (volume == "/")
                continue;

            var prefix = volume.EndsWith('/') ? volume : volume + "/";
            if (fullPath.Equals(volume, StringComparison.Ordinal) ||
                fullPath.StartsWith(prefix, StringComparison.Ordinal))
            {
                return volume;
            }
        }

        if (_watchedVolumes.Contains("/"))
            return "/";

        return _watchedVolumes.Count > 0 ? _watchedVolumes[0] : "/";
    }

    static VolumeEntryKind ClassifyKind(ulong mask) => mask switch
    {
        _ when (mask & (FanotifyConstants.Create | FanotifyConstants.MovedTo)) != 0 => VolumeEntryKind.Created,
        _ when (mask & (FanotifyConstants.Delete | FanotifyConstants.MovedFrom)) != 0 => VolumeEntryKind.Deleted,
        _ => VolumeEntryKind.Modified
    };
}

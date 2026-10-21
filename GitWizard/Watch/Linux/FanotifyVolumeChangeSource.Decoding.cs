using System.Text;

namespace GitWizard.Watch.Linux;

public sealed partial class FanotifyVolumeChangeSource
{
    (List<VolumeChangeEntry> Entries, bool QueueOverflow) DecodeEvents(byte[] buffer, int bytesRead)
    {
        var result = new List<VolumeChangeEntry>();
        var offset = 0;
        var queueOverflow = false;
        const int metadataHeaderLength = 24;
        using var mounts = _mounts.OpenBatch();

        while (offset + metadataHeaderLength <= bytesRead)
        {
            var eventLength = (int)BitConverter.ToUInt32(buffer, offset);
            if (eventLength < metadataHeaderLength || offset + eventLength > bytesRead)
            {
                break;
            }

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
                {
                    break;
                }

                if (infoType is FanotifyConstants.EventInfoTypeDfidName
                    or FanotifyConstants.EventInfoTypeOldDfidName
                    or FanotifyConstants.EventInfoTypeNewDfidName
                    or FanotifyConstants.EventInfoTypeDfid
                    or FanotifyConstants.EventInfoTypeFid)
                {
                    var entry = DecodeFidRecord(buffer, infoOffset, infoLength, kind, mounts);
                    if (entry is not null)
                    {
                        if (shouldInvalidate)
                        {
                            InvalidateCacheForPath(entry.Value.FullPath);
                        }

                        result.Add(entry.Value);
                    }
                }

                infoOffset += infoLength;
            }

            offset += eventLength;
        }

        return (result, queueOverflow);
    }

    VolumeChangeEntry? DecodeFidRecord(
        byte[] buffer, int infoOffset, int infoLength, VolumeEntryKind kind, MountRootTable.MountBatch mounts)
    {
        if (infoLength < 20)
        {
            return null;
        }

        var handleBytes = (int)BitConverter.ToUInt32(buffer, infoOffset + 12);
        if (infoLength < 20 + handleBytes)
        {
            return null;
        }

        var filesystemId = BitConverter.ToInt64(buffer, infoOffset + 4);
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
            {
                fileName = Encoding.UTF8.GetString(buffer, nameOffset, nameLength);
            }
        }

        var directoryPath = ResolveDirectoryPath(new FileHandleKey(filesystemId, fileHandlePayload), mounts);
        if (directoryPath is null)
        {
            return null;
        }

        string fullPath;
        if (string.IsNullOrEmpty(fileName) || fileName == ".")
        {
            fullPath = directoryPath;
        }
        else if (directoryPath == "/")
        {
            fullPath = "/" + fileName;
        }
        else
        {
            fullPath = Path.Combine(directoryPath, fileName);
        }

        return new VolumeChangeEntry(fullPath, kind);
    }

    string? ResolveDirectoryPath(FileHandleKey key, MountRootTable.MountBatch mounts)
    {
        // Resolved paths carry the prefix of the mount that produced them, so the mount is settled first: a cached
        // path survives only while the filesystem is still reached through the root that resolved it.
        var reachable = mounts.TryGetDescriptor(key.FilesystemId, out var mountDescriptor, out var mountChanged);
        if (mountChanged)
        {
            RemoveCacheForFilesystem(key.FilesystemId);
        }

        if (!reachable)
        {
            return null;
        }

        lock (_handleCacheLock)
        {
            if (_handleCache.TryGetValue(key, out var cachedPath))
            {
                return cachedPath;
            }
        }

        var directoryDescriptor = _native.OpenByHandle(
            mountDescriptor,
            key.HandleBytes,
            FanotifyConstants.OpenDirectoryFlags);

        if (directoryDescriptor < 0)
        {
            return null;
        }

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

    void RemoveCacheForFilesystem(long filesystemId)
    {
        lock (_handleCacheLock)
        {
            foreach (var key in _handleCache.Keys.Where(key => key.FilesystemId == filesystemId).ToList())
            {
                _handleCache.Remove(key);
            }
        }
    }

    void InvalidateCacheForPath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

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
            {
                _handleCache.Remove(key);
            }
        }
    }

    static VolumeEntryKind ClassifyKind(ulong mask) => mask switch
    {
        _ when (mask & FanotifyConstants.MovedTo) != 0 && (mask & FanotifyConstants.OnDirectory) != 0 =>
            VolumeEntryKind.MovedIn,
        _ when (mask & (FanotifyConstants.Create | FanotifyConstants.MovedTo)) != 0 => VolumeEntryKind.Created,
        _ when (mask & (FanotifyConstants.Delete | FanotifyConstants.MovedFrom)) != 0 => VolumeEntryKind.Deleted,
        _ => VolumeEntryKind.Modified
    };
}

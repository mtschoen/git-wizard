namespace GitWizard.Watch.Linux;

/// <summary>
/// Value-equality key for caching directory paths by the filesystem identifier and Linux kernel
/// <c>struct file_handle</c> payload that fanotify reports together. Equal payloads on different
/// filesystems are different keys.
/// </summary>
readonly struct FileHandleKey : IEquatable<FileHandleKey>
{
    readonly int _hashCode;

    public FileHandleKey(long filesystemId, byte[] handleBytes)
    {
        FilesystemId = filesystemId;
        HandleBytes = handleBytes;
        var hash = new HashCode();
        hash.Add(filesystemId);
        foreach (var b in handleBytes)
        {
            hash.Add(b);
        }

        _hashCode = hash.ToHashCode();
    }

    public long FilesystemId { get; }

    public byte[] HandleBytes { get; }

    public bool Equals(FileHandleKey other) =>
        FilesystemId == other.FilesystemId && HandleBytes.AsSpan().SequenceEqual(other.HandleBytes);

    public override bool Equals(object? obj) =>
        obj is FileHandleKey other && Equals(other);

    public override int GetHashCode() => _hashCode;
}

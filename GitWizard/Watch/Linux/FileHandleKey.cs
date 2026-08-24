namespace GitWizard.Watch.Linux;

/// <summary>
/// Value-equality key for caching directory paths by their Linux kernel <c>struct file_handle</c> payload.
/// </summary>
internal readonly struct FileHandleKey : IEquatable<FileHandleKey>
{
    readonly byte[] _bytes;
    readonly int _hashCode;

    public FileHandleKey(byte[] bytes)
    {
        _bytes = bytes;
        var hash = new HashCode();
        foreach (var b in bytes)
            hash.Add(b);
        _hashCode = hash.ToHashCode();
    }

    public bool Equals(FileHandleKey other) =>
        _bytes.AsSpan().SequenceEqual(other._bytes);

    public override bool Equals(object? obj) =>
        obj is FileHandleKey other && Equals(other);

    public override int GetHashCode() => _hashCode;
}

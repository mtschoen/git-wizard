using GitWizard.Watch.Linux;

namespace GitWizardTests.TestSupport;

sealed class FakeFanotifyNative : IFanotifyNative
{
    public FakeFanotifyNative() { }

    public FakeFanotifyNative(int descriptor, params FanotifyNativeCallResult[] markResults)
        : this(new FanotifyNativeCallResult(descriptor, 0), markResults) { }

    public FakeFanotifyNative(FanotifyNativeCallResult initializeResult,
        params FanotifyNativeCallResult[] markResults)
    {
        InitializeResult = initializeResult;
        foreach (var result in markResults)
        {
            MarkResults.Enqueue(result);
        }
    }

    public FanotifyNativeCallResult InitializeResult { get; set; } = new(1, 0);
    public int InitializeCallCount { get; private set; }
    public uint InitializeFlags { get; private set; }
    public uint EventFileFlags { get; private set; }

    public Queue<FanotifyNativeCallResult> MarkResults { get; } = new();
    public List<FanotifyMarkCall> MarkCalls { get; } = [];
    public List<int> ClosedDescriptors { get; } = [];

    public int PollResult { get; init; } = 1;
    public bool ReadReturnsZero { get; init; }
    public bool ReadThrowsException { get; init; }
    public Queue<byte[]> ReadQueue { get; } = new();

    public Dictionary<FileHandleKey, int> HandleToFdMap { get; } = new();
    public Dictionary<int, string> FdToPathMap { get; } = new();

    public FanotifyNativeCallResult Initialize(uint flags, uint eventFileFlags)
    {
        InitializeCallCount++;
        InitializeFlags = flags;
        EventFileFlags = eventFileFlags;
        return InitializeResult;
    }

    public FanotifyNativeCallResult Mark(
        int descriptor,
        uint flags,
        ulong mask,
        int directoryDescriptor,
        string path)
    {
        MarkCalls.Add(new FanotifyMarkCall(descriptor, flags, mask, directoryDescriptor, path));
        return MarkResults.Count > 0 ? MarkResults.Dequeue() : new FanotifyNativeCallResult(0, 0);
    }

    public int Close(int descriptor)
    {
        ClosedDescriptors.Add(descriptor);
        OpenMountDescriptors.Remove(descriptor);
        return 0;
    }

    public int Poll(int descriptor, int timeoutMilliseconds)
    {
        if (PollResult < 0)
        {
            return PollResult;
        }

        if (ReadThrowsException || ReadReturnsZero)
        {
            return 1;
        }

        return ReadQueue.Count > 0 ? 1 : 0;
    }

    public int Read(int descriptor, byte[] buffer, int count)
    {
        if (ReadThrowsException)
        {
            throw new InvalidOperationException("Simulated read exception");
        }

        if (ReadReturnsZero)
        {
            return 0;
        }

        if (ReadQueue.Count == 0)
        {
            return 0;
        }

        var source = ReadQueue.Dequeue();
        var bytesToCopy = Math.Min(source.Length, count);
        Buffer.BlockCopy(source, 0, buffer, 0, bytesToCopy);
        return bytesToCopy;
    }

    public Dictionary<string, long> FilesystemIdByMountPath { get; } = new(StringComparer.Ordinal);
    public HashSet<string> UnopenableMountPaths { get; } = new(StringComparer.Ordinal);
    public HashSet<string> UnreadableIdentifierMountPaths { get; } = new(StringComparer.Ordinal);
    /// <summary>Roots whose mount was detached: opening the path now reaches the underlying filesystem.</summary>
    public HashSet<string> GoneMountPaths { get; } = new(StringComparer.Ordinal);
    public List<(string Path, int Descriptor)> OpenedDirectories { get; } = [];
    public List<(int MountDescriptor, byte[] Handle)> OpenByHandleCalls { get; } = [];
    /// <summary>Descriptors opened through <see cref="OpenDirectory"/> and not yet closed.</summary>
    public HashSet<int> OpenMountDescriptors { get; } = [];
    /// <summary>Paths of the roots each <see cref="OpenByHandle"/> call resolved through, in call order.</summary>
    public List<string> OpenByHandleRoots { get; } = [];
    readonly Dictionary<int, string> _rootByDescriptor = new();
    readonly Dictionary<int, string> _relativePathByDirectoryDescriptor = new();
    readonly Dictionary<int, string> _rootByDirectoryDescriptor = new();
    readonly Dictionary<int, long> _filesystemIdByDescriptor = new();
    int _nextMountDescriptor = 1000;
    const long UnderlyingFilesystemId = 99;

    public void MapHandle(long filesystemId, byte[] handlePayload, int descriptor, string path)
    {
        HandleToFdMap[new FileHandleKey(filesystemId, handlePayload)] = descriptor;
        FdToPathMap[descriptor] = path;
    }

    /// <summary>Maps a handle to a directory whose resolved path is the resolving root plus <paramref name="relativePath"/>.</summary>
    public void MapHandleUnderRoot(long filesystemId, byte[] handlePayload, int descriptor, string relativePath)
    {
        HandleToFdMap[new FileHandleKey(filesystemId, handlePayload)] = descriptor;
        _relativePathByDirectoryDescriptor[descriptor] = relativePath;
    }

    public FanotifyNativeCallResult OpenDirectory(string path)
    {
        if (UnopenableMountPaths.Contains(path) || !Directory.Exists(path))
        {
            return new FanotifyNativeCallResult(-1, 2);
        }

        var descriptor = _nextMountDescriptor++;
        if (!UnreadableIdentifierMountPaths.Contains(path))
        {
            _filesystemIdByDescriptor[descriptor] = GoneMountPaths.Contains(path)
                ? UnderlyingFilesystemId
                : FilesystemIdByMountPath.GetValueOrDefault(path);
        }

        OpenedDirectories.Add((path, descriptor));
        OpenMountDescriptors.Add(descriptor);
        _rootByDescriptor[descriptor] = path;
        return new FanotifyNativeCallResult(descriptor, 0);
    }

    public bool TryReadFilesystemIdentifier(int descriptor, out long filesystemId) =>
        _filesystemIdByDescriptor.TryGetValue(descriptor, out filesystemId);

    public int OpenByHandle(int mountDirectoryDescriptor, byte[] handle, int flags)
    {
        OpenByHandleCalls.Add((mountDirectoryDescriptor, handle));
        OpenByHandleRoots.Add(_rootByDescriptor[mountDirectoryDescriptor]);
        if (!_filesystemIdByDescriptor.TryGetValue(mountDirectoryDescriptor, out var filesystemId))
        {
            return -1;
        }

        var key = new FileHandleKey(filesystemId, handle);
        if (!HandleToFdMap.TryGetValue(key, out var fd))
        {
            return -1;
        }

        _rootByDirectoryDescriptor[fd] = _rootByDescriptor[mountDirectoryDescriptor];
        return fd;
    }

    public string? ReadLink(string path)
    {
        if (!path.StartsWith("/proc/self/fd/", StringComparison.Ordinal)
            || !int.TryParse(path["/proc/self/fd/".Length..], out var fd))
        {
            return null;
        }

        return _relativePathByDirectoryDescriptor.TryGetValue(fd, out var relativePath)
            ? Path.Combine(_rootByDirectoryDescriptor[fd], relativePath)
            : FdToPathMap.GetValueOrDefault(fd);
    }
}

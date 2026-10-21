using GitWizard.Watch.Linux;

namespace GitWizardTests.Watch.Linux;

internal sealed class FakeFanotifyNative : IFanotifyNative
{
    readonly Queue<FanotifyNativeCallResult> _markResults;
    readonly FanotifyNativeCallResult _initializeResult;

    public FakeFanotifyNative(int descriptor, params FanotifyNativeCallResult[] markResults)
        : this(new FanotifyNativeCallResult(descriptor, 0), markResults)
    {
    }

    public FakeFanotifyNative(
        FanotifyNativeCallResult initializeResult,
        params FanotifyNativeCallResult[] markResults)
    {
        _initializeResult = initializeResult;
        _markResults = new Queue<FanotifyNativeCallResult>(markResults);
    }

    public int InitializeCallCount { get; private set; }
    public uint InitializeFlags { get; private set; }
    public uint EventFileFlags { get; private set; }
    public List<FanotifyMarkCall> MarkCalls { get; } = new();
    public List<int> ClosedDescriptors { get; } = new();

    public FanotifyNativeCallResult Initialize(uint flags, uint eventFileFlags)
    {
        InitializeCallCount++;
        InitializeFlags = flags;
        EventFileFlags = eventFileFlags;
        return _initializeResult;
    }

    public FanotifyNativeCallResult Mark(
        int descriptor,
        uint flags,
        ulong mask,
        int directoryDescriptor,
        string path)
    {
        MarkCalls.Add(new FanotifyMarkCall(descriptor, flags, mask, directoryDescriptor, path));
        return _markResults.Count == 0 ? new FanotifyNativeCallResult(0, 0) : _markResults.Dequeue();
    }

    public int Close(int descriptor)
    {
        ClosedDescriptors.Add(descriptor);
        return 0;
    }

    public int Poll(int descriptor, int timeoutMilliseconds) => 0;

    public int Read(int descriptor, byte[] buffer, int count) => 0;

    public int OpenByHandle(int mountDirectoryDescriptor, byte[] handle, int flags) => -1;

    public FanotifyNativeCallResult OpenDirectory(string path) => new(-1, 2);

    public bool TryReadFilesystemIdentifier(int descriptor, out long filesystemId)
    {
        filesystemId = 0;
        return false;
    }

    public string? ReadLink(string path) => null;
}

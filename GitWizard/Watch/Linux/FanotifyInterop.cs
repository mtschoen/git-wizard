using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using JetBrains.Annotations;
using Microsoft.Win32.SafeHandles;

namespace GitWizard.Watch.Linux;

public enum FanotifyCapabilityStatus
{
    Available = 0,
    RequiresElevation = 1,
    UnsupportedKernel = 2,
    UnsupportedFileSystem = 3
}

public readonly record struct FanotifyCapabilityResult(
    FanotifyCapabilityStatus Status,
    [property: UsedImplicitly] int ErrorNumber)
{
    public bool IsSupported => Status is
        FanotifyCapabilityStatus.Available or FanotifyCapabilityStatus.RequiresElevation;

    public bool RequiresElevation => Status == FanotifyCapabilityStatus.RequiresElevation;
}

/// <summary>
/// Linux fanotify interop and capability probing for filesystem marks that report directory
/// file identifiers and names. <c>FAN_REPORT_DFID_NAME</c> requires Linux 5.9 or newer.
/// </summary>
public static class FanotifyInterop
{
    public static Version MinimumKernelVersion { get; } = new(5, 9);

    /// <summary>
    /// Probes fanotify support for the filesystem containing the existing directory
    /// identified by <paramref name="path"/>. A result requiring elevation remains supported
    /// so callers can offer a pkexec path.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="path"/> is blank or does not identify an existing directory.
    /// </exception>
    [SupportedOSPlatform("linux")]
    public static FanotifyCapabilityResult Probe(string path = "/")
    {
        return new FanotifyProbe(FanotifyNative.Instance).Probe(path);
    }
}

sealed class FanotifyProbe(IFanotifyNative native)
{
    public FanotifyCapabilityResult Probe(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Directory.Exists(path))
        {
            throw new ArgumentException("Path must identify an existing directory.", nameof(path));
        }

        var initializeResult = native.Initialize(
            FanotifyConstants.CloseOnExecute | FanotifyConstants.ReportDirectoryFileIdAndName,
            FanotifyConstants.ReadOnly);
        if (initializeResult.Value < 0)
        {
            return ClassifyFailure(initializeResult.ErrorNumber);
        }

        using var descriptor = new SafeUnixFileDescriptor(initializeResult.Value, native);
        var addResult = Mark(initializeResult.Value, FanotifyConstants.MarkAdd, path);
        if (addResult.Value < 0)
        {
            return ClassifyFailure(addResult.ErrorNumber);
        }

        var removeResult = Mark(initializeResult.Value, FanotifyConstants.MarkRemove, path);
        return removeResult.Value < 0
            ? ClassifyFailure(removeResult.ErrorNumber)
            : new FanotifyCapabilityResult(FanotifyCapabilityStatus.Available, 0);
    }

    FanotifyNativeCallResult Mark(int descriptor, uint operation, string path)
    {
        return native.Mark(
            descriptor,
            operation | FanotifyConstants.MarkFilesystem,
            FanotifyConstants.ProbeEventMask,
            FanotifyConstants.CurrentWorkingDirectory,
            path);
    }

    static FanotifyCapabilityResult ClassifyFailure(int errorNumber)
    {
        return errorNumber switch
        {
            FanotifyError.NotPermitted => new FanotifyCapabilityResult(
                FanotifyCapabilityStatus.RequiresElevation,
                errorNumber),
            FanotifyError.NoSystemCall or FanotifyError.InvalidArgument => new FanotifyCapabilityResult(
                FanotifyCapabilityStatus.UnsupportedKernel,
                errorNumber),
            FanotifyError.NoDevice or FanotifyError.OperationNotSupported => new FanotifyCapabilityResult(
                FanotifyCapabilityStatus.UnsupportedFileSystem,
                errorNumber),
            _ => throw new Win32Exception(errorNumber, "fanotify capability probe failed.")
        };
    }
}

sealed class SafeUnixFileDescriptor : SafeHandleMinusOneIsInvalid
{
    readonly IFanotifyNative _native;

    public SafeUnixFileDescriptor(int descriptor, IFanotifyNative native)
        : base(true)
    {
        _native = native;
        SetHandle(descriptor);
    }

    public int Value => handle.ToInt32();

    protected override bool ReleaseHandle()
    {
        return _native.Close(handle.ToInt32()) == 0;
    }
}

interface IFanotifyNative
{
    FanotifyNativeCallResult Initialize(uint flags, uint eventFileFlags);

    FanotifyNativeCallResult Mark(
        int descriptor,
        uint flags,
        ulong mask,
        int directoryDescriptor,
        string path);

    int Close(int descriptor);

    int Poll(int descriptor, int timeoutMilliseconds);

    int Read(int descriptor, byte[] buffer, int count);

    int OpenByHandle(int mountDirectoryDescriptor, byte[] handle, int flags);

    FanotifyNativeCallResult OpenDirectory(string path);

    bool TryReadFilesystemIdentifier(int descriptor, out long filesystemId);

    string? ReadLink(string path);
}

readonly record struct FanotifyNativeCallResult(int Value, int ErrorNumber);

[SupportedOSPlatform("linux")]
sealed partial class FanotifyNative : IFanotifyNative
{
    public static FanotifyNative Instance { get; } = new();

    FanotifyNative()
    {
    }

    public FanotifyNativeCallResult Initialize(uint flags, uint eventFileFlags)
    {
        return CaptureResult(FanotifyInitialize(flags, eventFileFlags));
    }

    public FanotifyNativeCallResult Mark(
        int descriptor,
        uint flags,
        ulong mask,
        int directoryDescriptor,
        string path)
    {
        return CaptureResult(FanotifyMark(descriptor, flags, mask, directoryDescriptor, path));
    }

    public int Close(int descriptor)
    {
        return CloseDescriptor(descriptor);
    }

    public int Poll(int descriptor, int timeoutMilliseconds)
    {
        unsafe
        {
            var pollFd = new PollFd
            {
                Fd = descriptor,
                Events = 0x0001,
                Revents = 0
            };
            return NativePoll(&pollFd, 1, timeoutMilliseconds);
        }
    }

    public int Read(int descriptor, byte[] buffer, int count)
    {
        unsafe
        {
            fixed (byte* ptr = buffer)
            {
                return (int)NativeRead(descriptor, ptr, (nuint)count);
            }
        }
    }

    public int OpenByHandle(int mountDirectoryDescriptor, byte[] handle, int flags)
    {
        unsafe
        {
            fixed (byte* ptr = handle)
            {
                return NativeOpenByHandleAt(mountDirectoryDescriptor, ptr, flags);
            }
        }
    }

    public FanotifyNativeCallResult OpenDirectory(string path)
    {
        return CaptureResult(NativeOpen(path, FanotifyConstants.MountDescriptorFlags));
    }

    public bool TryReadFilesystemIdentifier(int descriptor, out long filesystemId)
    {
        // The statfs layout below is the 64-bit Linux one (x86-64 and arm64), where f_fsid follows seven 8-byte fields.
        filesystemId = 0;
        if (nint.Size != 8)
        {
            return false;
        }

        unsafe
        {
            var buffer = stackalloc byte[FanotifyConstants.StatfsBufferSize];
            if (NativeFilesystemStatistics(descriptor, buffer) != 0)
            {
                return false;
            }

            filesystemId = Unsafe.ReadUnaligned<long>(buffer + FanotifyConstants.StatfsFilesystemIdOffset);
            return true;
        }
    }

    public string? ReadLink(string path)
    {
        unsafe
        {
            const int initialSize = 1024;
            var buffer = stackalloc byte[initialSize];
            var result = NativeReadLink(path, buffer, initialSize);
            switch (result)
            {
                case <= 0:
                    return null;
                case < initialSize:
                    return System.Text.Encoding.UTF8.GetString(buffer, (int)result);
            }

            var bufferSize = 4096;
            while (bufferSize <= 65536)
            {
                var rented = new byte[bufferSize];
                fixed (byte* pointer = rented)
                {
                    result = NativeReadLink(path, pointer, (nuint)bufferSize);
                    if (result <= 0)
                    {
                        return null;
                    }

                    if (result < bufferSize)
                    {
                        return System.Text.Encoding.UTF8.GetString(pointer, (int)result);
                    }
                }

                bufferSize *= 2;
            }

            return null;
        }
    }

    static FanotifyNativeCallResult CaptureResult(int value)
    {
        return new FanotifyNativeCallResult(value, value < 0 ? Marshal.GetLastPInvokeError() : 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    [LibraryImport("libc", EntryPoint = "fanotify_init", SetLastError = true)]
    private static partial int FanotifyInitialize(uint flags, uint eventFileFlags);

    [LibraryImport(
        "libc",
        EntryPoint = "fanotify_mark",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf8)]
    private static partial int FanotifyMark(
        int fanotifyDescriptor,
        uint flags,
        ulong mask,
        int directoryDescriptor,
        string path);

    [LibraryImport("libc", EntryPoint = "close")]
    private static partial int CloseDescriptor(int descriptor);

    [LibraryImport("libc", EntryPoint = "poll", SetLastError = true)]
    private static unsafe partial int NativePoll(PollFd* fds, nuint nfds, int timeout);

    [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
    private static unsafe partial nint NativeRead(int descriptor, byte* buffer, nuint count);

    [LibraryImport("libc", EntryPoint = "open_by_handle_at", SetLastError = true)]
    private static unsafe partial int NativeOpenByHandleAt(int mountDescriptor, byte* handle, int flags);

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int NativeOpen(string path, int flags);

    [LibraryImport("libc", EntryPoint = "fstatfs", SetLastError = true)]
    private static unsafe partial int NativeFilesystemStatistics(int descriptor, byte* buffer);

    [LibraryImport("libc", EntryPoint = "readlink", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial nint NativeReadLink(string path, byte* buffer, nuint bufferSize);
}

static class FanotifyConstants
{
    public const uint CloseOnExecute = 0x00000001;
    public const uint ReadOnly = 0;
    const uint ReportDirectoryFileId = 0x00000400;
    const uint ReportName = 0x00000800;
    public const uint ReportDirectoryFileIdAndName = ReportDirectoryFileId | ReportName;

    public const uint MarkAdd = 0x00000001;
    public const uint MarkRemove = 0x00000002;
    public const uint MarkFilesystem = 0x00000100;

    public const ulong Modify = 0x00000002;
    public const ulong MovedFrom = 0x00000040;
    public const ulong MovedTo = 0x00000080;
    public const ulong Create = 0x00000100;
    public const ulong Delete = 0x00000200;
    public const ulong QueueOverflow = 0x00004000;
    public const ulong OnDirectory = 0x40000000;
    public const ulong ProbeEventMask = Modify | MovedFrom | MovedTo | Create | Delete | OnDirectory;

    public const int CurrentWorkingDirectory = -100;

    public const byte EventInfoTypeFid = 1;
    public const byte EventInfoTypeDfidName = 2;
    public const byte EventInfoTypeDfid = 3;
    public const byte EventInfoTypeOldDfidName = 10;
    public const byte EventInfoTypeNewDfidName = 12;

    public const int StatfsBufferSize = 256;
    public const int StatfsFilesystemIdOffset = 56;

    const int OpenPath = 0x00200000;
    const int OpenDirectory = 0x00010000;
    const int OpenCloseOnExecute = 0x00080000;
    public const int OpenDirectoryFlags = (int)ReadOnly | OpenDirectory | OpenCloseOnExecute | OpenPath;

    // open_by_handle_at rejects an O_PATH mount descriptor with EBADF, so the descriptor that anchors handle
    // resolution is a plain read-only directory descriptor, while the directories it resolves stay O_PATH.
    public const int MountDescriptorFlags = (int)ReadOnly | OpenDirectory | OpenCloseOnExecute;
}

static class FanotifyError
{
    public const int NotPermitted = 1;
    public const int NoDevice = 19;
    public const int InvalidArgument = 22;
    public const int NoSystemCall = 38;
    public const int OperationNotSupported = 95;
}

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace GitWizard.CLI;

/// <summary>
/// Reassigns a directory tree to one user with descriptor-relative calls, because the caller is root
/// and the tree is writable by that user: a path-based chown could be steered through a symlink the
/// user swaps in mid-walk. The root rejects every symlink component and entries are opened without
/// following links. Only singly linked regular files and directories are reassigned, through the
/// same descriptor whose metadata was checked.
/// </summary>
internal static partial class LinuxOwnershipRepair
{
    // O_DIRECTORY and O_NOFOLLOW differ between the x86 and ARM kernel ABIs.
    static readonly bool ArmFlagValues = RuntimeInformation.ProcessArchitecture is Architecture.Arm or Architecture.Arm64;
    static readonly int OpenDirectoryFlags =
        (ArmFlagValues ? 0x4000 : 0x10000) | (ArmFlagValues ? 0x8000 : 0x20000) | 0x80000;
    static readonly int OpenEntryFlags = (ArmFlagValues ? 0x8000 : 0x20000) | 0x800 | 0x80000;
    const int AtCurrentWorkingDirectory = -100;
    const uint FileTypeMask = 0xF000;
    internal const uint RegularFile = 0x8000;
    const uint DirectoryEntryType = 0x4000;
    const uint Unchanged = uint.MaxValue;

    /// <summary>Changes one entry of an open directory and returns whether it succeeded.</summary>
    internal delegate bool ReassignEntry(int directoryDescriptor, string name, string relativePath);
    internal delegate int GetFileStatus(int descriptor, out FileStatus status);

    /// <summary>Gives <paramref name="folder"/> and everything below it to the user; returns the failure count.</summary>
    [SupportedOSPlatform("linux")]
    internal static int Reassign(string folder, uint userId)
    {
        var groupId = PrimaryGroupOf(userId);
        return Walk(
            folder,
            (directoryDescriptor, name, _) => ReassignOpenedEntry(directoryDescriptor, name, userId, groupId),
            descriptor => ReassignRoot(descriptor, userId, groupId));
    }

    [SupportedOSPlatform("linux")]
    internal static int Walk(string folder, ReassignEntry reassignEntry, Func<int, bool> reassignRoot) =>
        Walk(folder, reassignEntry, reassignRoot, (OpenRoot, OpenAt, Close, Directory.EnumerateFileSystemEntries));

    internal static int Walk(
        string folder,
        ReassignEntry reassignEntry,
        Func<int, bool> reassignRoot,
        (Func<string, int, int> Open,
            Func<int, string, int, int> OpenAt,
            Action<int> Close,
            Func<string, IEnumerable<string>> EnumerateEntries) native)
    {
        var rootDescriptor = native.Open(folder, OpenDirectoryFlags);
        if (rootDescriptor < 0)
        {
            // Absent, or a symlink in any component: nothing this walk may safely touch.
            return 0;
        }

        try
        {
            var failures = reassignRoot(rootDescriptor) ? 0 : 1;
            return failures + WalkDirectory(rootDescriptor, string.Empty, reassignEntry, native);
        }
        finally
        {
            native.Close(rootDescriptor);
        }
    }

    static int WalkDirectory(
        int directoryDescriptor,
        string relativePrefix,
        ReassignEntry reassignEntry,
        (Func<string, int, int> Open,
            Func<int, string, int, int> OpenAt,
            Action<int> Close,
            Func<string, IEnumerable<string>> EnumerateEntries) native)
    {
        var failures = 0;
        var names = native.EnumerateEntries($"/proc/self/fd/{directoryDescriptor}")
            .Select(Path.GetFileName)
            .OfType<string>()
            .ToList();
        foreach (var name in names)
        {
            var relativePath = Path.Combine(relativePrefix, name);
            if (!reassignEntry(directoryDescriptor, name, relativePath))
            {
                failures++;
            }

            var childDescriptor = native.OpenAt(directoryDescriptor, name, OpenDirectoryFlags);
            if (childDescriptor < 0)
            {
                continue;
            }

            try
            {
                failures += WalkDirectory(childDescriptor, relativePath, reassignEntry, native);
            }
            finally
            {
                native.Close(childDescriptor);
            }
        }

        return failures;
    }

    static int OpenRoot(string path, int flags) => OpenRoot(path, flags, (OpenAt, Close));

    internal static int OpenRoot(
        string path,
        int flags,
        (Func<int, string, int, int> OpenAt, Action<int> Close) native)
    {
        var descriptor = native.OpenAt(AtCurrentWorkingDirectory, path.StartsWith('/') ? "/" : ".", flags);
        foreach (var component in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var childDescriptor = descriptor < 0 ? -1 : native.OpenAt(descriptor, component, flags);
            if (descriptor >= 0)
            {
                native.Close(descriptor);
            }

            descriptor = childDescriptor;
        }

        return descriptor;
    }

    static bool ReassignOpenedEntry(int directoryDescriptor, string name, uint userId, uint groupId)
    {
        var descriptor = OpenAt(directoryDescriptor, name, OpenEntryFlags);
        if (descriptor < 0)
        {
            return true;
        }

        try
        {
            return ReassignOpened(descriptor, userId, groupId);
        }
        finally
        {
            Close(descriptor);
        }
    }

    static bool ReassignRoot(int descriptor, uint userId, uint groupId) =>
        ReassignOpened(descriptor, userId, groupId);

    static bool ReassignOpened(int descriptor, uint userId, uint groupId) =>
        ReassignOpened(descriptor, userId, groupId, (GetStatus, ChangeOwner));

    internal static bool ReassignOpened(
        int descriptor,
        uint userId,
        uint groupId,
        (GetFileStatus GetStatus, Func<int, uint, uint, int> ChangeOwner) native)
    {
        if (native.GetStatus(descriptor, out var status) != 0 ||
            ((status.Mode & FileTypeMask) is not (RegularFile or DirectoryEntryType)) ||
            ((status.Mode & FileTypeMask) == RegularFile && status.LinkCount != 1) ||
            (status.UserId == userId && (groupId == Unchanged || status.GroupId == groupId)))
        {
            return true;
        }

        return native.ChangeOwner(descriptor, userId, groupId) == 0;
    }

    static uint PrimaryGroupOf(uint userId)
    {
        var entry = GetPasswordEntry(userId);
        return entry == IntPtr.Zero ? Unchanged : Marshal.PtrToStructure<PasswordEntry>(entry).GroupId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileStatus
    {
        public ulong Device;
        public ulong Inode;
        public ulong LinkCount;
        public uint Mode;
        public uint UserId;
        public uint GroupId;
        public int Padding;
        public ulong SpecialDevice;
        public long Size;
        public long BlockSize;
        public long BlockCount;
        public long AccessSeconds;
        public long AccessNanoseconds;
        public long ModificationSeconds;
        public long ModificationNanoseconds;
        public long ChangeSeconds;
        public long ChangeNanoseconds;
        public long Reserved0;
        public long Reserved1;
        public long Reserved2;
    }

    // Leading fields of struct passwd: name and password pointers, then uid and gid.
    [StructLayout(LayoutKind.Sequential)]
    struct PasswordEntry
    {
        public IntPtr Name;
        public IntPtr Password;
        public uint UserId;
        public uint GroupId;
    }

    [LibraryImport("libc", EntryPoint = "openat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenAt(int directoryDescriptor, string name, int flags);

    [LibraryImport("libc", EntryPoint = "close")]
    private static partial void Close(int descriptor);

    [LibraryImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static partial int GetStatus(int descriptor, out FileStatus status);

    [LibraryImport("libc", EntryPoint = "fchown", SetLastError = true)]
    private static partial int ChangeOwner(int descriptor, uint userId, uint groupId);

    [LibraryImport("libc", EntryPoint = "getpwuid")]
    private static partial IntPtr GetPasswordEntry(uint userId);
}

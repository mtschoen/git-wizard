using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MFTLib.Index;

namespace GitWizard;

public static partial class WindowsRootClassifier
{
    public static RootClassification Classify(string root)
    {
        return !OperatingSystem.IsWindows() ? new RootClassification(root, RootFileSystem.Recursive, null, null) : ClassifyWindows(root);
    }

    [SupportedOSPlatform("windows")]
    static RootClassification ClassifyWindows(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return new RootClassification(root, RootFileSystem.Unknown, null, "Path is empty");
        }

        if (root.StartsWith(@"\\", StringComparison.Ordinal) || root.StartsWith("//", StringComparison.Ordinal))
        {
            return new RootClassification(root, RootFileSystem.Recursive, null, null);
        }

        var volumePathBuffer = new char[512];
        if (!GetVolumePathName(root, volumePathBuffer, (uint)volumePathBuffer.Length))
        {
            var error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return new RootClassification(root, RootFileSystem.Unknown, null, error);
        }

        var volumePath = new string(volumePathBuffer).TrimEnd('\0');
        var nullIndex = volumePath.IndexOf('\0');
        if (nullIndex >= 0)
        {
            volumePath = volumePath[..nullIndex];
        }

        var driveType = GetDriveType(volumePath);
        if (driveType == 4) // DRIVE_REMOTE
        {
            return new RootClassification(root, RootFileSystem.Recursive, null, null);
        }

        var fsNameBuffer = new char[256];
        if (!GetVolumeInformation(volumePath, null, 0, out _, out _, out _, fsNameBuffer, (uint)fsNameBuffer.Length))
        {
            var error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return new RootClassification(root, RootFileSystem.Unknown, null, error);
        }

        var fileSystemName = new string(fsNameBuffer).TrimEnd('\0');
        nullIndex = fileSystemName.IndexOf('\0');
        if (nullIndex >= 0)
        {
            fileSystemName = fileSystemName[..nullIndex];
        }

        if (string.Equals(fileSystemName, "NTFS", StringComparison.OrdinalIgnoreCase))
        {
            string? selectedDrive = null;
            if (volumePath.Length >= 2 && volumePath[1] == ':' && char.IsAsciiLetter(volumePath[0]))
            {
                selectedDrive = char.ToUpperInvariant(volumePath[0]).ToString();
            }

            if (selectedDrive is null)
            {
                return new RootClassification(root, RootFileSystem.Unknown, null, "Unsupported volume identity without drive letter");
            }

            try
            {
                _ = IndexedDrive.FromWindowsVolume(selectedDrive[0]);
                return new RootClassification(root, RootFileSystem.Ntfs, selectedDrive, null);
            }
            catch (Exception ex)
            {
                return new RootClassification(root, RootFileSystem.Unknown, null, $"Unsupported volume identity: {ex.Message}");
            }
        }

        return new RootClassification(root, RootFileSystem.Recursive, null, null);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetVolumePathName(
        string lpszFileName,
        [Out] char[] lpszVolumePathName,
        uint cchBufferLength);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDriveTypeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetDriveType(string lpRootPathName);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetVolumeInformation(
        string lpRootPathName,
        [Out] char[]? lpVolumeNameBuffer,
        uint nVolumeNameSize,
        out uint lpVolumeSerialNumber,
        out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags,
        [Out] char[] lpFileSystemNameBuffer,
        uint nFileSystemNameSize);
}

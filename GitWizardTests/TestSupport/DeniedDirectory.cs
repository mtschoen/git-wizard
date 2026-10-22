using System.Security.AccessControl;
using System.Security.Principal;

namespace GitWizardTests.TestSupport;

/// <summary>Makes a directory unreadable for the current user until disposed, or ignores the test if it cannot.</summary>
sealed class DeniedDirectory : IDisposable
{
    readonly Action _restore;

    DeniedDirectory(Action restore) => _restore = restore;

    public static DeniedDirectory Deny(string path)
    {
        if (OperatingSystem.IsWindows())
            return DenyWindows(path);

        return DenyUnix(path);
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    static DeniedDirectory DenyUnix(string path)
    {
        var original = File.GetUnixFileMode(path);
        File.SetUnixFileMode(path, UnixFileMode.None);
        try
        {
            Directory.GetFileSystemEntries(path);
        }
        catch (UnauthorizedAccessException)
        {
            return new DeniedDirectory(() => File.SetUnixFileMode(path, original));
        }

        File.SetUnixFileMode(path, original);
        Assert.Ignore("This process can read a mode-000 directory (for example it runs as root).");
        throw new InvalidOperationException("Unreachable.");
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static DeniedDirectory DenyWindows(string path)
    {
        var directory = new DirectoryInfo(path);
        var user = WindowsIdentity.GetCurrent().User!;
        var rule = new FileSystemAccessRule(user, FileSystemRights.ListDirectory | FileSystemRights.ReadData,
            AccessControlType.Deny);
        var security = directory.GetAccessControl();
        security.AddAccessRule(rule);
        directory.SetAccessControl(security);
        return new DeniedDirectory(() =>
        {
            var restored = directory.GetAccessControl();
            restored.RemoveAccessRule(rule);
            directory.SetAccessControl(restored);
        });
    }

    public void Dispose() => _restore();
}

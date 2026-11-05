using System.Security.AccessControl;
using System.Security.Principal;

namespace GitWizardTests.TestSupport;

/// <summary>
/// Makes a directory unlistable for the current process until disposed. A root process ignores the test on Unix;
/// on Windows a directory still listable after the deny fails the test as a broken precondition.
/// </summary>
sealed class DeniedDirectory : IDisposable
{
    readonly Action _restore;

    DeniedDirectory(Action restore) => _restore = restore;

    public static DeniedDirectory Deny(string path)
    {
        return OperatingSystem.IsWindows() ? DenyWindows(path) : DenyUnix(path);
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

    // .NET opens directories for enumeration with FILE_FLAG_BACKUP_SEMANTICS, so an enabled SeBackupPrivilege
    // grants the listing whatever the DACL says. An elevated token holds the privilege disabled, and the MSYS2
    // runtime behind Git Bash enables it at startup in a token its native children inherit, so the deny rule
    // alone does not deny an elevated suite launched from Git Bash. The privilege stays disabled until disposal.
    internal const string BackupPrivilege = "SeBackupPrivilege";

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
        var privilege = ProcessPrivilege.Set(BackupPrivilege, enabled: false);
        var denied = new DeniedDirectory(() =>
        {
            // The privilege is process-wide, so a directory that vanished before cleanup still restores it.
            try
            {
                var restored = directory.GetAccessControl();
                restored.RemoveAccessRule(rule);
                directory.SetAccessControl(restored);
            }
            finally
            {
                privilege?.Dispose();
            }
        });

        try
        {
            Directory.GetFileSystemEntries(path);
        }
        catch (UnauthorizedAccessException)
        {
            return denied;
        }

        denied.Dispose();
        Assert.Fail($"{path} is still listable after denying {user} ListDirectory and disabling {BackupPrivilege}.");
        throw new InvalidOperationException("Unreachable.");
    }

    public void Dispose() => _restore();
}

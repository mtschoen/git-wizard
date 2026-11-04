using System.Runtime.Versioning;

namespace GitWizardTests.TestSupport;

// Both tests change the process token, which every concurrently running test shares.
[TestFixture]
[NonParallelizable]
[Platform("Win")]
[SupportedOSPlatform("windows")]
public class DeniedDirectoryTests
{
    // Held, and disabled by default, in elevated and unelevated tokens alike.
    const string HeldPrivilege = "SeShutdownPrivilege";

    [Test]
    public void ProcessPrivilege_RestoresThePreviousStateOnDispose()
    {
        var original = ProcessPrivilege.IsEnabled(HeldPrivilege);
        if (original is not { } before)
        {
            Assert.Inconclusive($"The process token does not hold {HeldPrivilege}.");
            return;
        }

        using (ProcessPrivilege.Set(HeldPrivilege, !before))
        {
            Assert.That(ProcessPrivilege.IsEnabled(HeldPrivilege), Is.EqualTo(!before));
        }

        Assert.That(ProcessPrivilege.IsEnabled(HeldPrivilege), Is.EqualTo(before));
    }

    // Issue 502: an elevated suite launched from Git Bash inherits an enabled SeBackupPrivilege from the MSYS2
    // runtime, and the deny rule alone then left the directory listable.
    [Test]
    public void Deny_KeepsTheDirectoryUnlistable_WhenBackupPrivilegeIsEnabled()
    {
        using var temporary = new TempDirectory("GitWizardTests_Denied_");
        var denied = Path.Combine(temporary.Path, "denied");
        Directory.CreateDirectory(Path.Combine(denied, "inner"));

        using var backup = ProcessPrivilege.Set(DeniedDirectory.BackupPrivilege, enabled: true);
        if (backup is null)
        {
            Assert.Inconclusive($"Requires an elevated token that holds {DeniedDirectory.BackupPrivilege}.");
            return;
        }

        using (DeniedDirectory.Deny(denied))
        {
            Assert.That(() => Directory.GetFileSystemEntries(denied), Throws.TypeOf<UnauthorizedAccessException>());
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ProcessPrivilege.IsEnabled(DeniedDirectory.BackupPrivilege), Is.True);
            Assert.That(Directory.GetFileSystemEntries(denied), Has.Length.EqualTo(1));
        }
    }
}

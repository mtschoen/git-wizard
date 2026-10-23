using GitWizard;
using GitWizardTests.TestSupport;

namespace GitWizardTests;

public class TempDirectoriesTests
{
    [Test]
    public void IsTempPath_UnderOtherProfilesWindowsTemp_IsTrue()
    {
        Assert.That(TempDirectories.IsTempPath(
            @"C:\Users\example\AppData\Local\Temp\pytest-of-example\pytest-12602\repo"), Is.True);
    }

    [Test]
    public void IsTempPath_ExactlyOtherProfilesWindowsTemp_IsTrue()
    {
        Assert.That(TempDirectories.IsTempPath(@"D:\Users\someone\AppData\Local\Temp"), Is.True);
    }

    [Test]
    public void IsTempPath_WindowsTempPatternIsCaseInsensitive()
    {
        Assert.That(TempDirectories.IsTempPath(@"c:\uSeRs\eXaMpLe\APPDATA\LOCAL\TEMP\repo"), Is.True);
    }

    [Test]
    public void IsTempPath_UserProfileOutsideTemp_IsFalse()
    {
        Assert.That(TempDirectories.IsTempPath(@"C:\Users\example\AppData\Local\Programs\repo"), Is.False);
        Assert.That(TempDirectories.IsTempPath(@"C:\Users\example\repos\repo"), Is.False);
    }

    [Test]
    public void IsTempPath_OrdinaryWindowsPath_IsFalse()
    {
        Assert.That(TempDirectories.IsTempPath(@"C:\repos\repo-a"), Is.False);
    }

    [Test]
    public void IsTempPath_UncPathWithTempLikeSegments_IsFalse()
    {
        // The per-profile pattern is anchored to a drive letter, per the owner decision
        // (<drive>:\Users\<name>\AppData\Local\Temp).
        Assert.That(TempDirectories.IsTempPath(@"\\server\share\Users\someone\AppData\Local\Temp\repo"), Is.False);
    }

    [Test]
    public void IsTempPath_UnderPosixTmp_IsTrue()
    {
        Assert.That(TempDirectories.IsTempPath("/tmp/repo-a"), Is.True);
        Assert.That(TempDirectories.IsTempPath("/tmp"), Is.True);
    }

    [Test]
    public void IsTempPath_PosixPathOutsideTmp_IsFalse()
    {
        Assert.That(TempDirectories.IsTempPath("/var/tmp/repo-a"), Is.False);
        Assert.That(TempDirectories.IsTempPath("/home/example/repos/repo-a"), Is.False);
    }

    [Test]
    public void IsTempPath_UnderCurrentProcessTemp_IsTrue()
    {
        // TempDirectory.SystemTempPath resolves through TMP/TEMP on Windows and TMPDIR//tmp elsewhere,
        // the same roots the check captures.
        var underTemp = Path.Combine(TempDirectory.SystemTempPath, "gw-temp-check", "repo");
        Assert.That(TempDirectories.IsTempPath(underTemp), Is.True);
    }
}

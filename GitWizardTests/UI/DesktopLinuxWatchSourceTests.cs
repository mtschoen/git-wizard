using System.Runtime.Versioning;
using GitWizard.Watch.Linux;
using GitWizardUI.Services;

namespace GitWizardTests.UI;

// The desktop, not the library, opts in to the pkexec helper. Creating a source starts nothing,
// and the test assembly forbids pkexec launches, so no prompt can appear.
[TestFixture]
[Platform("Linux")]
[SupportedOSPlatform("linux")]
public class DesktopLinuxWatchSourceTests
{
    [Test]
    public async Task Root_UsesTheInProcessFanotifySource()
    {
        await using var source = DesktopLinuxWatchSource.Create(true, "/opt/git-wizard/GitWizardUI");

        Assert.That(source, Is.TypeOf<FanotifyVolumeChangeSource>());
    }

    [Test]
    public async Task NonRoot_UsesTheHelperRunningTheSuppliedExecutable()
    {
        await using var source = DesktopLinuxWatchSource.Create(false, "/opt/git-wizard/GitWizardUI");

        var startInfo = ((ElevatedHelperVolumeChangeSource)source).StartInfo;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(startInfo, Is.Not.Null);
            Assert.That(startInfo!.ArgumentList, Does.Contain("/opt/git-wizard/GitWizardUI"));
            Assert.That(startInfo.ArgumentList[^1], Is.EqualTo(LinuxWatchHelperHost.Flag));
        }
    }

    [Test]
    public async Task NonRoot_WithADotnetHost_HasNoLaunchCommand()
    {
        await using var source = DesktopLinuxWatchSource.Create(false, "/usr/share/dotnet/dotnet");

        Assert.That(((ElevatedHelperVolumeChangeSource)source).StartInfo, Is.Null);
    }
}

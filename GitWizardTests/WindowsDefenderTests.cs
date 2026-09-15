using System.Reflection;
using GitWizard;

namespace GitWizardTests;

public class WindowsDefenderTests
{
    [Test]
    public void ProcessExclusions_ContainsShippedApplicationsAndExternalTools()
    {
        var field = typeof(WindowsDefender).GetField(
            "ProcessExclusions", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.That(field, Is.Not.Null);
        var exclusions = (string[])field!.GetValue(null)!;

        Assert.That(exclusions, Is.EquivalentTo(new[]
        {
            "dotnet.exe", "git.exe", "git-lfs.exe", "git-wizard.exe", "GitWizardUI.exe"
        }));
    }
}

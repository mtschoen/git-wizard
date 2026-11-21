namespace GitWizardTests.TestSupport;

[TestFixture]
public class GlobalTestSetupTests
{
    [Test]
    public void ResolveRunHome_WithExplicitIsolatedHome_StaysInsideIt()
    {
        using var isolated = new TempDirectory("gw-isolated-");
        using var profile = new TempDirectory("gw-profile-");

        var home = GlobalTestSetup.ResolveRunHome(isolated.Path, profile.Path);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Path.GetDirectoryName(home), Is.EqualTo(isolated.Path));
            Assert.That(home, Does.Not.StartWith(profile.Path));
            Assert.That(Directory.GetFileSystemEntries(profile.Path), Is.Empty);
        }
    }

    [TestCase(null)]
    [TestCase("")]
    public void ResolveRunHome_WithoutExplicitIsolation_UsesTheProfile(string? explicitHome)
    {
        using var profile = new TempDirectory("gw-profile-");

        var home = GlobalTestSetup.ResolveRunHome(explicitHome, profile.Path);

        Assert.That(Path.GetDirectoryName(home), Is.EqualTo(Path.Combine(profile.Path, ".GitWizardTestHome")));
    }

    [Test]
    public void ResolveRunHome_IsUniquePerCall()
    {
        using var isolated = new TempDirectory("gw-isolated-");

        var first = GlobalTestSetup.ResolveRunHome(isolated.Path, "unused");
        var second = GlobalTestSetup.ResolveRunHome(isolated.Path, "unused");

        Assert.That(first, Is.Not.EqualTo(second));
    }
}

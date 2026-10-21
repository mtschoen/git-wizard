using GitWizard;

namespace GitWizardTests.TestSupport;

[TestFixture]
[NonParallelizable]
public class LocalFilesSandboxTests
{
    [Test]
    public async Task NestedSandboxRestoresOuterHomeAndResetsCachedConfiguration()
    {
        var previous = Environment.GetEnvironmentVariable("GITWIZARD_HOME");
        using (var outer = new LocalFilesSandbox())
        {
            var configuration = await GitWizardConfiguration.GetGlobalConfigurationAsync();
            configuration.SearchPaths.Clear();
            configuration.SearchPaths.Add("outer-only-marker");
            string? innerPath = null;
            try
            {
                using var inner = new LocalFilesSandbox();
                innerPath = inner.Path;
                Assert.That(Environment.GetEnvironmentVariable("GITWIZARD_HOME"), Is.EqualTo(inner.Path));
                var innerConfiguration = await GitWizardConfiguration.GetGlobalConfigurationAsync();
                Assert.That(innerConfiguration, Is.Not.SameAs(configuration));
                Assert.That(innerConfiguration.SearchPaths, Does.Not.Contain("outer-only-marker"));
                throw new InvalidOperationException("unwind");
            }
            catch (InvalidOperationException exception) when (exception.Message == "unwind") { }
            Assert.That(innerPath, Is.Not.Null);
            Assert.That(Directory.Exists(innerPath), Is.False);
            Assert.That(Environment.GetEnvironmentVariable("GITWIZARD_HOME"), Is.EqualTo(outer.Path));
        }
        Assert.That(Environment.GetEnvironmentVariable("GITWIZARD_HOME"), Is.EqualTo(previous));
    }
}

using MFTLib.Index;

namespace GitWizardTests;

[TestFixture]
public class CacheDirectoryIsolationTests
{
    [Test]
    public void ResolveDefaultPath_WhenTestAssemblyIsLoaded_Throws()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => CacheDirectory.ResolveDefaultPath());

        Assert.That(exception!.Message,
            Does.Contain("CacheDirectoryIsolation.ForbidDefaultCacheDirectory"));
    }
}

using GitWizard;

namespace GitWizardTests.Discovery;

[TestFixture]
public class DiscoveryScopeTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void ScopeKeepsSearchAndRetainedRootsWithoutClassifyingThem(bool noMft)
    {
        var configuration = new GitWizardConfiguration
        {
            SearchPaths = { @"C:\repos", @"D:\repos", @"Z:\shared", @"U:\unknown" }
        };
        var request = new DiscoveryRequest(configuration, DiscoveryPurpose.Watch, false, noMft,
            [@"E:\retained", @"c:\repos"]);
        var scope = DiscoveryScope.CreateExpanded(request, [.. configuration.SearchPaths], request.RetainedRoots, []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(scope.SearchRoots, Is.EqualTo(configuration.SearchPaths));
            Assert.That(scope.RetainedRoots, Is.EqualTo(request.RetainedRoots));
            Assert.That(scope.IndexRoots, Is.EqualTo(new[] { @"C:\repos", @"D:\repos", @"U:\unknown", @"Z:\shared", @"E:\retained" }));
            Assert.That(scope.ExpansionErrors, Is.Empty);
        }
    }
}

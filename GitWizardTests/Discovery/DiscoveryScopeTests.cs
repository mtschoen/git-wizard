using GitWizard;

namespace GitWizardTests.Discovery;

[TestFixture]
public class DiscoveryScopeTests
{
    [Test]
    public void ScopeKeepsFailedDriveAndRetainedOutsideDrive()
    {
        var configuration = new GitWizardConfiguration
        {
            SearchPaths = { @"C:\repos", @"D:\repos", @"Z:\shared" }
        };
        var request = new DiscoveryRequest(configuration, DiscoveryPurpose.Watch, false, false,
            [@"E:\retained"]);
        RootClassification Classify(string root) => root.StartsWith(@"Z:\", StringComparison.Ordinal)
            ? new(root, RootFileSystem.Recursive, null, null)
            : new(root, RootFileSystem.Ntfs, root[..1], null);

        var scope = DiscoveryScope.CreateExpanded(request, configuration.SearchPaths.ToArray(),
            request.RetainedRoots, [], Classify);
        Assert.That(scope.RequestedNtfsDrives, Is.EquivalentTo(new[] { "C", "D", "E" }));
        Assert.That(scope.RecursiveRoots, Is.EqualTo(new[] { @"Z:\shared" }));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void UnknownFileSystem_GovernedByNoMft(bool noMft)
    {
        var configuration = new GitWizardConfiguration
        {
            SearchPaths = { @"U:\unknown" }
        };
        var request = new DiscoveryRequest(configuration, DiscoveryPurpose.Ordinary, false, noMft, []);
        RootClassification Classify(string root) =>
            new(root, RootFileSystem.Unknown, null, "access denied");

        var scope = DiscoveryScope.CreateExpanded(request, configuration.SearchPaths.ToArray(),
            request.RetainedRoots, [], Classify);

        if (!noMft)
        {
            Assert.That(scope.ClassificationErrors, Does.ContainKey(@"U:\unknown"));
            Assert.That(scope.RecursiveRoots, Is.Empty);
        }
        else
        {
            Assert.That(scope.RecursiveRoots, Does.Contain(@"U:\unknown"));
        }
    }

    [Test]
    public void WatchExcludesNonNtfsAndUnknownRoots()
    {
        var configuration = new GitWizardConfiguration
        {
            SearchPaths = { @"C:\ntfs", @"Z:\remote", @"U:\unknown" }
        };
        var request = new DiscoveryRequest(configuration, DiscoveryPurpose.Watch, false, true, [@"E:\retained"]);
        RootClassification Classify(string root) => root switch
        {
            @"C:\ntfs" => new(root, RootFileSystem.Ntfs, "C", null),
            @"Z:\remote" => new(root, RootFileSystem.Recursive, null, null),
            @"U:\unknown" => new(root, RootFileSystem.Unknown, null, "not available"),
            @"E:\retained" => new(root, RootFileSystem.Ntfs, "E", null),
            _ => new(root, RootFileSystem.Unknown, null, "unrecognized")
        };

        var scope = DiscoveryScope.CreateExpanded(request, configuration.SearchPaths.ToArray(),
            request.RetainedRoots, [], Classify);

        Assert.That(scope.WatchExcludedRoots, Does.Contain(@"Z:\remote"));
        Assert.That(scope.WatchExcludedRoots, Does.Contain(@"U:\unknown"));
        Assert.That(scope.RequestedNtfsDrives, Is.EquivalentTo(new[] { "C", "E" }));
    }
}

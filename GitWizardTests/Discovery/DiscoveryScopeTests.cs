using GitWizard;

namespace GitWizardTests.Discovery;

[TestFixture]
public class DiscoveryScopeTests
{
    [TestCase("letter")]
    [TestCase("spec", TestName = "Issue370_Scope_ParsesTrimmedDriveSpecs")]
    [TestCase("path", TestName = "Issue370_Scope_ExtractsFullPathRoot")]
    [TestCase("fallback", TestName = "Issue370_Scope_UsesRootWhenDriveIsAbsent")]
    public void ScopeKeepsFailedDriveAndRetainedOutsideDrive(string driveForm)
    {
        var configuration = new GitWizardConfiguration
        {
            SearchPaths = { @"C:\repos", @"D:\repos", @"Z:\shared" }
        };
        var request = new DiscoveryRequest(configuration, DiscoveryPurpose.Watch, false, false,
            [@"E:\retained", @"c:\retained"]);
        RootClassification Classify(string root)
        {
            if (root.StartsWith(@"Z:\", StringComparison.Ordinal))
            {
                return new RootClassification(root, RootFileSystem.Recursive, null, null);
            }

            var drive = driveForm switch
            {
                "letter" => root[..1],
                "spec" => $" {root[..1].ToLowerInvariant()}/ ",
                "path" => root.ToLowerInvariant(),
                "fallback" => null,
                _ => throw new AssertionException("Unexpected drive form.")
            };
            return new RootClassification(root, RootFileSystem.Ntfs, drive, null);
        }

        var scope = DiscoveryScope.CreateExpanded(request, configuration.SearchPaths.ToArray(),
            request.RetainedRoots, [], Classify);
        Assert.That(scope.RequestedNtfsDrives, Is.EquivalentTo(["C", "D", "E"]));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(scope.RequestedNtfsDrives, Is.EqualTo(["C", "D", "E"]));
            Assert.That(scope.RecursiveRoots, Is.EqualTo([@"Z:\shared"]));
        }
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
            using (Assert.EnterMultipleScope())
            {
                Assert.That(scope.ClassificationErrors, Does.ContainKey(@"U:\unknown"));
                Assert.That(scope.RecursiveRoots, Is.Empty);
            }
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
            @"C:\ntfs" => new RootClassification(root, RootFileSystem.Ntfs, "C", null),
            @"Z:\remote" => new RootClassification(root, RootFileSystem.Recursive, null, null),
            @"U:\unknown" => new RootClassification(root, RootFileSystem.Unknown, null, "not available"),
            @"E:\retained" => new RootClassification(root, RootFileSystem.Ntfs, "E", null),
            _ => new RootClassification(root, RootFileSystem.Unknown, null, "unrecognized")
        };

        var scope = DiscoveryScope.CreateExpanded(request, configuration.SearchPaths.ToArray(),
            request.RetainedRoots, [], Classify);

        Assert.That(scope.WatchExcludedRoots, Does.Contain(@"Z:\remote"));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(scope.WatchExcludedRoots, Does.Contain(@"U:\unknown"));
            Assert.That(scope.RequestedNtfsDrives, Is.EquivalentTo(["C", "E"]));
        }
    }
}

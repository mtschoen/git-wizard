using GitWizard;
using GitWizardTests.TestSupport;
using MFTLib.Index;
using MFTLibTestExtensions;

namespace GitWizardTests.Discovery;

[TestFixture]
public class DirectoryRootAliasTests
{
    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task DiscoveryUsesResolvedDirectoryAndConfiguredIgnorePolicy(bool walkOnly, bool ignored)
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repo");
        var alias = Path.Combine(files.DirectoryPath, "alias");
        Directory.CreateSymbolicLink(alias, files.TreePath);
        var configuration = new GitWizardConfiguration { SearchPaths = [alias], IncludeTempDirectories = true };
        if (ignored)
        {
            configuration.IgnoredPaths.Add(Path.Combine(alias, "repo"));
        }
        var source = SyntheticIndexSource.Create(files.Producer);
        var configureOptions = files.ConfigureOptions;
        var dependencies = RepositoryDiscoveryDependencies.CreateDefault(options =>
            configureOptions(options) with { CacheMode = CacheMode.Disabled, MftSource = source });
        var coordinator = new RepositoryDiscoveryCoordinator(dependencies);
        var updates = new RecordingUpdateHandler();

        await using var result = await coordinator.DiscoverAsync(
            new DiscoveryRequest(configuration, DiscoveryPurpose.Rebuild, false, walkOnly, []), updates, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Inventory.CanPublishFullInventory, Is.True);
            Assert.That(result.Inventory.Paths, Is.EqualTo(ignored ? Array.Empty<string>() :
                [Path.Combine(files.TreePath, "repo")]));
            Assert.That(result.Inventory.RootStatuses.Single().IndexStatus!.Root, Is.EqualTo(RootKey.FromDirectory(alias)));
            Assert.That(result.Inventory.RootStatuses.Single().IndexStatus!.Producer,
                Is.EqualTo(walkOnly ? ProducerKind.Enumeration : ProducerKind.Mft));
            Assert.That(updates.Messages, Has.Some.Contains($"producer: {(walkOnly ? "Enumeration" : "Mft")}"));
        }
    }
}

using GitWizard;
using GitWizardTests.TestSupport;
using MFTLib.Index;
using MFTLibTestExtensions;

namespace GitWizardTests.Discovery;

/// <summary>
/// A configured alias root publishes repositories in resolved coordinates, so a partial discovery must
/// reconcile the saved list against the resolved coverage: a stale entry under the resolved root is
/// pruned, while entries under the failed root survive.
/// </summary>
[TestFixture]
public class DirectoryRootAliasReconciliationTests
{
    // The crawl case takes the enumeration route a non-Windows host uses, on every host.
    [TestCase(false)]
    [TestCase(true)]
    public async Task Rebuild_PartialAliasDiscovery_PrunesResolvedStaleEntryAndKeepsFailedRootEntry(bool crawl)
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repo");
        var alias = Path.Combine(files.DirectoryPath, "alias");
        Directory.CreateSymbolicLink(alias, files.TreePath);
        var missingRoot = Path.Combine(files.DirectoryPath, "missing");
        var stale = Path.Combine(files.TreePath, "gone");
        var failedRootEntry = Path.Combine(missingRoot, "kept");
        var listWrites = new List<IReadOnlyList<string>>();
        var source = SyntheticIndexSource.Create(files.Producer);
        var configureOptions = files.ConfigureOptions;
        var defaults = RepositoryDiscoveryDependencies.CreateDefault(options =>
            configureOptions(options) with { CacheMode = CacheMode.Disabled, MftSource = source }, () => []);
        var coordinator = new RepositoryDiscoveryCoordinator(new RepositoryDiscoveryDependencies
        {
            IsWindows = defaults.IsWindows && !crawl,
            IsLinux = defaults.IsLinux,
            CrawlRootAsync = defaults.CrawlRootAsync,
            ConfigureOptions = defaults.ConfigureOptions,
            AdditionalCloneRoots = defaults.AdditionalCloneRoots,
            ReadListAsync = _ => Task.FromResult<string[]?>([stale, failedRootEntry]),
            WriteListAsync = (paths, _) => { listWrites.Add(paths); return Task.CompletedTask; },
            CandidateExists = defaults.CandidateExists,
            CreateLinuxSource = defaults.CreateLinuxSource
        });
        var configuration = new GitWizardConfiguration
        {
            SearchPaths = [alias, missingRoot],
            IncludeTempDirectories = true
        };

        await using var result = await coordinator.DiscoverAsync(
            new DiscoveryRequest(configuration, DiscoveryPurpose.Rebuild, false, false, []), null, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Inventory.Outcome, Is.EqualTo(DiscoveryOutcome.Partial));
            Assert.That(listWrites.Single(), Is.EquivalentTo([Path.Combine(files.TreePath, "repo"), failedRootEntry]));
        }
    }

    [Test]
    public async Task WatchStartup_PartialAliasDiscovery_PrunesResolvedStaleEntryAndKeepsFailedRootEntry()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repo");
        var alias = Path.Combine(files.DirectoryPath, "alias");
        Directory.CreateSymbolicLink(alias, files.TreePath);
        var failedRoot = Directory.CreateDirectory(Path.Combine(files.DirectoryPath, "Y")).FullName;
        var stale = Path.Combine(files.TreePath, "gone");
        var failedRootEntry = Path.Combine(failedRoot, "kept");
        var scripted = new ScriptedWatchSource();
        var indices = await Task.WhenAll(
            files.OpenMftIndexAsync(scripted, 'X', RootKey.FromDirectory(alias)),
            files.OpenMftIndexAsync(scripted, 'Y', RootKey.FromDirectory(failedRoot)));
        var session = new MftIndexSession(indices, null, [alias, failedRoot], new Dictionary<string, string>());
        var listWrites = new List<IReadOnlyList<string>>();
        var coordinator = new RepositoryDiscoveryCoordinator(new RepositoryDiscoveryDependencies
        {
            IsWindows = true,
            IsLinux = false,
            CrawlRootAsync = (_, _, _) => throw new AssertionException("Indexed roots must not be crawled."),
            OpenIndexAsync = (_, _, _, _) => Task.FromResult(session),
            ReadListAsync = _ => Task.FromResult<string[]?>([stale, failedRootEntry]),
            WriteListAsync = (paths, _) => { listWrites.Add(paths); return Task.CompletedTask; },
            CandidateExists = (_, _) => true,
            CreateLinuxSource = () => throw new PlatformNotSupportedException()
        });
        var configuration = new GitWizardConfiguration { SearchPaths = { alias, failedRoot }, IncludeTempDirectories = true };
        using var cancellation = new CancellationTokenSource(TestTimeouts.HangGuard);

        var startupTask = coordinator.PrepareWatchAsync(
            new DiscoveryRequest(configuration, DiscoveryPurpose.Watch, false, false, []), null, cancellation.Token);
        await Task.WhenAll(scripted.WaitForStartAsync('X', 1, cancellation.Token),
            scripted.WaitForStartAsync('Y', 1, cancellation.Token));
        scripted.WatchFor('Y').LoseChannel(new IOException("Y journal corrupt"));
        await scripted.WatchFor('X').PublishCaughtUpAsync();
        await using var startup = await startupTask;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(startup.Inventory.Outcome, Is.EqualTo(DiscoveryOutcome.Partial));
            Assert.That(listWrites.Single(), Is.EquivalentTo([Path.Combine(files.TreePath, "repo"), failedRootEntry]));
        }
    }
}

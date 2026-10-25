using GitWizard;
using GitWizardTests.TestSupport;

namespace GitWizardTests.Discovery;

/// <summary>Linux retained roots pass through the same admission policy as every other route.</summary>
[TestFixture]
public class LinuxRetainedRootAdmissionTests
{

    static readonly string Root = Path.Combine(TempDirectory.SystemTempPath, "gw-linux-admission");


    internal static async Task<IReadOnlyList<string>> InventoryAsync(
        string searchRoot, string retainedRoot, string? ignoredRoot, bool skipHiddenDirectories, bool includeTempDirectories)
    {
        Directory.CreateDirectory(searchRoot);
        var coordinator = new RepositoryDiscoveryCoordinator(new RepositoryDiscoveryDependencies
        {
            IsWindows = false,
            IsLinux = true,
            ClassifyRoot = root => new RootClassification(root, RootFileSystem.Recursive, null, null),
            CrawlRootAsync = (_, _, _) => Task.FromResult(new RecursiveDiscovery([], [], new Dictionary<string, string>())),
            OpenIndexAsync = (_, _, _, _) => throw new PlatformNotSupportedException(),
            ReadListAsync = _ => Task.FromResult<string[]?>([]),
            WriteListAsync = (_, _) => Task.CompletedTask,
            CandidateExists = (_, _) => true,
            CreateLinuxSource = () => new ScriptedVolumeChangeSource { KeepWatching = true }
        });
        var configuration = new GitWizardConfiguration
        {
            SearchPaths = { searchRoot },
            IncludeTempDirectories = includeTempDirectories,
            SkipHiddenDirectories = skipHiddenDirectories
        };
        if (ignoredRoot is not null)
        {
            configuration.IgnoredPaths.Add(ignoredRoot);
        }

        var request = new DiscoveryRequest(configuration, DiscoveryPurpose.Watch, false, false, [retainedRoot]);
        using var cancellation = new CancellationTokenSource(TestTimeouts.HangGuard);
        await using var startup = await coordinator.PrepareWatchAsync(request, null, cancellation.Token);
        return startup.Inventory.Paths;
    }

    [Test]
    public async Task RetainedRootOutsideSearchRoots_IsAdmitted()
    {
        var paths = await InventoryAsync(Path.Combine(Root, "search"), Path.Combine(Root, "retained", "repo"), null, false, true);
        Assert.That(paths, Is.EqualTo(new[] { Path.Combine(Root, "retained", "repo") }).IgnoreCase);
    }

    [Test]
    public async Task RetainedRootUnderIgnoredRoot_IsRejected()
    {
        var paths = await InventoryAsync(Path.Combine(Root, "search"), Path.Combine(Root, "retained", "ignored", "repo"), Path.Combine(Root, "retained", "ignored"), false, true);
        Assert.That(paths, Is.Empty);
    }

    [Test]
    public async Task RetainedRootUnderHiddenSegmentOfSearchRoot_IsRejectedWhenHiddenDirectoriesAreSkipped()
    {
        var paths = await InventoryAsync(Path.Combine(Root, "search"), Path.Combine(Root, "search", ".hidden", "repo"), null, true, true);
        Assert.That(paths, Is.Empty);
    }
}

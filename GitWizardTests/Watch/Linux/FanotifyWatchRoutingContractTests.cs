using System.Runtime.Versioning;
using GitWizard;
using GitWizard.Watch;
using GitWizard.Watch.Linux;
using GitWizardTests.TestSupport;
using static GitWizardTests.Watch.Linux.FanotifyEventBuffers;

namespace GitWizardTests.Watch.Linux;

/// <summary>
/// Feeds real fanotify decoder output through the prepared Linux startup and the watch service,
/// so the batch routing identity and the move semantics are checked end to end.
/// </summary>
[TestFixture]
[SupportedOSPlatform("linux")]
public class FanotifyWatchRoutingContractTests
{

    string _temporaryRoot = null!;

    [SetUp]
    public void CreateTemporaryRoot() =>
        _temporaryRoot = Directory.CreateTempSubdirectory("git-wizard-fanotify-routing").FullName;

    [TearDown]
    public void DeleteTemporaryRoot() => Directory.Delete(_temporaryRoot, true);

    [Test]
    public async Task EventUnderNonRootMarkedDirectory_IsDeliveredToTheService()
    {
        var searchRoot = Directory.CreateDirectory(Path.Combine(_temporaryRoot, "repos")).FullName;
        var repositoryPath = Path.Combine(searchRoot, "tracked");
        var handle = new byte[] { 11, 12, 13, 14 };
        var native = new FakeFanotifyNative();
        native.MapHandle(0L, GetFullHandleBytes(handle), 500, repositoryPath);
        native.ReadQueue.Enqueue(CreateSyntheticEventBuffer(FanotifyConstants.Modify, handle, "file.txt"));

        await using var startup = await PrepareStartupAsync(native, searchRoot, [repositoryPath]);
        var service = new RepositoryWatchService(startup, debounce: TimeSpan.Zero);

        var changeEvent = await FirstEventAsync(service);

        Assert.That(changeEvent, Is.EqualTo(new RepositoryChangeEvent(repositoryPath, RepositoryChangeKind.Changed)));
    }

    [Test]
    public async Task DirectoryMovedIntoSearchRoot_IsAdoptedAsRepository()
    {
        var searchRoot = Directory.CreateDirectory(Path.Combine(_temporaryRoot, "repos")).FullName;
        var movedInPath = Path.Combine(searchRoot, "incoming");
        Directory.CreateDirectory(Path.Combine(movedInPath, ".git"));
        var handle = new byte[] { 21, 22, 23, 24 };
        var native = new FakeFanotifyNative();
        native.MapHandle(0L, GetFullHandleBytes(handle), 501, searchRoot);
        native.ReadQueue.Enqueue(CreateSyntheticEventBuffer(
            FanotifyConstants.MovedTo | FanotifyConstants.OnDirectory, handle, "incoming"));

        await using var startup = await PrepareStartupAsync(native, searchRoot, [Path.Combine(searchRoot, "existing")]);
        var service = new RepositoryWatchService(startup, debounce: TimeSpan.Zero);

        var changeEvent = await FirstEventAsync(service);

        Assert.That(changeEvent, Is.EqualTo(new RepositoryChangeEvent(movedInPath, RepositoryChangeKind.Created)));
    }

    static async Task<RepositoryChangeEvent> FirstEventAsync(RepositoryWatchService service)
    {
        using var cancellation = new CancellationTokenSource(TestTimeouts.HangGuard);
        await using var enumerator = service.RunAsync(cancellation.Token).GetAsyncEnumerator();
        Assert.That(await enumerator.MoveNextAsync().AsTask().WaitAsync(TestTimeouts.HangGuard), Is.True);
        return enumerator.Current;
    }

    static async Task<WatchStartupResult> PrepareStartupAsync(
        FakeFanotifyNative native, string searchRoot, string[] crawledRepositories)
    {
        var coordinator = new RepositoryDiscoveryCoordinator(new RepositoryDiscoveryDependencies
        {
            IsWindows = false,
            IsLinux = true,
            ClassifyRoot = root => new RootClassification(root, RootFileSystem.Recursive, null, null),
            CrawlRootAsync = (_, _, _) => Task.FromResult(
                new RecursiveDiscovery(crawledRepositories, [], new Dictionary<string, string>())),
            OpenIndexAsync = (_, _, _, _) => throw new PlatformNotSupportedException(),
            ReadListAsync = _ => Task.FromResult<string[]?>([]),
            WriteListAsync = (_, _) => Task.CompletedTask,
            CandidateExists = (_, _) => true,
            CreateLinuxSource = () => new FanotifyVolumeChangeSource(native)
        });

        var config = new GitWizardConfiguration { SearchPaths = { searchRoot } };
        var request = new DiscoveryRequest(config, DiscoveryPurpose.Watch, false, false, []);
        using var cancellation = new CancellationTokenSource(TestTimeouts.HangGuard);
        return await coordinator.PrepareWatchAsync(request, null, cancellation.Token);
    }
}

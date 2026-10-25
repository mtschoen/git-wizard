using System.Collections.Concurrent;
using GitWizard;
using GitWizardTests.TestSupport;
using MFTLib.Index;
using MFTLibTestExtensions;

namespace GitWizardTests.Discovery;

/// <summary>
/// A lost journal checkpoint reaches watch preparation through a real library open. The window
/// override is process-global, so the fixture is non-parallel, the scope outlives every awaited
/// step, and sessions are disposed before the scope.
/// </summary>
[TestFixture]
[NonParallelizable]
public class RepositoryDiscoveryCoordinatorCheckpointLossTests
{
    static readonly SyntheticJournalWindow Healthy = new(PackedIndexFixture.JournalIdentifier, 0, 5_000, 64, 8_192);
    static readonly SyntheticJournalWindow Lost = new(PackedIndexFixture.JournalIdentifier, 1_500, 5_000, 64, 8_192);

    PackedIndexFixture _files = null!;
    ConcurrentDictionary<char, SyntheticJournalWindow> _windows = null!;
    IDisposable _scope = null!;
    int _productions;

    [SetUp]
    public void SetUp()
    {
        _productions = 0;
        _files = new PackedIndexFixture();
        _files.AddMarker("repo");
        _files.BeforeProduceAsync = (_, _) =>
        {
            Interlocked.Increment(ref _productions);
            return Task.CompletedTask;
        };
        _windows = new ConcurrentDictionary<char, SyntheticJournalWindow>();
        _windows['X'] = Healthy;
        _scope = JournalIsolation.OverrideJournalWindow(
            drive => _windows.TryGetValue(drive.Letter, out var window) ? window : null);
    }

    [TearDown]
    public void TearDown()
    {
        _scope.Dispose();
        _files.Dispose();
    }

    FileIndexOptions Options(bool cacheOnly, ScriptedWatchSource? source = null) => new()
    {
        Drives = [new IndexedDrive('X', _files.TreePath, 1)],
        CacheDirectory = _files.BlockDirectory,
        CacheMode = cacheOnly ? CacheMode.ExistingOnly : CacheMode.Persistent,
        MftSource = SyntheticIndexSource.Create(_files.Producer, source)
    };

    async Task SeedCacheAsync()
    {
        await using var seed = await FileIndex.OpenAsync(Options(cacheOnly: false), CancellationToken.None);
        Assert.That(_productions, Is.EqualTo(1));
    }

    async Task<MftIndexSession> OpenSessionAsync(bool cacheOnly, ScriptedWatchSource source)
    {
        var index = await FileIndex.OpenAsync(Options(cacheOnly, source), CancellationToken.None);
        return new MftIndexSession(index, new CountingOwner(), ["X"],
            [new IndexedDrive('X', _files.TreePath, 1)], new Dictionary<string, string>());
    }

    RepositoryDiscoveryCoordinator CreateCoordinator() => new(new RepositoryDiscoveryDependencies
    {
        IsWindows = true,
        IsLinux = false,
        ClassifyRoot = _ => new RootClassification(_files.TreePath, RootFileSystem.Ntfs, "X", null),
        CrawlRootAsync = (_, _, _) => throw new AssertionException("NTFS roots must not be crawled."),
        OpenIndexAsync = (_, _, _, _) => throw new AssertionException("The prepared session must be reused."),
        ReadListAsync = _ => Task.FromResult<string[]?>([]),
        WriteListAsync = (_, _) => Task.CompletedTask,
        CandidateExists = (_, _) => true,
        CreateLinuxSource = () => throw new PlatformNotSupportedException()
    });

    DiscoveryRequest CreateWatchRequest() => new(
        new GitWizardConfiguration { SearchPaths = { _files.TreePath } }, DiscoveryPurpose.Watch, false, false, []);

    [TestCase(true, BlockOrigin.WarmStartedFromCache, 1)]
    [TestCase(false, BlockOrigin.ProducedByScan, 0)]
    public async Task CheckpointLoss_PreparesWatchAccordingToInitialBlockOrigin(
        bool cacheOnly, BlockOrigin expectedInitialOrigin, int expectedAdditionalProductions)
    {
        var source = new ScriptedWatchSource { CatchUpOnStart = true };
        await SeedCacheAsync();
        _windows['X'] = Lost;
        var session = await OpenSessionAsync(cacheOnly, source);
        Assert.That(session.Index.Drives.Single().Watch.CheckpointLoss, Is.Not.Null);
        Assert.That(session.Index.Drives.Single().Block?.Origin, Is.EqualTo(expectedInitialOrigin));
        var productionsBeforeWatch = _productions;
        using var cancellation = new CancellationTokenSource(TestTimeouts.HangGuard);

        await using var startup = await CreateCoordinator()
            .PrepareWatchAsync(CreateWatchRequest(), session, cancellation.Token);

        Assert.Multiple(() =>
        {
            Assert.That(_productions - productionsBeforeWatch, Is.EqualTo(expectedAdditionalProductions));
            Assert.That(source.Starts, Has.Count.EqualTo(1));
            Assert.That(startup.WatchVolumes, Is.EquivalentTo(["X"]));
        });
    }
}

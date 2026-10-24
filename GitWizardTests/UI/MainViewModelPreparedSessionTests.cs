using System.Runtime.CompilerServices;
using GitWizard;
using GitWizard.Watch;
using GitWizard.Watch.Linux;
using GitWizardTests.TestSupport;
using GitWizardUI.ViewModels;
using MFTLib.Index;
using MFTLibTestExtensions;

namespace GitWizardTests.UI;

// Covers PreparedDiscoveryIndexStore.cs, MainViewModel.cs, IndexCacheViewModel.cs, and LiveWatchViewModel.Startup.cs.
[TestFixture]
public class MainViewModelPreparedSessionTests
{
    static MainViewModel NewViewModel() => TestViewModels.Create();

    [SetUp]
    public void SetUp() => GitWizardLog.SilentMode = true;

    [Test]
    public async Task SetPreparedDiscoveryIndexAsync_ReplacesAndDisposesOldIndex()
    {
        await using var first = new IndexOwnerFixture();
        await first.StartAsync();
        await using var second = new IndexOwnerFixture();
        await second.StartAsync();
        await using var viewModel = NewViewModel();
        await viewModel.PreparedDiscoveryIndex.SetAsync(first.Owner);
        await viewModel.PreparedDiscoveryIndex.SetAsync(first.Owner);
        Assert.That(viewModel.PreparedDiscoveryIndex.Current, Is.SameAs(first.Owner));
        Assert.That(first.ConnectionDisposals, Is.Zero);
        Assert.That(first.Owner.IsDisposed, Is.False);
        await viewModel.PreparedDiscoveryIndex.SetAsync(second.Owner);
        Assert.That(first.ConnectionDisposals, Is.EqualTo(1));
        Assert.That(viewModel.PreparedDiscoveryIndex.Current, Is.SameAs(second.Owner));
        Assert.That(second.Owner.IsDisposed, Is.False);
    }

    [Test]
    public Task ClearCacheAsync_DisposesPreparedDiscoveryIndex() =>
        AssertReleaseAsync(viewModel => viewModel.IndexCache.ClearCacheAsync());

    [Test]
    public Task DeleteAllLocalFilesAsync_DisposesPreparedDiscoveryIndex() =>
        AssertReleaseAsync(viewModel => viewModel.IndexCache.DeleteAllLocalFilesAsync());

    [Test]
    public Task DisposeAsync_DisposesPreparedDiscoveryIndex() =>
        AssertReleaseAsync(viewModel => viewModel.DisposeAsync().AsTask());

    [Test]
    public async Task TakePreparedDiscoveryIndex_AtomicallyTransfersAndClears()
    {
        await using var fixture = new IndexOwnerFixture();
        await fixture.StartAsync();
        var viewModel = NewViewModel();
        MftIndexSession? owner;
        try
        {
            await viewModel.PreparedDiscoveryIndex.SetAsync(fixture.Owner);
            owner = viewModel.PreparedDiscoveryIndex.Take();
            Assert.That(viewModel.PreparedDiscoveryIndex.Take(), Is.Null);
        }
        finally
        {
            await viewModel.DisposeAsync();
        }
        try
        {
            Assert.That(owner, Is.SameAs(fixture.Owner));
            Assert.That(owner!.Index.Search(new SearchQuery(".git", NameMatchMode.Exact)).Single().Parent!.Value.Name, Is.EqualTo("repo"));
            Assert.That(fixture.ConnectionDisposals, Is.Zero);
        }
        finally
        {
            if (owner is not null)
                await owner.DisposeAsync();
        }
        Assert.That(fixture.ConnectionDisposals, Is.EqualTo(1));
    }

    [Test]
    public async Task PreparedDiscoveryIndex_PreservesDrives()
    {
        await using var fixture = new IndexOwnerFixture();
        await fixture.StartAsync();
        await using var viewModel = NewViewModel();
        await viewModel.PreparedDiscoveryIndex.SetAsync(fixture.Owner);
        Assert.That(viewModel.PreparedDiscoveryIndex.Current!.Drives, Is.EqualTo(new[] { "X" }));
        Assert.That(viewModel.PreparedDiscoveryIndex.Current.Index, Is.SameAs(fixture.Owner.Index));
    }

    [Test]
    public Task SetPreparedDiscoveryIndexAsync_WhenNull_DisposesExistingIndex() =>
        AssertReleaseAsync(viewModel => viewModel.PreparedDiscoveryIndex.SetAsync(null));

    static async Task AssertReleaseAsync(Func<MainViewModel, Task> release)
    {
        await using var fixture = new IndexOwnerFixture();
        await fixture.StartAsync();
        await using var viewModel = NewViewModel();
        await viewModel.PreparedDiscoveryIndex.SetAsync(fixture.Owner);
        await release(viewModel);
        Assert.That(viewModel.PreparedDiscoveryIndex.Current, Is.Null);
        Assert.That(fixture.Owner.IsDisposed, Is.True);
        Assert.That(fixture.ConnectionDisposals, Is.EqualTo(1));
        await viewModel.PreparedDiscoveryIndex.DisposeAsync();
        Assert.That(viewModel.PreparedDiscoveryIndex.Current, Is.Null);
        Assert.That(fixture.ConnectionDisposals, Is.EqualTo(1));
    }

    [Test]
    public Task ToggleLiveAsync_TwoConsecutiveStarts_FirstAdoptsPreparedIndex_SecondOpensIndex() =>
        AssertRestartAsync(respawn: false);

    [Test]
    public Task ToggleLiveAsync_ElevatedRespawn_FirstAdoptsPreparedIndex_RespawnOpensIndex() =>
        AssertRestartAsync(respawn: true);

    static async Task AssertRestartAsync(bool respawn)
    {
        var configuration = await GitWizardConfiguration.GetGlobalConfigurationAsync();
        var searchPaths = configuration.SearchPaths;
        configuration.SearchPaths = [];
        try
        {
            await ExerciseRestartAsync(respawn);
        }
        finally
        {
            configuration.SearchPaths = searchPaths;
        }
    }

    static async Task ExerciseRestartAsync(bool respawn)
    {
        var scripted = new ScriptedWatchSource { CatchUpOnStart = true };
        await using var fixture = new IndexOwnerFixture();
        await fixture.StartAsync(scripted);
        await using var viewModel = NewViewModel();
        await viewModel.PreparedDiscoveryIndex.SetAsync(fixture.Owner);
        var opens = 0;
        viewModel.LiveWatch.PrepareWatchAsync = async (request, prepared, _, cancellationToken) =>
        {
            var source = new IndexVolumeChangeSource((_, _, _) =>
            {
                opens++;
                throw new InvalidOperationException("Fresh index unavailable");
            }, prepared);
            return await FakeLiveStartup.FromSourceArmedAsync(source, request, cancellationToken,
                    watchVolumes: ["X"])
                .ConfigureAwait(false);
        };
        viewModel.LiveWatch.IsElevated = () => respawn;
        viewModel.LiveWatch.CapabilityProbe = () => new FanotifyCapabilityResult(FanotifyCapabilityStatus.Available, 0);
        viewModel.RepositoryList.AddRepository(new GitWizardRepository(@"X:\repo"), streaming: false);
        try
        {
            var starting = viewModel.LiveWatch.ToggleLiveAsync();
            await scripted.WaitForStartAsync('X', 1, CancellationToken.None).WaitAsync(TestTimeouts.HangGuard);
            await WaitUntilAsync(() => viewModel.LiveWatch.IsLive);
            Assert.That(opens, Is.Zero);
            Assert.That(viewModel.PreparedDiscoveryIndex.Current, Is.Null);
            if (respawn)
            {
                scripted.WatchFor('X').LoseChannel(new IOException("source died"));
                await starting.WaitAsync(TestTimeouts.HangGuard);
            }
            else
            {
                await viewModel.LiveWatch.ToggleLiveAsync();
                await starting.WaitAsync(TestTimeouts.HangGuard);
                Assert.That(fixture.ConnectionDisposals, Is.EqualTo(1));
                await viewModel.LiveWatch.ToggleLiveAsync();
            }
            Assert.That(viewModel.LiveWatch.IsLive, Is.False);
            Assert.That(opens, Is.EqualTo(1));
            Assert.That(fixture.ConnectionDisposals, Is.EqualTo(1));
        }
        finally
        {
            viewModel.LiveWatch.PrepareWatchAsync = FailIfPrepareWatchIsRequested;
        }
    }

    static Task<WatchStartupResult> FailIfPrepareWatchIsRequested(
        DiscoveryRequest request, MftIndexSession? prepared, IProgress<WatchStartupProgress>? progress, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("The test prepare seam must not be used after teardown.");

    static Task WaitUntilAsync(Func<bool> condition, [CallerArgumentExpression(nameof(condition))] string? expression = null) =>
        AsyncCondition.WaitUntilAsync(condition, TestTimeouts.HangGuard, expression);
}

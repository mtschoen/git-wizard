using GitWizard;
using GitWizard.Watch;
using GitWizard.Watch.Linux;
using GitWizardTests.TestSupport;
using GitWizardUI.ViewModels;
using GitWizardUI.ViewModels.Services;
using MFTLib.Index;

namespace GitWizardTests.UI;

[TestFixture]
public class MainViewModelPreparedSessionTests
{
    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);
    static MainViewModel NewViewModel() =>
        new(new StubUiDispatcher(), new StubUserDialogs(), new StubClipboardService());

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
        await viewModel.SetPreparedDiscoveryIndexAsync(first.Owner);
        await viewModel.SetPreparedDiscoveryIndexAsync(second.Owner);
        Assert.That(first.ConnectionDisposals, Is.EqualTo(1));
        Assert.That(viewModel.PreparedDiscoveryIndex, Is.SameAs(second.Owner));
        Assert.That(second.Owner.IsDisposed, Is.False);
    }

    [Test]
    public Task ClearCacheAsync_DisposesPreparedDiscoveryIndex() =>
        AssertReleaseAsync(viewModel => viewModel.ClearCacheAsync());

    [Test]
    public Task DeleteAllLocalFilesAsync_DisposesPreparedDiscoveryIndex() =>
        AssertReleaseAsync(viewModel => viewModel.DeleteAllLocalFilesAsync());

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
            await viewModel.SetPreparedDiscoveryIndexAsync(fixture.Owner);
            owner = viewModel.TakePreparedDiscoveryIndex();
            Assert.That(viewModel.TakePreparedDiscoveryIndex(), Is.Null);
        }
        finally
        {
            await viewModel.DisposeAsync();
        }
        try
        {
            Assert.That(owner, Is.SameAs(fixture.Owner));
            Assert.That(owner!.Index.FindByName(".git").Single().Parent!.Value.Name, Is.EqualTo("repo"));
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
        await viewModel.SetPreparedDiscoveryIndexAsync(fixture.Owner);
        Assert.That(viewModel.PreparedDiscoveryIndex!.Drives, Is.EqualTo(new[] { "X" }));
        Assert.That(viewModel.PreparedDiscoveryIndex.Index, Is.SameAs(fixture.Owner.Index));
    }

    [Test]
    public Task SetPreparedDiscoveryIndexAsync_WhenNull_DisposesExistingIndex() =>
        AssertReleaseAsync(viewModel => viewModel.SetPreparedDiscoveryIndexAsync(null));

    static async Task AssertReleaseAsync(Func<MainViewModel, Task> release)
    {
        await using var fixture = new IndexOwnerFixture();
        await fixture.StartAsync();
        await using var viewModel = NewViewModel();
        await viewModel.SetPreparedDiscoveryIndexAsync(fixture.Owner);
        await release(viewModel);
        Assert.That(viewModel.PreparedDiscoveryIndex, Is.Null);
        Assert.That(fixture.Owner.IsDisposed, Is.True);
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
        var scripted = new ScriptedIndexWatchSource();
        await using var fixture = new IndexOwnerFixture();
        await fixture.StartAsync(scripted);
        await using var viewModel = NewViewModel();
        await viewModel.SetPreparedDiscoveryIndexAsync(fixture.Owner);
        var opens = 0;
        viewModel.LivePrepareWatchAsync = async (request, prepared, cancellationToken) =>
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
        viewModel.LiveIsElevated = () => respawn;
        viewModel.LiveCapabilityProbe = () => new FanotifyCapabilityResult(FanotifyCapabilityStatus.Available, 0);
        viewModel.AddRepository(new GitWizardRepository(@"X:\repo"));
        try
        {
            var starting = viewModel.ToggleLiveAsync();
            await scripted.Started.WaitAsync(HangGuard);
            await WaitUntilAsync(() => viewModel.IsLive);
            Assert.That(opens, Is.Zero);
            Assert.That(viewModel.PreparedDiscoveryIndex, Is.Null);
            if (respawn)
            {
                scripted.Publish(new DriveWatchFailure('X', new IOException("source died")));
                await starting.WaitAsync(HangGuard);
            }
            else
            {
                await viewModel.ToggleLiveAsync();
                await starting.WaitAsync(HangGuard);
                Assert.That(fixture.ConnectionDisposals, Is.EqualTo(1));
                await viewModel.ToggleLiveAsync();
            }
            Assert.That(viewModel.IsLive, Is.False);
            Assert.That(opens, Is.EqualTo(1));
            Assert.That(fixture.ConnectionDisposals, Is.EqualTo(1));
        }
        finally
        {
            viewModel.LivePrepareWatchAsync = FailIfPrepareWatchIsRequested;
        }
    }

    static Task<WatchStartupResult> FailIfPrepareWatchIsRequested(
        DiscoveryRequest request, MftIndexSession? prepared, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("The test prepare seam must not be used after teardown.");

    static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(HangGuard);
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }
}

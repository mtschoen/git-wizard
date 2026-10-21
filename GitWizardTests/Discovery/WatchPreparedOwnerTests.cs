using GitWizard;
using GitWizardTests.TestSupport;

namespace GitWizardTests.Discovery;

/// <summary>
/// Watch preparation receives ownership of the prepared discovery owner on every platform,
/// because the desktop hands it over without asking which platform the coordinator will pick.
/// A platform that cannot adopt an MFT index owner must dispose it rather than drop it.
/// </summary>
[TestFixture]
public class WatchPreparedOwnerTests
{

    static RepositoryDiscoveryCoordinator CreateCoordinator(bool isLinux) =>
        new(new RepositoryDiscoveryDependencies
        {
            IsWindows = false,
            IsLinux = isLinux,
            ClassifyRoot = root => new RootClassification(root, RootFileSystem.Recursive, null, null),
            CrawlRootAsync = (_, _, _) => throw new AssertionException("No roots are configured to crawl."),
            OpenIndexAsync = (_, _, _, _) => throw new AssertionException("A non-Windows watch must not open an index."),
            ReadListAsync = _ => Task.FromResult<string[]?>([]),
            WriteListAsync = (_, _) => Task.CompletedTask,
            CandidateExists = (_, _) => false,
            CreateLinuxSource = () => new DiagnosticVolumeChangeSource()
        });

    static DiscoveryRequest CreateRequest() =>
        new(new GitWizardConfiguration(), DiscoveryPurpose.Watch, false, false, []);

    [Test]
    public async Task PrepareWatchAsync_OnLinux_DisposesPreparedOwner()
    {
        await using var fixture = new IndexOwnerFixture();
        await fixture.StartAsync();

        await using var startup = await CreateCoordinator(isLinux: true)
            .PrepareWatchAsync(CreateRequest(), fixture.Owner, CancellationToken.None)
            .WaitAsync(TestTimeouts.HangGuard);

        Assert.Multiple(() =>
        {
            Assert.That(fixture.Owner.IsDisposed, Is.True);
            Assert.That(fixture.ConnectionDisposals, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task PrepareWatchAsync_OnUnsupportedPlatform_DisposesPreparedOwnerBeforeThrowing()
    {
        await using var fixture = new IndexOwnerFixture();
        await fixture.StartAsync();

        Assert.ThrowsAsync<PlatformNotSupportedException>(() =>
            CreateCoordinator(isLinux: false).PrepareWatchAsync(CreateRequest(), fixture.Owner, CancellationToken.None));

        Assert.Multiple(() =>
        {
            Assert.That(fixture.Owner.IsDisposed, Is.True);
            Assert.That(fixture.ConnectionDisposals, Is.EqualTo(1));
        });
    }
}

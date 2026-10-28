using GitWizard;
using GitWizardTests.TestSupport;
using MFTLib.Index;

namespace GitWizardTests;

public class BrokerDiscoveryTests
{
    [SetUp]
    public void SetUp() => GitWizardLog.SilentMode = true;

    [Test]
    public async Task Discovery_FindsRepositoryParentsAndTransfersOwner()
    {
        await using var fixture = new IndexOwnerFixture();
        await fixture.StartAsync();
        var paths = new List<string>();
        await using var result = await DiscoverAsync(fixture, paths);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Success, Is.True);
            Assert.That(paths, Is.EqualTo([Path.Combine(fixture.Owner.Indices.Single().Root().Path, "repo")]));
            Assert.That(result.Index, Is.SameAs(fixture.Owner.IndexSet));
        }
    }

    [Test]
    public async Task Discovery_ConnectionDeclined_ReturnsFalse()
    {
        var paths = new List<string>();
        await using var result = await GitWizardApi.DiscoverRepositoriesWithIndexAsync(
            new GitWizardConfiguration(), paths, [@"X:\"], [], new IndexDiscoveryComposition(
                _ => throw new InvalidOperationException("UAC declined"), _ => true));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.Index, Is.Null);
            Assert.That(paths, Is.Empty);
        }
    }

    [Test]
    public async Task Discovery_CorruptIndexThrowsInvalidDataException_ReturnsFalse()
    {
        var paths = new List<string>();
        await using var result = await GitWizardApi.DiscoverRepositoriesWithIndexAsync(
            new GitWizardConfiguration(), paths, [@"X:\"], [], new IndexDiscoveryComposition(
                _ => throw new InvalidDataException("Corrupt MFT index block"), _ => true));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.Index, Is.Null);
            Assert.That(paths, Is.Empty);
        }
    }

    [Test]
    public async Task TryDiscoverRepositoriesUsingMftAsync_NoMft_ReturnsFalseWithoutScanning()
    {
        var configuration = new GitWizardConfiguration { SearchPaths = { @"X:\" } };
        var paths = new List<string>();
        await using var result = await GitWizardApi.TryDiscoverRepositoriesUsingMftAsync(
            configuration, paths, noMft: true);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Success, Is.False);
            Assert.That(paths, Is.Empty);
        }
    }

    [Test]
    public async Task Discovery_ZeroSelectedDrives_DoesNotInvokeConnectionFactory()
    {
        var calls = 0;
        var paths = new List<string>();
        await using var result = await GitWizardApi.DiscoverRepositoriesWithIndexAsync(
            new GitWizardConfiguration(), paths, ["/unselected"], [], new IndexDiscoveryComposition(
                _ =>
                {
                    calls++;
                    throw new AssertionException("No selected drive may connect");
                },
                _ => throw new AssertionException("No candidates may be validated")));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Success, Is.False);
            Assert.That(calls, Is.Zero);
            Assert.That(paths, Is.Empty);
        }
    }

    [Test]
    public async Task Discovery_DetachedOwnerSurvivesResultDisposal()
    {
        await using var fixture = new IndexOwnerFixture();
        await fixture.StartAsync();
        var result = await DiscoverAsync(fixture, new List<string>());
        var owner = result.DetachIndexSession();
        await result.DisposeAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Index, Is.Null);
            Assert.That(owner, Is.SameAs(fixture.Owner));
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(owner!.IndexSet.Search(new SearchQuery(".git", NameMatchMode.Exact)).Single().Parent!.Value.Name, Is.EqualTo("repo"));
            Assert.That(fixture.ConnectionDisposals, Is.Zero);
        }
        await owner.DisposeAsync();
        Assert.That(fixture.ConnectionDisposals, Is.EqualTo(1));
    }

    [Test]
    public async Task TryDiscoverRepositoriesUsingMftAsync_NoMft_ReturnsFalseAndNullIndex()
    {
        await using var result = await GitWizardApi.TryDiscoverRepositoriesUsingMftAsync(
            new GitWizardConfiguration(), new List<string>(), noMft: true);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.Index, Is.Null);
        }
    }

    [Test]
    public async Task Discovery_EmptySearchPaths_DoesNotInvokeConnectionFactory()
    {
        var calls = 0;
        var paths = new List<string>();
        await using var result = await GitWizardApi.DiscoverRepositoriesWithIndexAsync(
            new GitWizardConfiguration(), paths, [], [], new IndexDiscoveryComposition(
                _ =>
                {
                    calls++;
                    throw new AssertionException("Empty search paths may not connect");
                },
                _ => true));
        Assert.That(result.Success, Is.False);
        var detached = result.DetachIndexSession();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(detached, Is.Null);
            Assert.That(calls, Is.Zero);
            Assert.That(paths, Is.Empty);
        }
    }

    [Test]
    public async Task Discovery_NoReposFound_DisposesOwnerAndReturnsFalse()
    {
        await using var fixture = new IndexOwnerFixture();
        await fixture.StartAsync();
        var paths = new List<string>();
        await using var result = await DiscoverAsync(fixture, paths, _ => false);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.Index, Is.Null);
            Assert.That(paths, Is.Empty);
            Assert.That(fixture.ConnectionDisposals, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task Discovery_PartialFailureKeepsSuccessfulDriveAndReportsError()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repo");
        var owner = new MftIndexSession([await files.OpenIndexAsync()], null, ["X", "Y"], new Dictionary<string, string> { ["Y"] = "scan denied" });
        var updates = new RecordingUpdateHandler();
        await using var result = await GitWizardApi.DiscoverRepositoriesWithIndexAsync(
            new GitWizardConfiguration { IncludeTempDirectories = true }, new List<string>(), [@"X:\", @"Y:\"], [],
            new IndexDiscoveryComposition(_ => Task.FromResult(owner), _ => true), updates);
        Assert.That(result.Success, Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(updates.Messages, Has.Some.Contains("Y: scan denied"));
            Assert.That(result.Index!.Search(new SearchQuery(".git", NameMatchMode.Exact)).Single().Parent!.Value.Name, Is.EqualTo("repo"));
            Assert.That(result.ScanErrors, Does.ContainKey("Y"));
        }
        Assert.That(result.ScanErrors["Y"], Is.EqualTo("scan denied"));
    }

    [Test]
    public async Task Discovery_AllDrivesFail_PreservesScanErrorsInResult()
    {
        using var files = new PackedIndexFixture();
        var owner = new MftIndexSession([await files.OpenIndexAsync()], null, ["X"], new Dictionary<string, string> { ["X"] = "Timed out waiting for broker to connect" });
        await using var result = await GitWizardApi.DiscoverRepositoriesWithIndexAsync(
            new GitWizardConfiguration(), new List<string>(), [@"X:\"], [],
            new IndexDiscoveryComposition(_ => Task.FromResult(owner), _ => true));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.ScanErrors, Does.ContainKey("X"));
        }
        Assert.That(result.ScanErrors["X"], Is.EqualTo("Timed out waiting for broker to connect"));
    }

    [Test]
    public async Task Discovery_FailureAfterOpenKeepsDriveStatusesAndScanErrors()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repo");
        var owner = new MftIndexSession([await files.OpenIndexAsync()], null, ["X", "Y"], new Dictionary<string, string> { ["Y"] = "scan denied" });
        var expected = owner.CaptureRootStatuses();
        await using var result = await GitWizardApi.DiscoverRepositoriesWithIndexAsync(
            new GitWizardConfiguration { IncludeTempDirectories = true }, new List<string>(), [@"X:\", @"Y:\"], [],
            new IndexDiscoveryComposition(_ => Task.FromResult(owner),
                _ => throw new IOException("candidate probe failed")));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.Index, Is.Null);
            Assert.That(owner.IsDisposed, Is.True);
            Assert.That(result.RootStatuses, Is.EqualTo(expected));
            Assert.That(result.ScanErrors["Y"], Is.EqualTo("scan denied"));
            Assert.That(result.ScanErrors["*"], Is.EqualTo("candidate probe failed"));
        }
    }

    [Test]
    public void IsBrokerConnectionFailure_DistinguishesConnectionFailureFromPostConnectionFailure()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(TestUtilities.IsBrokerConnectionFailure(
                    "Timed out waiting 30s for the elevated broker to connect to pipe 'mftlib-broker-1'. The broker process was launched, but never connected (headless session, unserviced UAC prompt, or broker crash before connect)."), Is.True);
            Assert.That(TestUtilities.IsBrokerConnectionFailure(
                "Failed to launch the elevated broker (the UAC prompt was declined?)"), Is.True);
            Assert.That(TestUtilities.IsBrokerConnectionFailure(
                "Timed out waiting for broker to connect"), Is.True);

            Assert.That(TestUtilities.IsBrokerConnectionFailure(
                "Broker disconnected before block scan and catch-up completed."), Is.False);
            Assert.That(TestUtilities.IsBrokerConnectionFailure(
                "Truncated broker frame on pipe"), Is.False);
            Assert.That(TestUtilities.IsBrokerConnectionFailure(
                "The broker control connection is no longer usable."), Is.False);
        }
    }

    static Task<MftDiscoveryResult> DiscoverAsync(IndexOwnerFixture fixture, ICollection<string> paths,
        Func<string, bool>? candidateExists = null) => GitWizardApi.DiscoverRepositoriesWithIndexAsync(
        new GitWizardConfiguration { IncludeTempDirectories = true }, paths, [@"X:\"], [], new IndexDiscoveryComposition(
            _ => Task.FromResult(fixture.Owner), candidateExists ?? (_ => true)));

}

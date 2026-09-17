using GitWizard;
using GitWizard.Watch;
using GitWizardTests.TestSupport;
using MFTLib;

namespace GitWizardTests.Watch;

[TestFixture]
public class IndexVolumeChangeSourcePreparedIndexTests
{
    [Test]
    public async Task MatchingVolumes_AdoptsIndexWithoutOpening()
    {
        await using var fixture = new IndexOwnerFixture();
        await fixture.StartAsync();
        await using var source = new IndexVolumeChangeSource((_, _, _) =>
            throw new AssertionException("Prepared index must be reused"), fixture.Owner);
        var result = await source.ArmAndCatchUpAsync(["X"], CancellationToken.None);
        Assert.That(result.Errors, Is.Empty);
        Assert.That(fixture.Owner.IsDisposed, Is.False);
    }

    [Test]
    public Task VolumeMismatch_DisposesPreparedIndexAndFallsBack() => AssertFallbackAsync(false);

    [Test]
    public Task DisposedIndex_FallsBack() => AssertFallbackAsync(true);

    static async Task AssertFallbackAsync(bool disposeFirst)
    {
        await using var fixture = new IndexOwnerFixture();
        await fixture.StartAsync();
        await AssertFallbackCoreAsync(fixture, disposeFirst);
    }

    static async Task AssertFallbackCoreAsync(IndexOwnerFixture fixture, bool disposeFirst)
    {
        if (disposeFirst)
            await fixture.Owner.DisposeAsync();
        var opens = 0;
        await using var source = new IndexVolumeChangeSource((drives, _, _) =>
        {
            opens++;
            Assert.That(fixture.Owner.IsDisposed, Is.True);
            Assert.That(drives, Is.EqualTo(disposeFirst ? new[] { "X" } : new[] { "X", "Y" }));
            throw new InvalidOperationException("Fresh open failed");
        }, fixture.Owner);
        Assert.ThrowsAsync<InvalidOperationException>(() => source.ArmAndCatchUpAsync(
            disposeFirst ? ["X"] : ["X", "Y"], CancellationToken.None));
        Assert.That(opens, Is.EqualTo(1));
        Assert.That(fixture.ConnectionDisposals, Is.EqualTo(1));
    }

    [Test]
    public async Task NeverArmed_DisposesPreparedIndex()
    {
        await using var fixture = new IndexOwnerFixture();
        await fixture.StartAsync();
        var source = new IndexVolumeChangeSource((_, _, _) =>
            throw new AssertionException("Must not open"), fixture.Owner);
        await source.DisposeAsync();
        await source.DisposeAsync();
        Assert.That(fixture.ConnectionDisposals, Is.EqualTo(1));
    }

    [Test]
    public async Task PreparedIndex_ReplaysFromArmedCursorWithResolvedPaths()
    {
        var entry = UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = 44,
            ParentRecordNumber = 42,
            SequenceNumber = 1,
            Usn = 1001,
            Timestamp = PackedIndexFixture.CompletionTimestamp,
            FileAttributes = FileAttributes.Directory,
            Reason = UsnReason.FileCreate,
            FileName = "child"
        });
        await using var broker = new BlockBrokerFixture([entry]);
        broker.WatchEntries = [entry];
        var owner = await MftIndexSession.OpenAsync([broker.Drive('X')], broker.ConnectAsync,
            broker.CacheDirectory, null, CancellationToken.None);
        await using var source = new IndexVolumeChangeSource((_, _, _) =>
            throw new AssertionException("Must reuse owner"), owner);
        await source.ArmAndCatchUpAsync(["X"], CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var reader = source.WatchAsync(timeout.Token).GetAsyncEnumerator();
        Assert.That(await reader.MoveNextAsync(), Is.True);
        Assert.That(reader.Current.Volume, Is.EqualTo("X"));
        // Routed through IndexVolumeChangeSource, which rewrites this back onto its own
        // configured drive letter (MFTLib #143), so this stays the pre-existing synthetic shape.
        Assert.That(reader.Current.Entries.Single().FullPath, Is.EqualTo(@"X:\code\repo\child"));
        Assert.That(owner.Index.FindByName("child").Single().Parent!.Value.Name, Is.EqualTo("repo"));
        Assert.That(broker.ConnectionCount, Is.EqualTo(1));
        var started = await broker.WatchStarted.WaitAsync(timeout.Token);
        Assert.That(started.DrivesSpec, Does.StartWith("X:100:1000:"));
    }
}

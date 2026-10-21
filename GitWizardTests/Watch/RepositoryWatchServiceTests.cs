using GitWizard;
using GitWizard.Watch;
using GitWizardTests.TestSupport;

namespace GitWizardTests.Watch;

// Watch tests are split by concern because the combined fixture exceeds 500 lines.
// Covers RepositoryWatchService (core event loop, batching, and debounce).
[TestFixture]
public class RepositoryWatchServiceTests
{
    static async Task<List<RepositoryChangeEvent>> DrainAsync(RepositoryWatchService svc)
    {
        var events = new List<RepositoryChangeEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var ev in svc.RunAsync(cts.Token))
            events.Add(ev);
        return events;
    }

    [Test]
    public async Task RunAsync_StartupBatches_DeliveredInOrderBeforeLiveBatches()
    {
        // Batches buffered while discovery armed and reconciled membership are classified first,
        // in journal order, through the same path as live batches.
        var startupBatches = new[]
        {
            new VolumeChangeBatch("C", new[] { new VolumeChangeEntry(@"C:\repo\a\buffered.txt", VolumeEntryKind.Modified) })
        };
        var source = new ScriptedVolumeChangeSource(new[]
        {
            new VolumeChangeBatch("C", new[] { new VolumeChangeEntry(@"C:\repo\b\live.txt", VolumeEntryKind.Modified) })
        });
        var request = new DiscoveryRequest(
            new GitWizardConfiguration(), DiscoveryPurpose.Watch, false, false, []);
        await using var startup = await FakeLiveStartup.FromSourceArmedAsync(
            source, request, CancellationToken.None,
            inventoryPaths: [@"C:\repo\a", @"C:\repo\b"],
            startupBatches: startupBatches);
        var service = new RepositoryWatchService(startup, debounce: TimeSpan.Zero);
        var events = new List<RepositoryChangeEvent>();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var changeEvent in service.RunAsync(cancellation.Token))
            events.Add(changeEvent);

        Assert.That(events, Is.EqualTo(new[]
        {
                new RepositoryChangeEvent(@"C:\repo\a", RepositoryChangeKind.Changed),
                new RepositoryChangeEvent(@"C:\repo\b", RepositoryChangeKind.Changed)
            }));
    }

    [Test]
    public async Task ModifiedUnderTrackedRoot_CoalescesToSingleChanged()
    {
        var batch = new VolumeChangeBatch("C", new[]
        {
                new VolumeChangeEntry(@"C:\repo\a\one.txt", VolumeEntryKind.Modified),
                new VolumeChangeEntry(@"C:\repo\a\two.txt", VolumeEntryKind.Modified),
            });
        var svc = new RepositoryWatchService(
            new ScriptedVolumeChangeSource(new[] { batch }),
            trackedRoots: new[] { @"C:\repo\a" },
            searchRoots: new[] { @"C:\repo" },
            debounce: TimeSpan.FromMilliseconds(10));

        var events = await DrainAsync(svc);

        Assert.That(events, Has.Count.EqualTo(1));
        Assert.That(events[0], Is.EqualTo(new RepositoryChangeEvent(@"C:\repo\a", RepositoryChangeKind.Changed)));
    }

    [Test]
    public async Task FileCreatedUnderTrackedRoot_EmitsChanged()
    {
        var batch = new VolumeChangeBatch("C", new[]
        {
                new VolumeChangeEntry(@"C:\repo\a\test.txt", VolumeEntryKind.Created),
            });
        var svc = new RepositoryWatchService(
            new ScriptedVolumeChangeSource(new[] { batch }),
            trackedRoots: new[] { @"C:\repo\a" },
            searchRoots: new[] { @"C:\repo" },
            debounce: TimeSpan.FromMilliseconds(10));

        var events = await DrainAsync(svc);

        Assert.That(events, Is.EqualTo(new[]
        {
                new RepositoryChangeEvent(@"C:\repo\a", RepositoryChangeKind.Changed),
            }));
    }

    [Test]
    public async Task DotGitCreatedUnderSearchRoot_EmitsCreated()
    {
        var svc = new RepositoryWatchService(
            new ScriptedVolumeChangeSource(
                new[] { new VolumeChangeBatch("C", new[]
                {
                    new VolumeChangeEntry(@"C:\repo\newrepo\.git", VolumeEntryKind.Created)
                    }) }),
            trackedRoots: Array.Empty<string>(),
            searchRoots: new[] { @"C:\repo" },
            debounce: TimeSpan.FromMilliseconds(10));

        var events = await DrainAsync(svc);

        Assert.That(events, Does.Contain(
            new RepositoryChangeEvent(@"C:\repo\newrepo", RepositoryChangeKind.Created)));
    }

    [Test]
    public async Task TrackedRootDotGitDeleted_EmitsDeleted()
    {
        var svc = new RepositoryWatchService(
            new ScriptedVolumeChangeSource(
                new[] { new VolumeChangeBatch("C", new[]
                {
                    new VolumeChangeEntry(@"C:\repo\gone\.git", VolumeEntryKind.Deleted)
                    }) }),
            trackedRoots: new[] { @"C:\repo\gone" },
            searchRoots: new[] { @"C:\repo" },
            debounce: TimeSpan.FromMilliseconds(10));

        var events = await DrainAsync(svc);

        Assert.That(events, Does.Contain(
            new RepositoryChangeEvent(@"C:\repo\gone", RepositoryChangeKind.Deleted)));
    }

    [Test]
    public async Task NoDebounceSpecified_UsesDefaultAndCompletesOnEmptyStream()
    {
        var svc = new RepositoryWatchService(
            new ScriptedVolumeChangeSource(Array.Empty<VolumeChangeBatch>()),
            trackedRoots: Array.Empty<string>(),
            searchRoots: Array.Empty<string>());


        var events = await DrainAsync(svc);

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task TrackedRootWithNoSearchRootsOnVolume_StillClassifiesChanges()
    {
        var batch = new VolumeChangeBatch("C", new[]
        {
                new VolumeChangeEntry(@"C:\repo\a\file.txt", VolumeEntryKind.Modified)
            });
        var svc = new RepositoryWatchService(
            new ScriptedVolumeChangeSource(new[] { batch }),
            trackedRoots: new[] { @"C:\repo\a" },
            searchRoots: Array.Empty<string>(),
            debounce: TimeSpan.FromMilliseconds(10));

        var events = await DrainAsync(svc);

        Assert.That(events, Has.Count.EqualTo(1));
        Assert.That(events[0], Is.EqualTo(new RepositoryChangeEvent(@"C:\repo\a", RepositoryChangeKind.Changed)));
    }

    [Test]
    public async Task ContinuousUnrelatedDriveActivity_DoesNotStarveRelevantChange()
    {
        var svc = new RepositoryWatchService(
            new BusyVolumeChangeSource(),
            trackedRoots: new[] { @"C:\repo\a" },
            searchRoots: new[] { @"C:\repo" },
            debounce: TimeSpan.FromMilliseconds(20));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var enumerator = svc.RunAsync(cts.Token).GetAsyncEnumerator(cts.Token);

        var hasEvent = await enumerator.MoveNextAsync();

        Assert.That(hasEvent, Is.True);
        Assert.That(enumerator.Current,
            Is.EqualTo(new RepositoryChangeEvent(@"C:\repo\a", RepositoryChangeKind.Changed)));
        await cts.CancelAsync();
    }

    [Test]
    public async Task BatchesAcrossDebounceWindow_EmitSeparateChangedEvents()
    {
        var batches = new[]
        {
                new VolumeChangeBatch("C", new[]
                {
                    new VolumeChangeEntry(@"C:\repo\a\one.txt", VolumeEntryKind.Modified)
                }),
                new VolumeChangeBatch("C", new[]
                {
                    new VolumeChangeEntry(@"C:\repo\a\two.txt", VolumeEntryKind.Modified)
                }),
            };
        var svc = new RepositoryWatchService(
            new DelayedVolumeChangeSource(batches, TimeSpan.FromMilliseconds(60)),
            trackedRoots: new[] { @"C:\repo\a" },
            searchRoots: new[] { @"C:\repo" },
            debounce: TimeSpan.FromMilliseconds(10));

        var events = await DrainAsync(svc);

        Assert.That(events, Has.Count.EqualTo(2));
        Assert.That(events, Has.All.EqualTo(new RepositoryChangeEvent(@"C:\repo\a", RepositoryChangeKind.Changed)));
    }

    // IVolumeChangeSource preserves ordered batches. FileIndex owns replay and live changes;
    // starting its pump is not a startup catch-up fence (MFTLib#139).
    // The service classifies an initial replay batch exactly as it does a later live batch.
    [Test]
    public async Task CatchUpStyleLeadingBatch_ClassifiesTheSameAsLiveBatch()
    {
        var catchUpStyleBatch = new VolumeChangeBatch("C", new[]
        {
                new VolumeChangeEntry(@"C:\repo\a\during-cold-scan.txt", VolumeEntryKind.Modified)
            });
        var liveBatch = new VolumeChangeBatch("C", new[]
        {
                new VolumeChangeEntry(@"C:\repo\b\newrepo\.git", VolumeEntryKind.Created)
            });
        var svc = new RepositoryWatchService(
            new ScriptedVolumeChangeSource(
                new[] { catchUpStyleBatch, liveBatch }),
            trackedRoots: new[] { @"C:\repo\a" },
            searchRoots: new[] { @"C:\repo\b" },
        debounce: TimeSpan.FromMilliseconds(10));

        var events = await DrainAsync(svc);

        Assert.That(events, Does.Contain(
            new RepositoryChangeEvent(@"C:\repo\a", RepositoryChangeKind.Changed)));
        Assert.That(events, Does.Contain(
            new RepositoryChangeEvent(@"C:\repo\b\newrepo", RepositoryChangeKind.Created)));
    }
}

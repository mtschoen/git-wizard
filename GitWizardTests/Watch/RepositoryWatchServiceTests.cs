using System.Runtime.CompilerServices;
using GitWizard.Watch;
using GitWizardTests.TestSupport;

namespace GitWizardTests.Watch;

// A scripted IVolumeChangeSource that spaces its batches apart in real time, used only to
// exercise RepositoryWatchService's debounce-window flush (FakeVolumeChangeSource yields its
// batches back-to-back, which never lets a debounce window elapse mid-stream).
internal sealed class DelayedVolumeChangeSource : IVolumeChangeSource
{
    readonly IReadOnlyList<VolumeChangeBatch> _batches;
    readonly TimeSpan _delayBetweenBatches;

    // Explicit, empty-bodied implementation: this double never dies, so it never raises the
    // event, but a plain field-like `event` declaration that's never invoked triggers CS0067.
    event Action<string>? IVolumeChangeSource.SourceDied
    {
        add { }
        remove { }
    }

    public DelayedVolumeChangeSource(IReadOnlyList<VolumeChangeBatch> batches, TimeSpan delayBetweenBatches)
    {
        _batches = batches;
        _delayBetweenBatches = delayBetweenBatches;
    }

    public Task<VolumeArmResult> ArmAndCatchUpAsync(
        IReadOnlyCollection<string> volumes, CancellationToken ct) =>
        Task.FromResult(new VolumeArmResult(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), []));

    public async IAsyncEnumerable<VolumeChangeBatch> WatchAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var batch in _batches)
        {
            await Task.Delay(_delayBetweenBatches, ct);
            yield return batch;
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

// Emits one relevant change followed by continuous unrelated drive activity. A global
// idle-based debounce never flushes this stream; a fixed relevant-change window does.
internal sealed class BusyVolumeChangeSource : IVolumeChangeSource
{
    event Action<string>? IVolumeChangeSource.SourceDied
    {
        add { }
        remove { }
    }

    public Task<VolumeArmResult> ArmAndCatchUpAsync(
        IReadOnlyCollection<string> volumes, CancellationToken ct) =>
        Task.FromResult(new VolumeArmResult(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), []));

    public async IAsyncEnumerable<VolumeChangeBatch> WatchAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new VolumeChangeBatch("C",
            new[] { new VolumeChangeEntry(@"C:\repo\a\test.txt", VolumeEntryKind.Created) });

        for (var index = 0; index < 1000; index++)
        {
            await Task.Delay(5, ct);
            yield return new VolumeChangeBatch("C",
                new[] { new VolumeChangeEntry(@"C:\unrelated\noise.tmp", VolumeEntryKind.Modified) });
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

// An IVolumeChangeSource whose WatchAsync never resolves on its own - only cancellation (via
// RepositoryWatchService reacting to KillSource) can unblock it. Used to exercise the
// source-death path deterministically: since no batch is ever classified, there is no pending
// change whose flush-vs-drop outcome could race the cancellation, unlike scripting a real
// batch through FakeVolumeChangeSource and killing the source immediately after.
internal sealed class HangingVolumeChangeSource : IVolumeChangeSource
{
    public event Action<string>? SourceDied;

    public Task<VolumeArmResult> ArmAndCatchUpAsync(
        IReadOnlyCollection<string> volumes, CancellationToken ct) =>
        Task.FromResult(new VolumeArmResult(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), []));

    public async IAsyncEnumerable<VolumeChangeBatch> WatchAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        yield break;
    }

    public void KillSource(string reason) => SourceDied?.Invoke(reason);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

[TestFixture]
public partial class RepositoryWatchServiceTests
{
    [Test]
    public async Task DriveFailureKeepsHealthyEvents()
    {
        await using var source = new DiagnosticVolumeChangeSource();
        var service = new RepositoryWatchService(source, [@"X:\repo", @"Y:\repo"], [], TimeSpan.Zero);
        var updates = new List<IReadOnlyDictionary<string, string>>();
        service.ScanErrorsChanged += () => updates.Add(service.ScanErrors);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var reader = service.RunAsync(cancellation.Token).GetAsyncEnumerator();
        var pending = reader.MoveNextAsync().AsTask();
        try
        {
            await source.Started;
            var previous = service.ScanErrors;
            source.FailDrive("X", "watch failed");
            Assert.That(service.ScanErrors, Does.ContainKey("X").WithValue("watch failed"));
            Assert.That(updates.Last(), Is.SameAs(service.ScanErrors));
            Assert.That(previous, Is.Empty);
            var firstFailure = service.ScanErrors;
            source.FailDrive("X", "changed failure");
            Assert.That(firstFailure["X"], Is.EqualTo("watch failed"));
            Assert.That(service.ScanErrors["X"], Is.EqualTo("changed failure"));
            Assert.Throws<NotSupportedException>(() =>
                ((IDictionary<string, string>)service.ScanErrors).Add("Z", "mutation"));
            source.Publish(new VolumeChangeBatch("Y",
                [new VolumeChangeEntry(@"Y:\repo\file", VolumeEntryKind.Modified)]));
            Assert.That(await pending, Is.True);
            Assert.That(reader.Current.RepoRoot, Is.EqualTo(@"Y:\repo"));
        }
        finally
        {
            source.Complete();
            try
            {
                await pending;
            }
            finally
            {
                await reader.DisposeAsync();
            }
        }
        Assert.That(source.DiagnosticSubscribers, Is.Zero);
        await WatchFailureAssertions.VerifyQueryableIndexAsync();
        await WatchFailureAssertions.VerifyShutdownAsync();
        await WatchFailureAssertions.VerifyArmingSnapshotAsync();
        await WatchFailureAssertions.VerifyEarlyStopAsync();
    }

    static async Task<List<RepositoryChangeEvent>> DrainAsync(RepositoryWatchService svc)
    {
        var events = new List<RepositoryChangeEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var ev in svc.RunAsync(cts.Token))
            events.Add(ev);
        return events;
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
            new FakeVolumeChangeSource(new[] { batch }),
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
            new FakeVolumeChangeSource(new[] { batch }),
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
            new FakeVolumeChangeSource(
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
            new FakeVolumeChangeSource(
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
            new FakeVolumeChangeSource(Array.Empty<VolumeChangeBatch>()),
            trackedRoots: Array.Empty<string>(),
            searchRoots: Array.Empty<string>());

        var events = await DrainAsync(svc);

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task RootWithoutDriveLetter_IsIgnoredWhenGroupingByVolume()
    {
        var svc = new RepositoryWatchService(
            new FakeVolumeChangeSource(Array.Empty<VolumeChangeBatch>()),
            trackedRoots: new[] { "relative-repo" },
            searchRoots: Array.Empty<string>(),
            debounce: TimeSpan.FromMilliseconds(10));

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
            new FakeVolumeChangeSource(new[] { batch }),
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
    public async Task PosixTrackedRoot_EmitsEventWithOriginalPath()
    {
        var batch = new VolumeChangeBatch("/", new[]
        {
            new VolumeChangeEntry("/repo/a/file.txt", VolumeEntryKind.Modified)
        });
        var svc = new RepositoryWatchService(
            new FakeVolumeChangeSource(new[] { batch }),
            trackedRoots: new[] { "/repo/a" },
            searchRoots: Array.Empty<string>(),
            debounce: TimeSpan.FromMilliseconds(10));

        var events = await DrainAsync(svc);

        Assert.That(events, Is.EqualTo(new[]
        {
            new RepositoryChangeEvent("/repo/a", RepositoryChangeKind.Changed)
        }));
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

    [Test]
    public async Task ArmErrors_AreExposedAsScanErrorsAfterRun()
    {
        var errors = new Dictionary<string, string> { ["D"] = "drive not ready" };
        var svc = new RepositoryWatchService(
            new FakeVolumeChangeSource(
                Array.Empty<VolumeChangeBatch>(), errors),
            trackedRoots: Array.Empty<string>(),
            searchRoots: Array.Empty<string>(),
            debounce: TimeSpan.FromMilliseconds(10));

        await DrainAsync(svc);

        Assert.That(svc.ScanErrors, Does.ContainKey("D").WithValue("drive not ready"));
    }

    [Test]
    public async Task NoArmErrors_ScanErrorsIsEmptyAfterRun()
    {
        var svc = new RepositoryWatchService(
            new FakeVolumeChangeSource(Array.Empty<VolumeChangeBatch>()),
            trackedRoots: Array.Empty<string>(),
            searchRoots: Array.Empty<string>(),
            debounce: TimeSpan.FromMilliseconds(10));

        await DrainAsync(svc);

        Assert.That(svc.ScanErrors, Is.Empty);
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
            new FakeVolumeChangeSource(
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

    [Test]
    public async Task SourceDies_RaisesStoppedAndCompletesEnumeration()
    {
        var source = new HangingVolumeChangeSource();
        var svc = new RepositoryWatchService(
            source,
            trackedRoots: Array.Empty<string>(),
            searchRoots: Array.Empty<string>(),
            debounce: TimeSpan.FromMilliseconds(10));

        string? reason = null;
        svc.Stopped += r => reason = r;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var enumerator = svc.RunAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        var moveNextTask = enumerator.MoveNextAsync();

        source.KillSource("volume unmounted");

        var hasNext = await moveNextTask;

        Assert.That(reason, Is.EqualTo("volume unmounted"));
        Assert.That(hasNext, Is.False);
    }

    [Test]
    public async Task SourceDies_WithNoStoppedSubscriber_StillCompletesEnumeration()
    {
        var source = new HangingVolumeChangeSource();
        var svc = new RepositoryWatchService(
            source,
            trackedRoots: Array.Empty<string>(),
            searchRoots: Array.Empty<string>(),
            debounce: TimeSpan.FromMilliseconds(10));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var enumerator = svc.RunAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        var moveNextTask = enumerator.MoveNextAsync();

        source.KillSource("volume unmounted");

        var hasNext = await moveNextTask;

        Assert.That(hasNext, Is.False);
    }

    [Test]
    public async Task LiveDiscoveredRepository_SubsequentEdit_EmitsChanged()
    {
        var batches = new[]
        {
            new VolumeChangeBatch("C", new[]
            {
                new VolumeChangeEntry(@"C:\repo\newrepo\.git", VolumeEntryKind.Created)
            }),
            new VolumeChangeBatch("C", new[]
            {
                new VolumeChangeEntry(@"C:\repo\newrepo\a.txt", VolumeEntryKind.Modified)
            }),
        };
        var svc = new RepositoryWatchService(
            new FakeVolumeChangeSource(batches),
            trackedRoots: Array.Empty<string>(),
            searchRoots: new[] { @"C:\repo" },
            debounce: TimeSpan.FromMilliseconds(10));

        var events = await DrainAsync(svc);

        Assert.That(events, Does.Contain(
            new RepositoryChangeEvent(@"C:\repo\newrepo", RepositoryChangeKind.Created)));
        Assert.That(events, Does.Contain(
            new RepositoryChangeEvent(@"C:\repo\newrepo", RepositoryChangeKind.Changed)));
    }

    [Test]
    public async Task TrackedRootDeletedThenRecreated_EmitsDeletedThenCreatedAgain()
    {
        var batches = new[]
        {
            new VolumeChangeBatch("C", new[]
            {
                new VolumeChangeEntry(@"C:\repo\gone\.git", VolumeEntryKind.Deleted)
            }),
            new VolumeChangeBatch("C", new[]
            {
                new VolumeChangeEntry(@"C:\repo\gone\.git", VolumeEntryKind.Created)
            }),
            new VolumeChangeBatch("C", new[]
            {
                new VolumeChangeEntry(@"C:\repo\gone\a.txt", VolumeEntryKind.Modified)
            }),
        };
        var svc = new RepositoryWatchService(
            new FakeVolumeChangeSource(batches),
            trackedRoots: new[] { @"C:\repo\gone" },
            searchRoots: new[] { @"C:\repo" },
            debounce: TimeSpan.FromMilliseconds(10));

        var events = await DrainAsync(svc);

        Assert.That(events, Is.EqualTo(new[]
        {
            new RepositoryChangeEvent(@"C:\repo\gone", RepositoryChangeKind.Deleted),
            new RepositoryChangeEvent(@"C:\repo\gone", RepositoryChangeKind.Created),
            new RepositoryChangeEvent(@"C:\repo\gone", RepositoryChangeKind.Changed),
        }));
    }

    [Test]
    public async Task TrackedRootDeletedThenRecreatedInSameBatch_EmitsDeletedThenCreatedAndRemainsTracked()
    {
        var batches = new[]
        {
            new VolumeChangeBatch("C", new[]
            {
                new VolumeChangeEntry(@"C:\repo\lifecycle\.git", VolumeEntryKind.Deleted),
                new VolumeChangeEntry(@"C:\repo\lifecycle\.git", VolumeEntryKind.Created),
            }),
            new VolumeChangeBatch("C", new[]
            {
                new VolumeChangeEntry(@"C:\repo\lifecycle\file.txt", VolumeEntryKind.Modified)
            }),
        };
        var svc = new RepositoryWatchService(
            new FakeVolumeChangeSource(batches),
            trackedRoots: new[] { @"C:\repo\lifecycle" },
            searchRoots: new[] { @"C:\repo" },
            debounce: TimeSpan.FromMilliseconds(10));

        var events = await DrainAsync(svc);

        Assert.That(events, Is.EqualTo(new[]
        {
            new RepositoryChangeEvent(@"C:\repo\lifecycle", RepositoryChangeKind.Deleted),
            new RepositoryChangeEvent(@"C:\repo\lifecycle", RepositoryChangeKind.Created),
            new RepositoryChangeEvent(@"C:\repo\lifecycle", RepositoryChangeKind.Changed),
        }));
    }

    [Test]
    public async Task TrackedRootDeletedThenRecreatedAndModifiedInSameBatch_EmitsInOrder()
    {
        var batch = new VolumeChangeBatch("C", new[]
        {
            new VolumeChangeEntry(@"C:\repo\lifecycle\.git", VolumeEntryKind.Deleted),
            new VolumeChangeEntry(@"C:\repo\lifecycle\.git", VolumeEntryKind.Created),
            new VolumeChangeEntry(@"C:\repo\lifecycle\file.txt", VolumeEntryKind.Modified),
        });
        var svc = new RepositoryWatchService(
            new FakeVolumeChangeSource(new[] { batch }),
            trackedRoots: new[] { @"C:\repo\lifecycle" },
            searchRoots: new[] { @"C:\repo" },
            debounce: TimeSpan.FromMilliseconds(10));

        var events = await DrainAsync(svc);

        Assert.That(events, Is.EqualTo(new[]
        {
            new RepositoryChangeEvent(@"C:\repo\lifecycle", RepositoryChangeKind.Deleted),
            new RepositoryChangeEvent(@"C:\repo\lifecycle", RepositoryChangeKind.Created),
            new RepositoryChangeEvent(@"C:\repo\lifecycle", RepositoryChangeKind.Changed),
        }));
    }
}

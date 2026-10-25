using GitWizard;
using GitWizard.Watch;
using GitWizardTests.TestSupport;
using MFTLib.Index;

namespace GitWizardTests.Watch;

// Watch tests are split by concern because the combined fixture exceeds 500 lines.
// Covers RepositoryWatchService (diagnostics, warnings, and source termination).
[TestFixture]
public class RepositoryWatchServiceDiagnosticsTests
{
    static async Task DrainAsync(RepositoryWatchService svc)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var _ in svc.RunAsync(cts.Token)) { }
    }

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
            using (Assert.EnterMultipleScope())
            {
                Assert.That(service.ScanErrors, Does.ContainKey("X").WithValue("watch failed"));
                Assert.That(updates[^1], Is.SameAs(service.ScanErrors));
                Assert.That(previous, Is.Empty);
            }
            var firstFailure = service.ScanErrors;
            source.FailDrive("X", "changed failure");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(firstFailure["X"], Is.EqualTo("watch failed"));
                Assert.That(service.ScanErrors["X"], Is.EqualTo("changed failure"));
            }
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

    [Test]
    public async Task JournalWarningDetectedMidRun_ReplacesArmWarningAndNotifies()
    {
        await using var source = new DiagnosticVolumeChangeSource();
        var armLoss = FakeJournalSettingsSource.TrimmedLoss('X', 30L * 1024 * 1024, 60L * 1024 * 1024, 10L * 1024 * 1024);
        source.ArmWarnings.Add(new JournalWarning { DriveLetter = "X", VolumeSerial = 12345, Loss = armLoss });
        var service = new RepositoryWatchService(source, [@"X:\repo"], [], TimeSpan.Zero);
        var notifications = 0;
        service.ScanWarningsChanged += () => notifications++;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var reader = service.RunAsync(cancellation.Token).GetAsyncEnumerator();
        var pending = reader.MoveNextAsync().AsTask();
        try
        {
            await source.Started;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(service.ScanWarnings.Single().Loss, Is.SameAs(armLoss));
                Assert.That(notifications, Is.EqualTo(1));
            }

            var liveLoss = FakeJournalSettingsSource.TrimmedLoss('X', 30L * 1024 * 1024, 60L * 1024 * 1024,
                10L * 1024 * 1024, JournalCheckpointLossDetection.LiveWatch);
            source.RaiseJournalWarning(new JournalWarning { DriveLetter = "x", VolumeSerial = 12345, Loss = liveLoss });

            using (Assert.EnterMultipleScope())
            {
                // The live report is the newer fact about the same drive, so it replaces the
                // arm-time one instead of sitting beside it, matched case-insensitively.
                Assert.That(service.ScanWarnings.Single().Loss, Is.SameAs(liveLoss));
                Assert.That(notifications, Is.EqualTo(2));
            }
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
    }

    [TestCase(true, false, TestName = "StartupHandoff_Prepared_InitialPublication")]
    [TestCase(true, true, TestName = "StartupHandoff_Prepared_StateRefresh")]
    [TestCase(false, false, TestName = "StartupHandoff_Direct_InitialPublication")]
    [TestCase(false, true, TestName = "StartupHandoff_Direct_StateRefresh")]
    public async Task StartupHandoff_ConcurrentWarning_PreservesOrderingWithoutDeadlock(
            bool preparedStartup, bool isStateRefresh)
    {
        var source = new DiagnosticVolumeChangeSource();
        var barrier = new Barrier(2);
        var armLoss = FakeJournalSettingsSource.TrimmedLoss('C', 30L * 1024 * 1024, 60L * 1024 * 1024, 10L * 1024 * 1024);
        var initialWarning = new JournalWarning
        {
            DriveLetter = "C",
            VolumeSerial = 12345,
            Loss = armLoss,
            WatchState = isStateRefresh ? WatchState.Recovering : WatchState.NotStarted
        };

        if (isStateRefresh)
        {
            source.ArmWarnings.Add(initialWarning);
            source.CurrentWarnings.Add(initialWarning);
        }

        var refreshedWarning = initialWarning with
        {
            WatchState = isStateRefresh ? WatchState.CaughtUp : WatchState.Recovering
        };

        source.HandoffBarrier = barrier;

        RepositoryWatchService service;
        WatchStartupResult? startup = null;
        if (preparedStartup)
        {
            var request = new DiscoveryRequest(new GitWizardConfiguration(), DiscoveryPurpose.Watch, false, false, []);
            var armResult = new VolumeArmResult(new Dictionary<string, string>(), isStateRefresh ? [initialWarning] : []);
            var scope = DiscoveryScope.CreateExpanded(request, [], [@"C:\repo"], [],
                root => new RootClassification(root, RootFileSystem.Ntfs, "C", null));
            var inventory = new DiscoveryInventory
            {
                Outcome = DiscoveryOutcome.CompleteNonempty,
                Route = DiscoveryRoute.Indexed,
                Paths = [@"C:\repo"],
                RequestedNtfsDrives = ["C"],
                Observations = [],
                DriveStatuses = [],
                Errors = new Dictionary<string, string>(),
                WatchExcludedRoots = []
            };
            startup = new WatchStartupResult(inventory, armResult, source, ["C"], scope);
            service = new RepositoryWatchService(startup, debounce: TimeSpan.Zero);
        }
        else
        {
            service = new RepositoryWatchService(source, [@"C:\repo"], [], TimeSpan.Zero);
        }

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var reader = service.RunAsync(cancellation.Token).GetAsyncEnumerator();

        var publisherTask = Task.Run(() => source.RaiseJournalWarning(refreshedWarning));
        var startupTask = reader.MoveNextAsync().AsTask();

        try
        {
            await Task.WhenAll(publisherTask, source.Started).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.That(service.ScanWarnings, Has.Count.EqualTo(1));
            var warning = service.ScanWarnings.Single();
            Assert.That(warning.DriveLetter, Is.EqualTo("C").IgnoreCase);
            Assert.That(warning.WatchState, Is.EqualTo(refreshedWarning.WatchState));
        }
        finally
        {
            source.HandoffBarrier = null;
            startup?.DetachSource();
            source.Complete();
            try { await startupTask; } catch (OperationCanceledException) { }
            try { await publisherTask; } catch (OperationCanceledException) { }
            await reader.DisposeAsync();
            barrier.Dispose();
            await source.DisposeAsync();
        }
    }

    [Test]
    public async Task ArmErrors_AreExposedAsScanErrorsAfterRun()
    {
        var errors = new Dictionary<string, string> { ["D"] = "drive not ready" };
        var svc = new RepositoryWatchService(
            new ScriptedVolumeChangeSource(
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
            new ScriptedVolumeChangeSource(Array.Empty<VolumeChangeBatch>()),
            trackedRoots: Array.Empty<string>(),
            searchRoots: Array.Empty<string>(),
            debounce: TimeSpan.FromMilliseconds(10));

        await DrainAsync(svc);

        Assert.That(svc.ScanErrors, Is.Empty);
    }

    [Test]
    public async Task SourceDies_RaisesStoppedAndCompletesEnumeration()
    {
        var source = new ScriptedVolumeChangeSource { KeepWatching = true };
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reason, Is.EqualTo("volume unmounted"));
            Assert.That(hasNext, Is.False);
        }
    }

    [Test]
    public async Task SourceDies_WithNoStoppedSubscriber_StillCompletesEnumeration()
    {
        var source = new ScriptedVolumeChangeSource { KeepWatching = true };
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
}

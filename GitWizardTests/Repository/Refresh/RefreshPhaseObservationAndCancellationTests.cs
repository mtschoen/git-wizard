using System.Collections.Concurrent;
using GitWizard;
using GitWizardTests.TestSupport;

namespace GitWizardTests;

/// <summary>
/// Covers docs/refresh-concurrency-review.md hazard 7 (running Refresh inline on the
/// Parallel.ForEach worker without blocking Task.Run + Wait to eliminate thread-pool starvation)
/// and phase 2 cooperative cancellation between refresh phases.
/// </summary>
public class RefreshPhaseObservationAndCancellationTests
{
    [Test]
    public void Refresh_MoreRepositoriesThanPoolThreads_AllReachPhase1WithoutTimeout()
    {
        GitWizardLog.SilentMode = true;
        var repositoryCount = Math.Max(8, Environment.ProcessorCount * 2);
        var fixtures = new List<TempRepoFixture>();

        try
        {
            var paths = new SortedSet<string>();
            for (var i = 0; i < repositoryCount; i++)
            {
                var fixture = TempRepoFixture.CreateWithInitialCommit();
                fixtures.Add(fixture);
                paths.Add(fixture.Path);
            }

            var configuration = GitWizardConfiguration.CreateDefaultConfiguration();
            var report = new GitWizardReport(configuration);
            var reachedPhase1 = new ConcurrentBag<string>();

            // Pre-seed repositories with the phase observer hook to simulate a slow phase 0
            // and verify that every repository reaches phase 1 without thread-pool starvation.
            foreach (var path in paths)
            {
                var repository = new GitWizardRepository(path);
                var capturedPath = path;
                repository.PhaseObserverForTesting = phaseIndex =>
                {
                    if (phaseIndex == 0)
                    {
                        // Deliberate scheduling nudge so workers spend time in phase 0 rather than
                        // completing instantly, widening any thread-pool scheduling contention.
                        using var delayEvt = new ManualResetEventSlim(false);
                        delayEvt.Wait(5);
                    }
                    else if (phaseIndex == 1)
                    {
                        reachedPhase1.Add(capturedPath);
                    }
                };
                report.Repositories[path] = repository;
            }

            report.Refresh(paths);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(reachedPhase1, Has.Count.EqualTo(repositoryCount),
                            $"Expected all {repositoryCount} repositories to reach phase 1. Reached: {reachedPhase1.Count}");
                Assert.That(report.Repositories.Values.Select(repository => repository.RefreshError), Has.All.Null,
                    "Expected no repository to report a timeout or error.");
            }
        }
        finally
        {
            foreach (var fixture in fixtures)
            {
                fixture.Dispose();
            }
        }
    }

    [Test]
    public void Refresh_Cancelled_StopsAtPhaseBoundary()
    {
        GitWizardLog.SilentMode = true;
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repository = new GitWizardRepository(fixture.Path);

        using var observer = new PhaseCancellationObserver();
        repository.PhaseObserverForTesting = observer.OnPhase;

        repository.Refresh(cancellationToken: observer.Token);

        Assert.That(observer.ObservedPhases, Does.Contain(2), "Expected phase 2 to be reached before cancellation.");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(observer.ObservedPhases, Does.Not.Contain(3), "Expected phase 3 to never run after cancellation.");
            Assert.That(repository.RefreshError, Is.EqualTo("Timed out"));
            Assert.That(repository.IsRefreshing, Is.False);
        }
    }

    sealed class PhaseCancellationObserver : IDisposable
    {
        readonly CancellationTokenSource _cancellationTokenSource = new();

        public List<int> ObservedPhases { get; } = [];

        public CancellationToken Token => _cancellationTokenSource.Token;

        public void OnPhase(int phaseIndex)
        {
            ObservedPhases.Add(phaseIndex);
            if (phaseIndex == 2)
            {
                _ = _cancellationTokenSource.CancelAsync();
            }
        }

        public void Dispose()
        {
            _cancellationTokenSource.Dispose();
        }
    }

    [Test]
    public void Refresh_PreCancelledToken_DoesNotRunPhases()
    {
        GitWizardLog.SilentMode = true;
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repository = new GitWizardRepository(fixture.Path);

        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();

        var observedPhases = new List<int>();
        repository.PhaseObserverForTesting = observedPhases.Add;

        repository.Refresh(cancellationToken: cancellationTokenSource.Token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(observedPhases, Is.Empty, "Expected no refresh phase to execute on a pre-cancelled token.");
            Assert.That(repository.RefreshError, Is.EqualTo("Timed out"));
            Assert.That(repository.IsRefreshing, Is.False);
        }
    }

    [Test]
    public void Refresh_TimedOutViaRefreshTimeout_MarksRefreshFailed()
    {
        GitWizardLog.SilentMode = true;
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var previousTimeout = GitWizardReport.RefreshTimeout;

        try
        {
            // Configure a small timeout so the token cancels during refresh
            GitWizardReport.RefreshTimeout = TimeSpan.FromMilliseconds(1);

            var configuration = GitWizardConfiguration.CreateDefaultConfiguration();
            var report = new GitWizardReport(configuration);
            var repository = new GitWizardRepository(fixture.Path);

            repository.PhaseObserverForTesting = _ =>
            {
                // Give the 1ms timer time to expire
                using var delayEvt = new ManualResetEventSlim(false);
                delayEvt.Wait(10);
            };

            report.Repositories[fixture.Path] = repository;
            report.Refresh(new SortedSet<string> { fixture.Path });

            using (Assert.EnterMultipleScope())
            {
                Assert.That(repository.RefreshError, Is.EqualTo("Timed out"));
                Assert.That(repository.IsRefreshing, Is.False);
            }
        }
        finally
        {
            GitWizardReport.RefreshTimeout = previousTimeout;
        }
    }

    /// <summary>
    /// Cancels from inside <see cref="IUpdateHandler.OnSubmoduleCreated"/>, which fires from
    /// inside the <c>Parallel.ForEach</c> loop in <c>RefreshSubmodules</c> - i.e. after that
    /// phase has genuinely been entered and has processed at least one submodule - rather than
    /// from <c>PhaseObserverForTesting</c>, which fires before the phase's own
    /// <c>ThrowIfCancellationRequested</c> guard and so would cancel before the phase ever ran.
    /// Verifies both the parent and the newly created submodule repository observe the
    /// cancellation and stop.
    /// </summary>
    [Test]
    public void Refresh_CancelledInsideSubmoduleLoop_StopsParentAndChild()
    {
        GitWizardLog.SilentMode = true;
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddInitializedSubmodule("sub");
        var repository = new GitWizardRepository(fixture.Path);

        using var cancellationTokenSource = new CancellationTokenSource();
        Action cancel = cancellationTokenSource.Cancel;
        var handler = new RecordingUpdateHandler
        {
            CreatingSubmodule = (_, _) => cancel()
        };

        var observedPhases = new List<int>();
        repository.PhaseObserverForTesting = observedPhases.Add;

        repository.Refresh(handler, cancellationToken: cancellationTokenSource.Token);

        Assert.That(handler.SubmodulesCreated, Has.Length.EqualTo(1),
                "Expected the submodule-created callback to fire from inside the RefreshSubmodules loop before cancellation stopped further work.");
        Assert.That(observedPhases, Does.Contain(0), "Expected the submodule refresh phase to be entered.");

        var submodule = handler.SubmodulesCreated.SingleOrDefault().Submodule;
        Assert.That(submodule, Is.Not.Null, "Expected the created submodule repository to be captured by the handler.");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(submodule!.RefreshError, Is.EqualTo("Timed out"),
                    "Expected the submodule's own Refresh call to observe the token cancelled by the callback and stop immediately.");
            Assert.That(submodule.IsRefreshing, Is.False);

            Assert.That(repository.RefreshError, Is.EqualTo("Timed out"));
            Assert.That(repository.IsRefreshing, Is.False);
        }
    }

    /// <summary>
    /// Cancels from inside <see cref="IUpdateHandler.OnWorktreeCreated"/>, which fires from
    /// inside the <c>Parallel.ForEach</c> loop in <c>RefreshWorktrees</c> - i.e. after that
    /// phase has genuinely been entered and has processed at least one worktree - rather than
    /// from <c>PhaseObserverForTesting</c>, which fires before the phase's own
    /// <c>ThrowIfCancellationRequested</c> guard and so would cancel before the phase ever ran.
    /// Verifies both the parent and the newly created worktree repository observe the
    /// cancellation and stop.
    /// </summary>
    [Test]
    public void Refresh_CancelledInsideWorktreeLoop_StopsParentAndChild()
    {
        GitWizardLog.SilentMode = true;
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddWorktree("test-wt");
        var repository = new GitWizardRepository(fixture.Path);

        using var cancellationTokenSource = new CancellationTokenSource();
        Action cancel = cancellationTokenSource.Cancel;
        var handler = new RecordingUpdateHandler
        {
            CreatingWorktree = _ => cancel()
        };

        var observedPhases = new List<int>();
        repository.PhaseObserverForTesting = observedPhases.Add;

        repository.Refresh(handler, cancellationToken: cancellationTokenSource.Token);

        Assert.That(handler.WorktreesCreated, Has.Length.EqualTo(1),
                "Expected the worktree-created callback to fire from inside the RefreshWorktrees loop before cancellation stopped further work.");
        Assert.That(observedPhases, Does.Contain(2), "Expected the worktree refresh phase to be entered.");

        var worktree = handler.WorktreesCreated.SingleOrDefault();
        Assert.That(worktree, Is.Not.Null, "Expected the created worktree repository to be captured by the handler.");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(worktree!.RefreshError, Is.EqualTo("Timed out"),
                    "Expected the worktree's own Refresh call to observe the token cancelled by the callback and stop immediately.");
            Assert.That(worktree.IsRefreshing, Is.False);

            Assert.That(repository.RefreshError, Is.EqualTo("Timed out"));
            Assert.That(repository.IsRefreshing, Is.False);
        }
    }
}

using System.Collections.Concurrent;
using GitWizard;

namespace GitWizardTests;

/// <summary>
/// Covers docs/refresh-concurrency-review.md hazard 3 (Phase 1: per-repository refresh gate):
/// two overlapping <see cref="GitWizardRepository.Refresh"/> calls on the SAME repository object
/// (e.g. a Live-watch refresh racing a batch refresh) must serialize rather than interleave their
/// field writes.
/// </summary>
public class RefreshConcurrencyGateTests
{
    [Test]
    public void Refresh_ConcurrentCallsOnOneRepository_DoNotInterleave()
    {
        GitWizardLog.SilentMode = true;
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repository = new GitWizardRepository(fixture.Path);

        var phaseLog = new ConcurrentQueue<(int ThreadId, int PhaseIndex)>();
        repository.PhaseObserverForTesting = phaseIndex =>
        {
            phaseLog.Enqueue((Environment.CurrentManagedThreadId, phaseIndex));
            // Widen the race window so two concurrent Refresh calls on the same object overlap
            // deterministically on the pre-fix code path. The assertion below is entirely on the
            // recorded call PATTERN (thread hand-off count), never on elapsed time; this cannot be
            // replaced with a wait-for-condition because the gate under test is exactly what would
            // make that condition (both threads present) unreachable.
            // aislop-ignore-next-line ai-slop/test-sleep -- deliberate scheduling nudge, not a wall-clock assertion
            Thread.Sleep(5);
        };

        // Start both refreshes at the same instant so an unguarded Refresh has every chance to
        // interleave, rather than happening to run back-to-back by scheduling luck. A
        // TaskCompletionSource (not IDisposable) releases both waiting threads at once when
        // completed, so there is no disposable object captured by the two closures below.
        var startSignal = new TaskCompletionSource();
        void RunRefresh()
        {
            startSignal.Task.Wait();
            repository.Refresh();
        }

        var first = Task.Run(RunRefresh);
        var second = Task.Run(RunRefresh);
        startSignal.SetResult();
        Task.WaitAll(first, second);

        var sequence = phaseLog.ToArray();
        Assert.That(sequence, Is.Not.Empty, "Expected the phase observer to fire at least once per Refresh call.");

        var threadHandoffCount = 0;
        for (var i = 1; i < sequence.Length; i++)
        {
            if (sequence[i].ThreadId != sequence[i - 1].ThreadId)
                threadHandoffCount++;
        }

        // With the two Refresh calls serialized by the per-repository gate, one call's entire
        // phase sequence records before the other's begins, so the thread id changes exactly
        // once across the whole log. Any additional hand-off means the two runs interleaved.
        Assert.That(threadHandoffCount, Is.EqualTo(1),
            "Expected exactly one thread hand-off (one Refresh call's phases fully complete " +
            "before the other's begin). Recorded sequence: " +
            string.Join(", ", sequence.Select(entry => $"(thread {entry.ThreadId}, phase {entry.PhaseIndex})")));
    }
}

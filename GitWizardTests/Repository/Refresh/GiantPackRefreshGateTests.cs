using GitWizard;
using GitWizardTests.TestSupport;

namespace GitWizardTests;

public class GiantPackRefreshGateTests
{
    const long OneHundredMebibytes = 100L * 1024 * 1024;

    [Test]
    public void Refresh_PackIndexesOver100MiB_DoNotOverlap_Issue451()
    {
        Assert.That(RunTwoRepositoryRefresh(OneHundredMebibytes + 1), Is.EqualTo(1),
            "Two repositories with pack indexes over 100 MiB entered refresh together.");
    }

    [Test]
    public void Refresh_PackIndexesAt100MiB_CanOverlap_Issue451()
    {
        Assert.That(RunTwoRepositoryRefresh(OneHundredMebibytes), Is.EqualTo(2),
            "The strict over-100 MiB threshold must not serialize an index exactly at 100 MiB.");
    }

    [Test]
    public async Task Acquire_WaitingForGiantPack_ObservesCancellation_Issue451()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        CreatePackIndex(fixture.Path, OneHundredMebibytes + 1);
        using var heldGate = GiantPackRefreshGate.Acquire(fixture.Path, CancellationToken.None);
        using var cancellationTokenSource = new CancellationTokenSource();
        var attemptStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixturePath = fixture.Path;
        var cancellationToken = cancellationTokenSource.Token;

        var acquisition = Task.Run(() =>
        {
            attemptStarted.SetResult();
            Assert.Throws<OperationCanceledException>(() =>
                GiantPackRefreshGate.Acquire(fixturePath, cancellationToken));
        });

        await attemptStarted.Task.WaitAsync(TestTimeouts.ShortHangGuard);
        await cancellationTokenSource.CancelAsync();
        await acquisition.WaitAsync(TestTimeouts.ShortHangGuard);
    }

    static int RunTwoRepositoryRefresh(long packIndexBytes)
    {
        GitWizardLog.SilentMode = true;
        using var first = TempRepoFixture.CreateWithInitialCommit();
        using var second = TempRepoFixture.CreateWithInitialCommit();
        CreatePackIndex(first.Path, packIndexBytes);
        CreatePackIndex(second.Path, packIndexBytes);

        using var probe = new RefreshOverlapProbe();
        var report = new GitWizardReport
        {
            Repositories =
            {
                [first.Path] = new GitWizardRepository(first.Path) { PhaseObserverForTesting = probe.OnPhase },
                [second.Path] = new GitWizardRepository(second.Path) { PhaseObserverForTesting = probe.OnPhase },
            },
        };

        report.Refresh([first.Path, second.Path]);
        return probe.MaximumActive;
    }

    static void CreatePackIndex(string repositoryPath, long length)
    {
        var packDirectory = Path.Combine(repositoryPath, ".git", "objects", "pack");
        Directory.CreateDirectory(packDirectory);
        using var stream = new FileStream(Path.Combine(packDirectory, "synthetic.idx"),
            FileMode.Create, FileAccess.Write, FileShare.Read);
        stream.SetLength(length);
    }

    sealed class RefreshOverlapProbe : IDisposable
    {
        readonly ManualResetEventSlim _secondEntered = new(false);
        readonly object _sync = new();
        int _entries;
        int _active;

        public int MaximumActive { get; private set; }

        public void OnPhase(int phaseIndex)
        {
            if (phaseIndex != 0)
            {
                return;
            }

            var active = Interlocked.Increment(ref _active);
            lock (_sync)
            {
                MaximumActive = Math.Max(MaximumActive, active);
            }

            var entry = Interlocked.Increment(ref _entries);
            try
            {
                if (entry == 1)
                {
                    _ = _secondEntered.Wait(TestTimeouts.ShortHangGuard);
                }
                else
                {
                    _secondEntered.Set();
                }
            }
            finally
            {
                _ = Interlocked.Decrement(ref _active);
            }

            throw new OperationCanceledException();
        }

        public void Dispose() => _secondEntered.Dispose();
    }
}

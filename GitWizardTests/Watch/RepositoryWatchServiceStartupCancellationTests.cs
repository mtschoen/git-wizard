using System.Collections;
using GitWizard;
using GitWizard.Watch;
using GitWizardTests.TestSupport;

namespace GitWizardTests;

public class RepositoryWatchServiceStartupCancellationTests
{
    // Cancels the run while the startup replay is being enumerated and counts the batches pulled.
    sealed class CancellingBatches(int count, CancellationTokenSource cancellation) : IReadOnlyList<VolumeChangeBatch>
    {
        public int Enumerated { get; private set; }
        public int Count => count;
        public VolumeChangeBatch this[int index] => throw new NotSupportedException();

        public IEnumerator<VolumeChangeBatch> GetEnumerator()
        {
            for (var index = 0; index < count; index++)
            {
                Enumerated++;
                yield return new VolumeChangeBatch("C", [new VolumeChangeEntry($@"C:\repo\a\file{index}.txt", VolumeEntryKind.Modified)]);
                if (index == 1)
                {
                    cancellation.Cancel();
                }
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Test]
    public async Task CancellationDuringTheStartupReplay_StopsApplyingBatches()
    {
        var source = new ReadinessVolumeChangeSource();
        source.SetVolume(VolumeWatchStatus.CaughtUp("C", VolumeBlockOrigin.Cold));
        var request = new DiscoveryRequest(new GitWizardConfiguration(), DiscoveryPurpose.Watch, false, false, []);
        using var cancellation = new CancellationTokenSource(TestTimeouts.ShortHangGuard);
        var batches = new CancellingBatches(1000, cancellation);
        await using var startup = await FakeLiveStartup.FromSourceArmedAsync(source, request, CancellationToken.None,
            inventoryPaths: [@"C:\repo\a"], watchVolumes: ["C"], startupBatches: batches);
        var service = new RepositoryWatchService(startup, debounce: TimeSpan.Zero);

        try
        {
            await foreach (var _ in service.RunAsync(cancellation.Token))
            {
                source.Complete();
            }
        }
        catch (OperationCanceledException)
        {
            // The run observes the cancellation this test raised during the replay.
        }

        Assert.That(batches.Enumerated, Is.LessThan(10), "A cancelled run must not drain the whole startup buffer.");
    }
}

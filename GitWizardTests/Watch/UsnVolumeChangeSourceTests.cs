using System.Runtime.Versioning;
using GitWizard.Watch;
using MFTLib;

namespace GitWizardTests.Watch;

// Requires a real elevated broker child process, so it can only run against the live OS -
// gated the same way as MFTLib's/GitWizard's other RequiresAdmin tests (Assert.Inconclusive
// guard first in the body; scripts/run-coverage.ps1 self-elevates to include it). Skipped in
// the non-interactive/CI run via --filter "Category!=RequiresAdmin".
[Category("RequiresAdmin")]
[Platform("Win")]
public class UsnVolumeChangeSourceTests
{
    [Test]
    [SupportedOSPlatform("windows")]
    public async Task ArmAndCatchUpAsync_RealBroker_ReturnsNonNullColdRecordsAndErrors()
    {
        if (!ElevationUtilities.IsElevated()) { Assert.Inconclusive("Requires admin"); return; }

        await using var source = new UsnVolumeChangeSource(BrokerLauncher.Launch);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var armResult = await source.ArmAndCatchUpAsync(new[] { "C" }, cts.Token);

        Assert.That(armResult.ColdRecords, Is.Not.Null);
        Assert.That(armResult.Errors, Is.Not.Null);
    }

    // Exercises UsnVolumeChangeSource's catch-up replay path (EmitCatchUpBatchAsync) against
    // the real broker: any journal entries the broker captured between the armed cursor and
    // the advanced cursor (BrokerScanResult.CatchUpEntries) are replayed as each drive's first
    // WatchAsync batch, through the same resolution/index path as live entries. Whether any
    // catch-up entries actually exist is environment-timing-dependent (it depends on whether
    // anything changed on the volume during the cold scan) and not assertable deterministically
    // here, so this only asserts the path runs cleanly and, if a batch does arrive promptly,
    // that its entries resolved to real paths rather than nulls being silently dropped as empty.
    [Test]
    [SupportedOSPlatform("windows")]
    public async Task WatchAsync_RealBroker_ReplaysCatchUpEntriesWithoutThrowing()
    {
        if (!ElevationUtilities.IsElevated()) { Assert.Inconclusive("Requires admin"); return; }

        await using var source = new UsnVolumeChangeSource(BrokerLauncher.Launch);
        using var armCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await source.ArmAndCatchUpAsync(new[] { "C" }, armCts.Token);

        using var watchCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            await foreach (var batch in source.WatchAsync(watchCts.Token))
            {
                Assert.That(batch.Entries, Has.All.Matches<VolumeChangeEntry>(
                    entry => !string.IsNullOrEmpty(entry.FullPath)));
                break;
            }
        }
        catch (OperationCanceledException)
        {
            // No batch arrived within the window - acceptable; the point is that the
            // catch-up-replay path in WatchAsync didn't throw.
        }
    }
}

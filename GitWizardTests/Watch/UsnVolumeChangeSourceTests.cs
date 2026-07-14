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
    public async Task ArmAndCatchUpAsync_RealBroker_ReturnsNonNullColdRecords()
    {
        if (!ElevationUtilities.IsElevated()) { Assert.Inconclusive("Requires admin"); return; }

        await using var source = new UsnVolumeChangeSource(BrokerLauncher.Launch);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var cold = await source.ArmAndCatchUpAsync(new[] { "C" }, cts.Token);

        Assert.That(cold, Is.Not.Null);
    }
}

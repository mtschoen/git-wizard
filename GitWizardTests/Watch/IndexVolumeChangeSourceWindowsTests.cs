using System.Runtime.Versioning;
using GitWizard.Watch;
using GitWizardTests.TestSupport;
using MFTLib;
using MFTLib.Index;

namespace GitWizardTests.Watch;

// Requires a real elevated broker child process, so it can only run against the live OS -
// gated the same way as MFTLib's/GitWizard's other RequiresAdmin tests (Assert.Inconclusive
// guard first in the body; scripts/run-coverage.ps1 self-elevates to include it). Skipped in
// the non-interactive/CI run via --filter "Category!=RequiresAdmin".
[Category("RequiresAdmin")]
[Platform("Win")]
public class IndexVolumeChangeSourceWindowsTests
{
    [Test]
    [SupportedOSPlatform("windows")]
    public async Task ArmAndCatchUpAsync_RealBroker_ReturnsUsableDriveStatus()
    {
        if (!ElevationUtilities.IsElevated()) { Assert.Inconclusive("Requires admin"); return; }

        var drive = GetFixtureDrive(Path.GetTempPath());
        GitWizard.MftIndexSession? session = null;
        await using var source = new IndexVolumeChangeSource(async (drives, cancellationToken) =>
        {
            session = await GitWizard.MftIndexSession.OpenWindowsAsync(
                drives, UiBrokerLauncher.LaunchUiBroker, cancellationToken);
            return session;
        });
        using var armCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var armResult = await source.ArmAndCatchUpAsync(new[] { drive }, armCancellation.Token);

        Assert.That(armResult.Errors, Does.Not.ContainKey(drive));
        Assert.That(session, Is.Not.Null);
        var status = session!.Index.Drives.Single(candidate =>
            char.ToUpperInvariant(candidate.DriveLetter).ToString() == drive);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(status.State, Is.EqualTo(DriveState.Ready));
            Assert.That(status.WatchSupported, Is.True);
            Assert.That(status.RowCount, Is.GreaterThan(0));
            Assert.That(status.WatchFailureMessage, Is.Null);
        }
    }

    [Test]
    [SupportedOSPlatform("windows")]
    public async Task WatchAsync_RealBroker_ReplaysFileCreatedBetweenScanAndWatch()
    {
        if (!ElevationUtilities.IsElevated()) { Assert.Inconclusive("Requires admin"); return; }

        var temporaryPath = Path.Combine(Path.GetTempPath(), $"git-wizard-gap-{Guid.NewGuid():N}.tmp");
        var drive = GetFixtureDrive(temporaryPath);
        await using var source = new IndexVolumeChangeSource((drives, cancellationToken) =>
            GitWizard.MftIndexSession.OpenWindowsAsync(drives, UiBrokerLauncher.LaunchUiBroker, cancellationToken));
        using var armCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var armResult = await source.ArmAndCatchUpAsync(new[] { drive }, armCancellation.Token);
        Assert.That(armResult.Errors, Does.Not.ContainKey(drive));

        using var watchCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            // Creating after the cold scan and before enumeration requires armed-cursor replay.
            await File.WriteAllTextAsync(temporaryPath, "gap replay test", watchCancellation.Token);
            Assert.That(await ObservePathAsync(source, temporaryPath, watchCancellation.Token), Is.True,
                "The watch ended without replaying the file created between scan and watch.");
        }
        finally
        {
            await watchCancellation.CancelAsync();
            File.Delete(temporaryPath);
        }
    }

    [Test]
    [SupportedOSPlatform("windows")]
    public async Task WatchAsync_RealBroker_DetectsCreatedFile()
    {
        if (!ElevationUtilities.IsElevated()) { Assert.Inconclusive("Requires admin"); return; }

        var temporaryPath = Path.Combine(Path.GetTempPath(), $"git-wizard-watch-{Guid.NewGuid():N}.tmp");
        var drive = GetFixtureDrive(temporaryPath);
        await using var source = new IndexVolumeChangeSource((drives, cancellationToken) =>
            GitWizard.MftIndexSession.OpenWindowsAsync(drives, UiBrokerLauncher.LaunchUiBroker, cancellationToken));
        using var armCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var armResult = await source.ArmAndCatchUpAsync(new[] { drive }, armCancellation.Token);
        Assert.That(armResult.Errors, Does.Not.ContainKey(drive));

        using var watchCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var observedTask = ObservePathAsync(source, temporaryPath, watchCancellation.Token);
        try
        {
            // Replay covers pump startup races; starting observation is not a catch-up fence.
            await File.WriteAllTextAsync(temporaryPath, "watch test", watchCancellation.Token);
            Assert.That(await observedTask, Is.True,
                "The watch ended without reporting the deliberately created file.");
        }
        finally
        {
            await watchCancellation.CancelAsync();
            File.Delete(temporaryPath);
        }
    }

    static string GetFixtureDrive(string fixturePath)
    {
        var root = Path.GetPathRoot(fixturePath)
            ?? throw new InvalidOperationException("Could not determine the fixture drive.");
        return root.TrimEnd(':', '\\', '/').ToUpperInvariant();
    }

    [SupportedOSPlatform("windows")]
    static async Task<bool> ObservePathAsync(
        IndexVolumeChangeSource source, string expectedPath, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var batch in source.WatchAsync(cancellationToken))
            {
                if (batch.Entries.Any(entry =>
                    entry.FullPath.Equals(expectedPath, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Assert.Fail($"The watch timed out before reporting the deliberately created file: {expectedPath}");
        }

        return false;
    }

}

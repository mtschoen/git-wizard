using System.Runtime.Versioning;
using GitWizard.Watch;
using GitWizardTests.TestSupport;
using MFTLib;
using MFTLib.Index;
using MFTLibTestExtensions;

namespace GitWizardTests.Watch;

// Requires a real elevated broker child process, so it can only run against the live OS -
// gated the same way as MFTLib's/GitWizard's other RequiresAdmin tests (Assert.Inconclusive
// guard first in the body; scripts/run-coverage.ps1 self-elevates to include it). Skipped in
// the non-interactive/CI run via --filter "Category!=RequiresAdmin".
//
// Mutating tests (such as journal enlargement) are marked [Explicit] and require explicit
// attended opt-in via GITWIZARD_TEST_GROW_JOURNAL=1 so that default, CI, and generic elevated
// coverage runs do not mutate persistent volume configuration.
[Category("RequiresAdmin")]
[Platform("Win")]
public class IndexVolumeChangeSourceWindowsTests
{
    [Test]
    [Explicit("Mutates persistent USN journal settings on the host volume; requires explicit selection and opt-in.")]
    [Category("Attended")]
    [SupportedOSPlatform("windows")]
    public async Task GrowJournal_RealBroker_GrowsAndRejectsShrinkOnSameSession()
    {
        if (!ElevationUtilities.DefaultProvider.IsElevated())
        {
            Assert.Inconclusive("Requires admin");
            return;
        }

        if (Environment.GetEnvironmentVariable("GITWIZARD_TEST_GROW_JOURNAL") != "1")
        {
            Assert.Inconclusive("Requires explicit opt-in via GITWIZARD_TEST_GROW_JOURNAL=1 because this test permanently mutates persistent host volume journal configuration.");
            return;
        }

        var drive = GetFixtureDrive(Path.GetTempPath());
        var launches = 0;
        await using var source = new IndexVolumeChangeSource((drives, progress, cancellationToken) =>
            GitWizard.MftIndexSession.OpenWindowsAsync(drives, arguments =>
            {
                launches++;
                return UiBrokerLauncher.LaunchUiBroker(arguments);
            }, null, progress, cancellationToken));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var arm = await source.ArmAndCatchUpAsync([drive], cancellation.Token);
        Assert.That(arm.Errors, Does.Not.ContainKey(drive));
        var before = source.QueryUsnJournalSettings(drive[0]);
        Assert.That(before.MaximumSize, Is.Positive);
        Assert.That(before.AllocationDelta, Is.Positive);
        if (before.MaximumSize >= 1024L * 1024 * 1024)
        {
            Assert.Inconclusive("Journal maximum is already at or above 1 GB");
            return;
        }

        var requested = checked(before.MaximumSize + before.AllocationDelta);

        var after = await source.GrowUsnJournalAsync(drive[0], requested, before.AllocationDelta, cancellation.Token);

        Assert.That(after.MaximumSize, Is.GreaterThanOrEqualTo(requested));
        Assert.That(source.QueryUsnJournalSettings(drive[0]).MaximumSize, Is.EqualTo(after.MaximumSize));
        Assert.ThrowsAsync<InvalidOperationException>(() => source.GrowUsnJournalAsync(
            drive[0], before.MaximumSize, before.AllocationDelta, cancellation.Token));
        Assert.That(source.QueryUsnJournalSettings(drive[0]).MaximumSize, Is.EqualTo(after.MaximumSize));
        Assert.That(launches, Is.EqualTo(1));
    }

    [Test]
    [SupportedOSPlatform("windows")]
    public async Task ArmAndCatchUpAsync_RealBroker_ReturnsUsableDriveStatus()
    {
        if (!ElevationUtilities.DefaultProvider.IsElevated()) { Assert.Inconclusive("Requires admin"); return; }

        var drive = GetFixtureDrive(Path.GetTempPath());
        GitWizard.MftIndexSession? session = null;
        await using var source = new IndexVolumeChangeSource(async (drives, progress, cancellationToken) =>
        {
            session = await GitWizard.MftIndexSession.OpenWindowsAsync(
                drives, UiBrokerLauncher.LaunchUiBroker, null, progress, cancellationToken);
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
            Assert.That(status.Watch.Supported, Is.True);
            Assert.That(SyntheticIndexInspection.ReadHeader(session.Index, status.DriveLetter)?.RowCount, Is.GreaterThan(0u));
            Assert.That(status.Watch.FailureMessage, Is.Null);
        }
    }

    [Test]
    [SupportedOSPlatform("windows")]
    public async Task WatchAsync_RealBroker_ReplaysFileCreatedBetweenScanAndWatch()
    {
        if (!ElevationUtilities.DefaultProvider.IsElevated()) { Assert.Inconclusive("Requires admin"); return; }

        var temporaryPath = Path.Combine(Path.GetTempPath(), $"git-wizard-gap-{Guid.NewGuid():N}.tmp");
        var drive = GetFixtureDrive(temporaryPath);
        await using var source = new IndexVolumeChangeSource((drives, progress, cancellationToken) =>
            GitWizard.MftIndexSession.OpenWindowsAsync(drives, UiBrokerLauncher.LaunchUiBroker, null, progress, cancellationToken));
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
        if (!ElevationUtilities.DefaultProvider.IsElevated()) { Assert.Inconclusive("Requires admin"); return; }

        var temporaryPath = Path.Combine(Path.GetTempPath(), $"git-wizard-watch-{Guid.NewGuid():N}.tmp");
        var drive = GetFixtureDrive(temporaryPath);
        await using var source = new IndexVolumeChangeSource((drives, progress, cancellationToken) =>
            GitWizard.MftIndexSession.OpenWindowsAsync(drives, UiBrokerLauncher.LaunchUiBroker, null, progress, cancellationToken));
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

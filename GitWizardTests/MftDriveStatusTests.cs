using GitWizard;
using GitWizardTests.TestSupport;
using MFTLib.Index;

namespace GitWizardTests;

[TestFixture]
public class MftDriveStatusTests
{
    [Test]
    public async Task RequestedIdentityFailureIsNotLost()
    {
        await using var broker = new BlockBrokerFixture(scanError: "scan refused");
        var session = await MftIndexSession.OpenSelectedVolumesAsync(["W", "X"],
            drive => drive == "W" ? throw new IOException("identity denied") : broker.Drive('X'),
            broker.ConnectAsync, broker.CacheDirectory, null, CancellationToken.None);
        try
        {
            await AssertStatusesSurviveOwnerDisposalAsync(session);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    static async Task AssertStatusesSurviveOwnerDisposalAsync(MftIndexSession session)
    {
        var statuses = session.CaptureDriveStatuses();
        Assert.That(statuses.Select(status => status.Drive), Is.EqualTo(new[] { "W", "X" }));
        Assert.That(statuses[0].IdentityFailure, Is.EqualTo("identity denied"));
        Assert.That(statuses[0].IndexStatus, Is.Null);
        Assert.That(statuses[1].IdentityFailure, Is.Null);
        Assert.That(statuses[1].IndexStatus!.FailureKind, Is.EqualTo(DriveFailureKind.ProducerFailed));
        Assert.That(statuses[1].IndexStatus!.BlockSource, Is.EqualTo(BlockSource.None));
        await using var result = new MftDiscoveryResult(false, session);
        var captured = result.DriveStatuses;
        Assert.That(result.DetachIndexSession(), Is.SameAs(session));
        await session.DisposeAsync();
        Assert.That(result.DriveStatuses, Is.EqualTo(captured));
        Assert.That(captured[1].IndexStatus!.MftProducerFailureMessage, Does.Contain("scan refused"));
    }

    [Test]
    public async Task EachCaptureIsANewReadOnlySnapshot()
    {
        await using var broker = new BlockBrokerFixture();
        await using var session = await MftIndexSession.OpenAsync([broker.Drive('X')],
            broker.ConnectAsync, broker.CacheDirectory, null, CancellationToken.None);
        var first = session.CaptureDriveStatuses();
        var second = session.CaptureDriveStatuses();
        Assert.That(second, Is.Not.SameAs(first));
        Assert.That(second, Is.EqualTo(first));
        Assert.That(first, Is.InstanceOf<System.Collections.ObjectModel.ReadOnlyCollection<MftDriveStatus>>());
        Assert.That(first.Single().IndexStatus!.State, Is.EqualTo(DriveState.Ready));
    }

    [Test]
    public async Task ProjectionPreservesQueryableBlockAndCheckpointLoss()
    {
        using var files = new PackedIndexFixture();
        await using var index = await files.OpenIndexAsync();
        var loss = new JournalCheckpointLoss
        {
            DriveLetter = 'X',
            DetectedDuring = JournalCheckpointLossDetection.LiveWatch,
            Cause = JournalCheckpointLossCause.CheckpointTrimmed,
            AllocationDelta = 1024,
            MaximumSize = 2048
        };
        var source = index.Drives.Single() with
        {
            BlockSource = BlockSource.WarmStartedFromCache,
            WatchFailureMessage = "watch failed",
            CheckpointLoss = loss
        };
        var projection = new MftDriveStatus("X", source, null);
        Assert.That(projection.IndexStatus!.State, Is.EqualTo(DriveState.Ready));
        Assert.That(projection.IndexStatus.BlockSource, Is.EqualTo(BlockSource.WarmStartedFromCache));
        Assert.That(projection.IndexStatus.FailureKind, Is.EqualTo(DriveFailureKind.None));
        Assert.That(projection.IndexStatus.WatchFailureMessage, Is.EqualTo("watch failed"));
        Assert.That(projection.IndexStatus.CheckpointLoss, Is.EqualTo(loss));
        Assert.That(projection.IndexStatus.ScanTimestamp, Is.EqualTo(source.ScanTimestamp));
    }
}

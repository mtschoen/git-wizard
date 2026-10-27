using GitWizard;
using GitWizardTests.TestSupport;
using MFTLib;
using MFTLib.Index;
using MFTLibTestExtensions;

namespace GitWizardTests;

[TestFixture]
public class MftRootStatusTests
{
    [Test]
    public async Task RequestedIdentityFailureIsNotLost()
    {
        await using var broker = new BlockBrokerFixture(scanError: "scan refused");
        var session = await MftIndexSession.OpenSelectedVolumesAsync(["W", "X"],
            drive => drive == 'W' ? throw new IOException("identity denied") : broker.Drive('X'),
            broker.CreateSession(), broker.CacheDirectory, null, CancellationToken.None);
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
        var statuses = session.CaptureRootStatuses();
        Assert.That(statuses.Select(status => status.Drive), Is.EqualTo(["W", "X"]));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(statuses[0].IdentityFailure, Is.EqualTo("identity denied"));
            Assert.That(statuses[0].IndexStatus, Is.Null);
            Assert.That(statuses[1].IdentityFailure, Is.Null);
            Assert.That(statuses[1].IndexStatus!.Failure?.Kind, Is.EqualTo(RootFailureKind.ProducerFailed));
            Assert.That(statuses[1].IndexStatus!.Block, Is.Null);
        }
        await using var result = new MftDiscoveryResult(false, session);
        var captured = result.RootStatuses;
        Assert.That(result.DetachIndexSession(), Is.SameAs(session));
        await session.DisposeAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.RootStatuses, Is.EqualTo(captured));
            Assert.That(captured[1].IndexStatus!.Failure?.Message, Does.Contain("scan refused"));
        }
    }

    [Test]
    public async Task EachCaptureIsANewReadOnlySnapshot()
    {
        await using var broker = new BlockBrokerFixture();
        await using var session = await MftIndexSession.OpenAsync([broker.Drive('X')],
            broker.CreateSession(), broker.CacheDirectory, null, CancellationToken.None);
        var first = session.CaptureRootStatuses();
        var second = session.CaptureRootStatuses();
        Assert.That(second, Is.Not.SameAs(first));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(second, Is.EqualTo(first));
            Assert.That(first, Is.InstanceOf<System.Collections.ObjectModel.ReadOnlyCollection<MftRootStatus>>());
        }
        Assert.That(first.Single().IndexStatus!.State, Is.EqualTo(RootState.Ready));
    }

    [Test]
    public async Task ProjectionPreservesQueryableBlockAndCheckpointLoss()
    {
        using var files = new PackedIndexFixture();
        await using var index = await files.OpenIndexAsync();
        var loss = SyntheticNotifications.CreateJournalCheckpointLoss('X', JournalCheckpointLossCause.CheckpointTrimmed, JournalCheckpointLossDetection.LiveWatch, new UsnJournalSettings { MaximumSize = 2048, AllocationDelta = 1024 });
        var drive = index.Status;
        var source = drive with
        {
            Block = drive.Block! with { Origin = BlockOrigin.WarmStartedFromCache },
            Watch = drive.Watch with { FailureMessage = "watch failed", CheckpointLoss = loss }
        };
        var projection = new MftRootStatus("X", source, null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(projection.IndexStatus!.State, Is.EqualTo(RootState.Ready));
            Assert.That(projection.IndexStatus.Block?.Origin, Is.EqualTo(BlockOrigin.WarmStartedFromCache));
            Assert.That(projection.IndexStatus.Failure, Is.Null);
            Assert.That(projection.IndexStatus.Watch.FailureMessage, Is.EqualTo("watch failed"));
            Assert.That(projection.IndexStatus.Watch.CheckpointLoss, Is.EqualTo(loss));
            Assert.That(projection.IndexStatus.Block?.ScanTimestamp, Is.EqualTo(source.Block!.ScanTimestamp));
        }
    }
}

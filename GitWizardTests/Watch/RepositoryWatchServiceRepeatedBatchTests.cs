using GitWizard.Watch;
using GitWizardTests.TestSupport;

namespace GitWizardTests.Watch;

/// <summary>
/// MFTLib can deliver a batch applied just before a rescan commits again during the replacement
/// watch's catch-up, so the service must treat a repeated batch as the same change.
/// </summary>
[TestFixture]
public class RepositoryWatchServiceRepeatedBatchTests
{
    [Test]
    public async Task RepeatedRepositoryCreation_EmitsOneCreated()
    {
        var created = new VolumeChangeBatch("C", [new VolumeChangeEntry(@"C:\repo\newrepo\.git", VolumeEntryKind.Created)]);
        var service = new RepositoryWatchService(new ScriptedVolumeChangeSource([created, created]),
            trackedRoots: [], searchRoots: [@"C:\repo"], debounce: TimeSpan.FromMilliseconds(10));

        var events = await DrainAsync(service);

        Assert.That(events.Count(change => change.Kind == RepositoryChangeKind.Created), Is.EqualTo(1));
        Assert.That(events, Does.Contain(new RepositoryChangeEvent(@"C:\repo\newrepo", RepositoryChangeKind.Created)));
        Assert.That(events.Any(change => change.Kind == RepositoryChangeKind.Deleted), Is.False);
    }

    [Test]
    public async Task RepeatedRepositoryDeletion_EmitsOneDeleted()
    {
        var deleted = new VolumeChangeBatch("C", [new VolumeChangeEntry(@"C:\repo\a\.git", VolumeEntryKind.Deleted)]);
        var service = new RepositoryWatchService(new ScriptedVolumeChangeSource([deleted, deleted]),
            trackedRoots: [@"C:\repo\a"], searchRoots: [@"C:\repo"], debounce: TimeSpan.FromMilliseconds(10));

        var events = await DrainAsync(service);

        Assert.That(events, Is.EqualTo([new RepositoryChangeEvent(@"C:\repo\a", RepositoryChangeKind.Deleted)]));
    }

    static async Task<List<RepositoryChangeEvent>> DrainAsync(RepositoryWatchService service)
    {
        var events = new List<RepositoryChangeEvent>();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var change in service.RunAsync(cancellation.Token))
        {
            events.Add(change);
        }

        return events;
    }
}

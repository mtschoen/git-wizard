using GitWizard.Watch;

namespace GitWizardTests.Watch;

public class VolumeReadinessSnapshotTests
{
    [Test]
    public void WithAll_MatchesRepeatedWith_ForMixedCaseRootsReplacementsAndTransitions()
    {
        var transition = new VolumeWatchTransition(Guid.Empty, "/x/Repos", 1, "Faulted", null, DateTimeOffset.UnixEpoch);
        var start = VolumeReadinessSnapshot.Empty
            .With(VolumeWatchStatus.CatchingUp("/x/Repos", VolumeBlockOrigin.Unknown))
            .WithTransition(transition);
        VolumeWatchStatus[] statuses =
        [
            VolumeWatchStatus.CaughtUp("/x/Repos", VolumeBlockOrigin.Warm),
            VolumeWatchStatus.Excluded("/x/repos", VolumeBlockOrigin.Unknown, "mark failed"),
            VolumeWatchStatus.CaughtUp("/a/one", VolumeBlockOrigin.Cold),
            VolumeWatchStatus.CaughtUp("/a/one", VolumeBlockOrigin.Warm)
        ];

        var expected = statuses.Aggregate(start, (snapshot, status) => snapshot.With(status));
        var actual = start.WithAll(statuses);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual.Volumes, Is.EqualTo(expected.Volumes));
            Assert.That(actual.Volumes.Select(volume => volume.Volume), Is.EqualTo(["/a/one", "/x/Repos", "/x/repos"]));
            Assert.That(actual.RecentTransitions, Is.EqualTo(expected.RecentTransitions));
        }
    }
}

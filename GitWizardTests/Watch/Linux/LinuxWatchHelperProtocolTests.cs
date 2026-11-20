using GitWizard.Watch;
using GitWizard.Watch.Linux;

namespace GitWizardTests.Watch.Linux;

[TestFixture]
public class LinuxWatchHelperProtocolTests
{
    static T RoundTrip<T>(T frame) where T : LinuxWatchHelperFrame
    {
        var line = LinuxWatchHelperProtocol.Serialize(frame);
        var parsed = LinuxWatchHelperProtocol.TryParse(line, out var result, out var error) ? result : null;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(line, Does.Not.Contain("\n").And.Not.Contain("\r"), "A frame must stay on one line.");
            Assert.That(parsed, Is.TypeOf<T>(), error);
        }

        return (T)parsed!;
    }

    [Test]
    public void ArmRequest_RoundTrips()
    {
        var parsed = RoundTrip(new ArmRequestFrame(["/repos", "/srv/data/line\nbreak"]));
        Assert.That(parsed.Roots, Is.EqualTo(new[] { "/repos", "/srv/data/line\nbreak" }));
    }

    [Test]
    public void WatchRequest_RoundTrips() => Assert.That(RoundTrip(new WatchRequestFrame()), Is.TypeOf<WatchRequestFrame>());

    [Test]
    public void Hello_RoundTrips() =>
        Assert.That(RoundTrip(new HelloFrame(LinuxWatchHelperProtocol.Version)).ProtocolVersion, Is.EqualTo(LinuxWatchHelperProtocol.Version));

    [Test]
    public void ArmResult_RoundTrips()
    {
        var parsed = RoundTrip(new ArmResultFrame(new Dictionary<string, string> { ["/missing"] = "Directory does not exist: /missing" }));
        Assert.That(parsed.Errors, Is.EqualTo(new Dictionary<string, string> { ["/missing"] = "Directory does not exist: /missing" }));
    }

    [Test]
    public void Readiness_RoundTrips()
    {
        VolumeWatchStatus[] volumes =
        [
            VolumeWatchStatus.CaughtUp("/repos", VolumeBlockOrigin.Unknown),
            VolumeWatchStatus.Excluded("/missing", VolumeBlockOrigin.Cold, "mark failed") with { WatchStateVersion = 7 },
            VolumeWatchStatus.CatchingUp("/other", VolumeBlockOrigin.Warm)
        ];

        Assert.That(RoundTrip(new ReadinessFrame(volumes)).Volumes, Is.EqualTo(volumes));
    }

    [Test]
    public void PumpStarted_RoundTrips() => Assert.That(RoundTrip(new PumpStartedFrame()), Is.TypeOf<PumpStartedFrame>());

    [Test]
    public void Batch_RoundTrips()
    {
        VolumeChangeEntry[] entries =
        [
            new("/repos/a/file", VolumeEntryKind.Modified),
            new("/repos/b", VolumeEntryKind.Created),
            new("/repos/c", VolumeEntryKind.Deleted),
            new("/repos/d", VolumeEntryKind.MovedIn)
        ];

        var parsed = RoundTrip(new BatchFrame(new VolumeChangeBatch("/", entries)));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(parsed.Batch.Volume, Is.EqualTo("/"));
            Assert.That(parsed.Batch.Entries, Is.EqualTo(entries));
        }
    }

    [Test]
    public void DriveFailed_RoundTrips()
    {
        var parsed = RoundTrip(new DriveFailedFrame("/repos", "watch failed"));
        Assert.That((parsed.Volume, parsed.Message), Is.EqualTo(("/repos", "watch failed")));
    }

    [Test]
    public void Error_RoundTrips() => Assert.That(RoundTrip(new ErrorFrame("rejected")).Message, Is.EqualTo("rejected"));

    [Test]
    public void Died_RoundTrips() => Assert.That(RoundTrip(new DiedFrame("fanotify event queue overflowed.")).Reason,
        Is.EqualTo("fanotify event queue overflowed."));

    [TestCase("")]
    [TestCase("not json")]
    [TestCase("[]")]
    [TestCase("{}")]
    [TestCase("{\"type\":7}")]
    [TestCase("{\"type\":\"launch\"}")]
    [TestCase("{\"type\":\"arm\"}")]
    [TestCase("{\"type\":\"arm\",\"roots\":[1]}")]
    [TestCase("{\"type\":\"hello\",\"protocol\":\"1\"}")]
    [TestCase("{\"type\":\"armResult\",\"errors\":{\"/a\":1}}")]
    [TestCase("{\"type\":\"readiness\",\"volumes\":[{\"volume\":\"/a\",\"state\":\"5\",\"origin\":\"Unknown\"}]}")]
    [TestCase("{\"type\":\"readiness\",\"volumes\":[{\"volume\":\"/a\",\"state\":\"CaughtUp\",\"origin\":\"Unknown\",\"watchStateVersion\":\"x\"}]}")]
    [TestCase("{\"type\":\"batch\",\"volume\":\"/\",\"entries\":[{\"path\":\"/a\",\"kind\":\"Renamed\"}]}")]
    [TestCase("{\"type\":\"driveFailed\",\"volume\":\"/a\"}")]
    [TestCase("{\"type\":\"died\"}")]
    public void MalformedLine_IsRejectedWithAReason(string line)
    {
        Assert.That(LinuxWatchHelperProtocol.TryParse(line, out var frame, out var error), Is.False);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(frame, Is.Null);
            Assert.That(error, Is.Not.Empty);
        }
    }
}

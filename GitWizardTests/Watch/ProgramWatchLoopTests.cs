using GitWizard.CLI;
using GitWizard.Watch;
using GitWizardTests.TestSupport;
using static GitWizardTests.TestSupport.AsyncCondition;

namespace GitWizardTests.Watch;

[TestFixture]
public class ProgramWatchLoopTests
{
    [Test]
    public void ReadyLineGrammarQuotesReasons()
    {
        var readiness = new WatchReadiness(WatchReadinessMode.JournalBacklog, ["C", "D"],
        [
            VolumeWatchStatus.Excluded("E", VolumeBlockOrigin.Unknown, "Drive is \"Offline\", retry."),
            VolumeWatchStatus.Excluded("F", VolumeBlockOrigin.Cold, "watch failed")
        ]);
        Assert.That(Program.FormatReadyLine(readiness), Is.EqualTo(
            "ready: mode=journal caught-up=C,D excluded=E=\"Drive is \\u0022Offline\\u0022, retry.\",F=\"watch failed\""));
        Assert.That(Program.FormatReadyLine(new WatchReadiness(WatchReadinessMode.FilesystemMarks, ["/home"], [])),
            Is.EqualTo("ready: mode=marks caught-up=/home excluded=-"));
        Assert.That(Program.FormatReadyLine(new WatchReadiness(WatchReadinessMode.PumpActive, [], [])),
            Is.EqualTo("ready: mode=pump caught-up=- excluded=-"));
    }

    [Test]
    public void VolumeAndStartupLinesAreStable()
    {
        Assert.That(Program.FormatVolumeLine(VolumeWatchStatus.CatchingUp("C", VolumeBlockOrigin.Warm)), Is.EqualTo("drive: C state=catching-up origin=warm"));
        Assert.That(Program.FormatVolumeLine(VolumeWatchStatus.Excluded("D", VolumeBlockOrigin.Cold, "x \"y\"")), Is.EqualTo("drive: D state=excluded origin=cold reason=\"x \\u0022y\\u0022\""));
        Assert.That(Program.FormatStartupLine(new WatchStartupProgress(WatchStartupPhase.RequestingElevation)), Is.EqualTo("startup: requesting-elevation"));
        Assert.That(Program.FormatStartupLine(new WatchStartupProgress(WatchStartupPhase.Scanning, "C")), Is.EqualTo("startup: scanning C"));
    }

    [Test]
    public async Task ReadyPrintsOnceAfterLastFenceWithVerboseTimestamps()
    {
        await using var source = new ReadinessVolumeChangeSource();
        var service = new RepositoryWatchService(source, [@"X:\repo", @"Y:\repo"], [], TimeSpan.Zero);
        var lines = new List<string>();
        var sink = new Lock();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var loop = Program.RunWatchLoopAsync(service, line => { lock (sink) lines.Add(line); }, _ => { }, cancellation.Token,
            verbose: true, clock: new FrozenCacheClock());
        await source.Started.WaitAsync(TimeSpan.FromSeconds(5));
        source.SetVolume(VolumeWatchStatus.CatchingUp("X", VolumeBlockOrigin.Warm));
        source.SetVolume(VolumeWatchStatus.CatchingUp("Y", VolumeBlockOrigin.Cold));
        source.SetVolume(VolumeWatchStatus.CaughtUp("X", VolumeBlockOrigin.Warm));
        lock (sink) Assert.That(lines, Has.None.Contains("ready:"));
        source.SetVolume(VolumeWatchStatus.Excluded("Y", VolumeBlockOrigin.Cold, "Y failed"));
        await WaitUntilAsync(() => { lock (sink) return lines.Any(line => line.Contains("ready:")); });
        source.Publish(new VolumeChangeBatch("X", [new VolumeChangeEntry(@"X:\repo\file", VolumeEntryKind.Modified)]));
        await WaitUntilAsync(() => { lock (sink) return lines.Any(line => line.Contains("changed:")); });
        await cancellation.CancelAsync();
        await loop;

        const string stamp = "2026-09-23T12:00:00.000Z ";
        string[] snapshot;
        lock (sink) snapshot = lines.ToArray();
        Assert.That(snapshot, Has.All.StartsWith(stamp));
        Assert.That(snapshot.Count(line => line.Contains(" ready: ")), Is.EqualTo(1));
        var ready = snapshot.Single(line => line.Contains(" ready: "));
        Assert.That(ready, Is.EqualTo(stamp + "ready: mode=journal caught-up=X excluded=Y=\"Y failed\""));
        Assert.That(snapshot, Does.Contain(stamp + "drive: X state=catching-up origin=warm"));
        Assert.That(snapshot, Does.Contain(stamp + "drive: X state=caught-up origin=warm"));
        Assert.That(snapshot, Does.Contain(stamp + "drive: Y state=excluded origin=cold reason=\"Y failed\""));
        Assert.That(snapshot, Does.Contain(stamp + @"changed: X:\repo"));
        Assert.That(Array.IndexOf(snapshot, ready), Is.LessThan(Array.IndexOf(snapshot, stamp + @"changed: X:\repo")));
    }

    [Test]
    public async Task NonVerbosePreservesEventPayloadsAndOmitsDriveLines()
    {
        await using var source = new ReadinessVolumeChangeSource();
        source.SetVolume(VolumeWatchStatus.CaughtUp("X", VolumeBlockOrigin.Warm));
        var service = new RepositoryWatchService(source, [@"X:\repo"], [], TimeSpan.Zero);
        var lines = new List<string>();
        var sink = new Lock();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var loop = Program.RunWatchLoopAsync(service, line => { lock (sink) lines.Add(line); }, _ => { }, cancellation.Token);
        await WaitUntilAsync(() => { lock (sink) return lines.Contains("ready: mode=journal caught-up=X excluded=-"); });
        source.Publish(new VolumeChangeBatch("X", [new VolumeChangeEntry(@"X:\repo\file", VolumeEntryKind.Modified)]));
        await WaitUntilAsync(() => { lock (sink) return lines.Contains(@"changed: X:\repo"); });
        await cancellation.CancelAsync();
        await loop;
        lock (sink) Assert.That(lines, Has.None.StartsWith("drive: "));
    }

    [Test]
    public async Task SourceDeathBeforeSettleEmitsNoReady()
    {
        await using var source = new ReadinessVolumeChangeSource();
        var service = new RepositoryWatchService(source, [@"X:\repo"], [], TimeSpan.Zero);
        var lines = new List<string>();
        var sink = new Lock();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var loop = Program.RunWatchLoopAsync(service, line => { lock (sink) lines.Add(line); }, _ => { }, cancellation.Token);
        await source.Started.WaitAsync(TimeSpan.FromSeconds(5));
        source.SetVolume(VolumeWatchStatus.CatchingUp("X", VolumeBlockOrigin.Warm));
        source.KillThenSettle("broker died", VolumeWatchStatus.CaughtUp("X", VolumeBlockOrigin.Warm));
        Assert.That(await loop, Is.True);
        lock (sink)
        {
            Assert.That(lines, Does.Contain("Broker died: broker died"));
            Assert.That(lines, Has.None.StartsWith("ready:"));
        }
    }
}

using GitWizard.CLI;
using GitWizard.Watch;
using GitWizardTests.Watch;

namespace GitWizardTests;

// Exercises Program.RunWatchLoopAsync - the testable core of the --watch CLI command - through
// a fake IVolumeChangeSource-backed RepositoryWatchService and a capturing sink, so the printed
// line format for each event kind, scan errors, and broker death can be asserted without a real
// elevated broker or Environment.Exit.
[TestFixture]
public class ProgramWatchLoopTests
{
    [Test]
    public async Task RunWatchLoopAsync_PrintsScanErrorsThenChangedCreatedDeletedLines()
    {
        var errors = new Dictionary<string, string> { ["D"] = "drive not ready" };
        var batch = new VolumeChangeBatch("C", new[]
        {
            new VolumeChangeEntry(@"C:\repo\a\file.txt", VolumeEntryKind.Modified),
            new VolumeChangeEntry(@"C:\repo\b\newrepo\.git", VolumeEntryKind.Created),
            new VolumeChangeEntry(@"C:\repo\gone\.git", VolumeEntryKind.Deleted),
        });
        var source = new FakeVolumeChangeSource(Array.Empty<VolumeColdRecord>(), new[] { batch }, errors);
        var service = new RepositoryWatchService(
            source,
            trackedRoots: new[] { @"C:\repo\a", @"C:\repo\gone" },
            searchRoots: new[] { @"C:\repo\b" },
            debounce: TimeSpan.FromMilliseconds(10));

        var lines = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var stopped = await Program.RunWatchLoopAsync(service, lines.Add, cts.Token);

        Assert.That(stopped, Is.False);
        Assert.That(lines, Is.EqualTo(new[]
        {
            "scan error on D: drive not ready",
            @"changed: C:\repo\a",
            @"created: C:\repo\b\newrepo",
            @"deleted: C:\repo\gone",
        }));
    }

    [Test]
    public async Task RunWatchLoopAsync_NoScanErrors_PrintsNoScanErrorLine()
    {
        var source = new FakeVolumeChangeSource(Array.Empty<VolumeColdRecord>(), Array.Empty<VolumeChangeBatch>());
        var service = new RepositoryWatchService(
            source,
            trackedRoots: Array.Empty<string>(),
            searchRoots: Array.Empty<string>(),
            debounce: TimeSpan.FromMilliseconds(10));

        var lines = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var stopped = await Program.RunWatchLoopAsync(service, lines.Add, cts.Token);

        Assert.That(stopped, Is.False);
        Assert.That(lines, Is.Empty);
    }

    [Test]
    public async Task RunWatchLoopAsync_SourceDies_PrintsBrokerDiedAndReturnsTrue()
    {
        var source = new HangingVolumeChangeSource();
        var service = new RepositoryWatchService(
            source,
            trackedRoots: Array.Empty<string>(),
            searchRoots: Array.Empty<string>(),
            debounce: TimeSpan.FromMilliseconds(10));

        var lines = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var loopTask = Program.RunWatchLoopAsync(service, lines.Add, cts.Token);

        source.KillSource("volume unmounted");
        var stopped = await loopTask;

        Assert.That(stopped, Is.True);
        Assert.That(lines, Does.Contain("Broker died: volume unmounted"));
    }
}

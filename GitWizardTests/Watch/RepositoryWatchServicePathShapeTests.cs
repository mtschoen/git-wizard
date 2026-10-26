using GitWizard.Watch;
using GitWizardTests.TestSupport;

namespace GitWizardTests.Watch;

// Watch tests are split by concern because the combined fixture exceeds 500 lines.
// Covers RepositoryWatchService (case normalization and cross-platform path handling).
[TestFixture]
public class RepositoryWatchServicePathShapeTests
{
    static async Task<List<RepositoryChangeEvent>> DrainAsync(RepositoryWatchService svc)
    {
        var events = new List<RepositoryChangeEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var ev in svc.RunAsync(cts.Token))
        {
            events.Add(ev);
        }

        return events;
    }

    [Test]
    public async Task PosixCaseDistinctRootsWithinDebounceWindow_EmitSeparateEvents()
    {
        var batches = new[]
        {
                new VolumeChangeBatch("/", [
                    new VolumeChangeEntry("/repo/A/one.txt", VolumeEntryKind.Modified)
                ]),
                new VolumeChangeBatch("/", [
                    new VolumeChangeEntry("/repo/a/two.txt", VolumeEntryKind.Modified)
                ]),
            };
        var svc = new RepositoryWatchService(
            new ScriptedVolumeChangeSource(batches),
            trackedRoots: ["/repo/A", "/repo/a"],
            searchRoots: [],
            debounce: TimeSpan.FromMilliseconds(10));

        var events = await DrainAsync(svc);

        Assert.That(events, Is.EquivalentTo([
                new RepositoryChangeEvent("/repo/A", RepositoryChangeKind.Changed),
                new RepositoryChangeEvent("/repo/a", RepositoryChangeKind.Changed),
            ]));
    }

    [Test]
    public async Task WindowsCaseVariantsWithinDebounceWindow_Coalesce()
    {
        var batches = new[]
        {
                new VolumeChangeBatch("C", [
                    new VolumeChangeEntry(@"C:\repo\New\.git", VolumeEntryKind.Created)
                ]),
                new VolumeChangeBatch("C", [
                    new VolumeChangeEntry(@"C:\repo\new\.git", VolumeEntryKind.Created)
                ]),
            };
        var svc = new RepositoryWatchService(
            new ScriptedVolumeChangeSource(batches),
            trackedRoots: [],
            searchRoots: [@"C:\repo"]);


        var events = await DrainAsync(svc);

        // The first create's root is adopted into the running filter as soon as it classifies,
        // so the case-varied second create maps to a Changed on that root instead of a second
        // Created. The single-Created coalescing this test pins is unchanged.
        Assert.That(events, Has.Count.EqualTo(2));
        Assert.That(events, Does.Contain(
            new RepositoryChangeEvent(@"C:\repo\New", RepositoryChangeKind.Created)));
        Assert.That(events, Does.Contain(
            new RepositoryChangeEvent(@"C:\repo\New", RepositoryChangeKind.Changed)));
    }

    [Test]
    public async Task UncCaseVariedGitDirectory_EmitsCreatedWithUncPath()
    {
        const string volume = @"\\Server\Share";
        var batch = new VolumeChangeBatch(volume, [
                new VolumeChangeEntry(@"\\server\share\repos\NewRepo\.GIT", VolumeEntryKind.Created)
            ]);
        var service = new RepositoryWatchService(
            new ScriptedVolumeChangeSource([batch]),
            trackedRoots: [],
            searchRoots: [@"\\Server\Share\Repos"],
            debounce: TimeSpan.FromMilliseconds(10));

        var events = await DrainAsync(service);

        Assert.That(events, Is.EqualTo([
                new RepositoryChangeEvent(@"\\server\share\repos\NewRepo", RepositoryChangeKind.Created)
            ]));
    }

    [Test]
    public async Task RootedWindowsCaseVariedChange_EmitsOriginalRoot()
    {
        const string repositoryRoot = @"\Repos\Repo";
        var batch = new VolumeChangeBatch(@"\", [
                new VolumeChangeEntry(@"\repos\repo\file.txt", VolumeEntryKind.Modified)
            ]);
        var service = new RepositoryWatchService(
            new ScriptedVolumeChangeSource([batch]),
            trackedRoots: [repositoryRoot],
            searchRoots: [],
            debounce: TimeSpan.FromMilliseconds(10));

        var events = await DrainAsync(service);

        Assert.That(events, Is.EqualTo([
                new RepositoryChangeEvent(repositoryRoot, RepositoryChangeKind.Changed)
            ]));
    }

    [Test]
    public async Task DriveRootChangedAndDeleted_EmitAbsoluteOriginalRoot()
    {
        const string repositoryRoot = @"C:\";
        var batch = new VolumeChangeBatch("C", [
                new VolumeChangeEntry(@"c:\file.txt", VolumeEntryKind.Modified),
                new VolumeChangeEntry(@"c:\", VolumeEntryKind.Deleted),
            ]);
        var service = new RepositoryWatchService(
            new ScriptedVolumeChangeSource([batch]),
            trackedRoots: [repositoryRoot],
            searchRoots: [],
            debounce: TimeSpan.FromMilliseconds(10));

        var events = await DrainAsync(service);

        Assert.That(events, Is.EquivalentTo([
                new RepositoryChangeEvent(repositoryRoot, RepositoryChangeKind.Changed),
                new RepositoryChangeEvent(repositoryRoot, RepositoryChangeKind.Deleted),
            ]));
    }

    [Test]
    public async Task PosixTrackedRoot_EmitsEventWithOriginalPath()
    {
        var batch = new VolumeChangeBatch("/", [
                new VolumeChangeEntry("/repo/a/file.txt", VolumeEntryKind.Modified)
            ]);
        var svc = new RepositoryWatchService(
            new ScriptedVolumeChangeSource([batch]),
            trackedRoots: ["/repo/a"],
            searchRoots: [],
            debounce: TimeSpan.FromMilliseconds(10));

        var events = await DrainAsync(svc);

        Assert.That(events, Is.EqualTo([
                new RepositoryChangeEvent("/repo/a", RepositoryChangeKind.Changed)
            ]));
    }

    [Test]
    public async Task RootWithoutDriveLetter_IsIgnoredWhenGroupingByVolume()
    {
        var svc = new RepositoryWatchService(
            new ScriptedVolumeChangeSource([]),
            trackedRoots: ["relative-repo"],
            searchRoots: [],
            debounce: TimeSpan.FromMilliseconds(10));

        var events = await DrainAsync(svc);

        Assert.That(events, Is.Empty);
    }
}

using GitWizard.Watch;

namespace GitWizardTests.Watch;

public partial class RepositoryWatchServiceTests
{
    [Test]
    public async Task PosixCaseDistinctRootsWithinDebounceWindow_EmitSeparateEvents()
    {
        var batches = new[]
        {
            new VolumeChangeBatch("/", new[]
            {
                new VolumeChangeEntry("/repo/A/one.txt", VolumeEntryKind.Modified)
            }),
            new VolumeChangeBatch("/", new[]
            {
                new VolumeChangeEntry("/repo/a/two.txt", VolumeEntryKind.Modified)
            }),
        };
        var svc = new RepositoryWatchService(
            new FakeVolumeChangeSource(batches),
            trackedRoots: new[] { "/repo/A", "/repo/a" },
            searchRoots: Array.Empty<string>(),
            debounce: TimeSpan.FromMilliseconds(10));

        var events = await DrainAsync(svc);

        Assert.That(events, Is.EquivalentTo(new[]
        {
            new RepositoryChangeEvent("/repo/A", RepositoryChangeKind.Changed),
            new RepositoryChangeEvent("/repo/a", RepositoryChangeKind.Changed),
        }));
    }

    [Test]
    public async Task WindowsCaseVariantsWithinDebounceWindow_Coalesce()
    {
        var batches = new[]
        {
            new VolumeChangeBatch("C", new[]
            {
                new VolumeChangeEntry(@"C:\repo\New\.git", VolumeEntryKind.Created)
            }),
            new VolumeChangeBatch("C", new[]
            {
                new VolumeChangeEntry(@"C:\repo\new\.git", VolumeEntryKind.Created)
            }),
        };
        var svc = new RepositoryWatchService(
            new FakeVolumeChangeSource(batches),
            trackedRoots: Array.Empty<string>(),
            searchRoots: new[] { @"C:\repo" },
            debounce: TimeSpan.FromMilliseconds(10));

        var events = await DrainAsync(svc);

        Assert.That(events, Is.EqualTo(new[]
        {
            new RepositoryChangeEvent(@"C:\repo\New", RepositoryChangeKind.Created)
        }));
    }

    [Test]
    public async Task UncCaseVariedGitDirectory_EmitsCreatedWithUncPath()
    {
        const string volume = @"\\Server\Share";
        var batch = new VolumeChangeBatch(volume, new[]
        {
            new VolumeChangeEntry(@"\\server\share\repos\NewRepo\.GIT", VolumeEntryKind.Created)
        });
        var service = new RepositoryWatchService(
            new FakeVolumeChangeSource(new[] { batch }),
            trackedRoots: Array.Empty<string>(),
            searchRoots: new[] { @"\\Server\Share\Repos" },
            debounce: TimeSpan.FromMilliseconds(10));

        var events = await DrainAsync(service);

        Assert.That(events, Is.EqualTo(new[]
        {
            new RepositoryChangeEvent(@"\\server\share\repos\NewRepo", RepositoryChangeKind.Created)
        }));
    }

    [Test]
    public async Task RootedWindowsCaseVariedChange_EmitsOriginalRoot()
    {
        const string repositoryRoot = @"\Repos\Repo";
        var batch = new VolumeChangeBatch(@"\", new[]
        {
            new VolumeChangeEntry(@"\repos\repo\file.txt", VolumeEntryKind.Modified)
        });
        var service = new RepositoryWatchService(
            new FakeVolumeChangeSource(new[] { batch }),
            trackedRoots: new[] { repositoryRoot },
            searchRoots: Array.Empty<string>(),
            debounce: TimeSpan.FromMilliseconds(10));

        var events = await DrainAsync(service);

        Assert.That(events, Is.EqualTo(new[]
        {
            new RepositoryChangeEvent(repositoryRoot, RepositoryChangeKind.Changed)
        }));
    }

    [Test]
    public async Task DriveRootChangedAndDeleted_EmitAbsoluteOriginalRoot()
    {
        const string repositoryRoot = @"C:\";
        var batch = new VolumeChangeBatch("C", new[]
        {
            new VolumeChangeEntry(@"c:\file.txt", VolumeEntryKind.Modified),
            new VolumeChangeEntry(@"c:\", VolumeEntryKind.Deleted),
        });
        var service = new RepositoryWatchService(
            new FakeVolumeChangeSource(new[] { batch }),
            trackedRoots: new[] { repositoryRoot },
            searchRoots: Array.Empty<string>(),
            debounce: TimeSpan.FromMilliseconds(10));

        var events = await DrainAsync(service);

        Assert.That(events, Is.EquivalentTo(new[]
        {
            new RepositoryChangeEvent(repositoryRoot, RepositoryChangeKind.Changed),
            new RepositoryChangeEvent(repositoryRoot, RepositoryChangeKind.Deleted),
        }));
    }
}

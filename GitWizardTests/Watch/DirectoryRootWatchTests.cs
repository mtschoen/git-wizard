using GitWizard;
using GitWizard.Watch;
using GitWizardTests.TestSupport;
using MFTLib.Index;
using MFTLibTestExtensions;

namespace GitWizardTests.Watch;

[TestFixture]
public class DirectoryRootWatchTests
{
    // The fixture tree numbers its directories in walk order after the root row (5): first, first/repo, first/repo/.git, second, second/repo, second/repo/.git.
    const ulong FirstDirectoryRow = 6;
    const ulong SecondDirectoryRow = 9;

    [Test]
    public async Task SiblingViewsOnOneVolumeShareOneWatchAndEachReceivesOnlyItsOwnChanges()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("first/repo");
        files.AddMarker("second/repo");
        var firstRoot = RootKey.FromDirectory(Path.Combine(files.TreePath, "first"));
        var secondRoot = RootKey.FromDirectory(Path.Combine(files.TreePath, "second"));
        var scripted = new ScriptedWatchSource();
        var indexSource = SyntheticIndexSource.Create(files.Producer, scripted);
        var first = await files.OpenMftIndexAsync(indexSource, 'X', firstRoot);
        var second = await files.OpenMftIndexAsync(indexSource, 'X', secondRoot);
        var roots = new[] { firstRoot.ToString(), secondRoot.ToString() };
        var owner = new MftIndexSession([first, second], null, roots, new Dictionary<string, string>());
        await using var source = IndexVolumeChangeSource.CreatePrepared(owner);
        source.StartStartupBuffering();
        using var cancellation = new CancellationTokenSource(TestTimeouts.HangGuard);
        var arming = source.ArmAndCatchUpAsync(roots, cancellation.Token);
        await scripted.WaitForStartAsync('X', 1, cancellation.Token);
        await scripted.WatchFor('X').PublishCaughtUpAsync();
        await arming;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(source.Readiness.CaughtUpVolumes, Is.EqualTo(["X"]));
            Assert.That(scripted.Starts, Has.Count.EqualTo(1), "both views share one watch");
        }

        var firstPaths = new List<string>();
        var secondPaths = new List<string>();
        var firstApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.Changed += change =>
        {
            firstPaths.Add(change.Path);
            firstApplied.TrySetResult();
        };
        second.Changed += change =>
        {
            secondPaths.Add(change.Path);
            secondApplied.TrySetResult();
        };
        await scripted.WatchFor('X').PublishBatchAsync([
            PackedIndexFixture.Entry(100, FirstDirectoryRow, "inside-first.txt", SyntheticJournalReason.FileCreate),
            PackedIndexFixture.Entry(101, SecondDirectoryRow, "inside-second.txt", SyntheticJournalReason.FileCreate)
        ], new SyntheticJournalCursor(PackedIndexFixture.JournalIdentifier, 1002));
        await Task.WhenAll(firstApplied.Task, secondApplied.Task).WaitAsync(cancellation.Token);

        var delivered = source.CompleteStartupBuffer().SelectMany(batch => batch.Entries)
            .Select(entry => entry.FullPath).ToArray();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstPaths, Has.Count.EqualTo(1));
            Assert.That(firstPaths[0], Does.EndWith("inside-first.txt"));
            Assert.That(secondPaths, Has.Count.EqualTo(1));
            Assert.That(secondPaths[0], Does.EndWith("inside-second.txt"));
            Assert.That(delivered.Count(path => path.EndsWith("inside-first.txt", StringComparison.Ordinal)), Is.EqualTo(1));
            Assert.That(delivered.Count(path => path.EndsWith("inside-second.txt", StringComparison.Ordinal)), Is.EqualTo(1));
        }
    }

    const ulong NestedDirectoryRow = 7;

    static async Task<(IndexVolumeChangeSource Source, MftIndexSession Owner, ScriptedWatchSource Scripted,
        RootKey OuterRoot, RootKey NestedRoot)> ArmNestedViewsAsync(PackedIndexFixture files)
    {
        files.AddMarker("outer/repo");
        Directory.CreateDirectory(Path.Combine(files.TreePath, "outer", "nested"));
        var outerRoot = RootKey.FromDirectory(Path.Combine(files.TreePath, "outer"));
        var nestedRoot = RootKey.FromDirectory(Path.Combine(files.TreePath, "outer", "nested"));
        var scripted = new ScriptedWatchSource();
        var indexSource = SyntheticIndexSource.Create(files.Producer, scripted);
        var outer = await files.OpenMftIndexAsync(indexSource, 'X', outerRoot);
        var nested = await files.OpenMftIndexAsync(indexSource, 'X', nestedRoot);
        var roots = new[] { outerRoot.ToString(), nestedRoot.ToString() };
        var owner = new MftIndexSession([outer, nested], null, roots, new Dictionary<string, string>());
        var source = IndexVolumeChangeSource.CreatePrepared(owner);
        source.StartStartupBuffering();
        var arming = source.ArmAndCatchUpAsync(roots, CancellationToken.None);
        await scripted.WaitForStartAsync('X', 1, CancellationToken.None).WaitAsync(TestTimeouts.HangGuard);
        await scripted.WatchFor('X').PublishCaughtUpAsync();
        await arming;
        return (source, owner, scripted, outerRoot, nestedRoot);
    }

    static async Task PublishAndWaitForBothViewsAsync(
        MftIndexSession owner, ScriptedWatchSource scripted, params SyntheticJournalRecord[] records)
    {
        var outerApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nestedApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.Indices[0].Changed += _ => outerApplied.TrySetResult();
        owner.Indices[1].Changed += _ => nestedApplied.TrySetResult();
        await scripted.WatchFor('X').PublishBatchAsync(records,
            new SyntheticJournalCursor(PackedIndexFixture.JournalIdentifier, 1002));
        await Task.WhenAll(outerApplied.Task, nestedApplied.Task).WaitAsync(TestTimeouts.HangGuard);
    }

    [Test]
    public async Task NestedViewsDeliverCreatedMarkerOnlyOnce()
    {
        using var files = new PackedIndexFixture();
        var (source, owner, scripted, _, _) = await ArmNestedViewsAsync(files);
        await using var _ = source;
        await PublishAndWaitForBothViewsAsync(owner, scripted,
            PackedIndexFixture.Entry(50, NestedDirectoryRow, "newrepo", SyntheticJournalReason.FileCreate, directory: true),
            PackedIndexFixture.Entry(51, 50, ".git", SyntheticJournalReason.FileCreate, directory: true));

        var delivered = source.CompleteStartupBuffer().SelectMany(batch => batch.Entries)
            .Where(entry => entry.FullPath.EndsWith(Path.Combine("newrepo", ".git"), StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.That(delivered, Has.Length.EqualTo(1));
    }

    [Test]
    public async Task CrossViewRenameUsesOnlyTheOutermostParticipatingView()
    {
        using var files = new PackedIndexFixture();
        var (source, owner, scripted, _, _) = await ArmNestedViewsAsync(files);
        await using var _ = source;
        await PublishAndWaitForBothViewsAsync(owner, scripted,
            PackedIndexFixture.Entry(8, NestedDirectoryRow, "repo", SyntheticJournalReason.RenameNewName, directory: true));

        var delivered = source.CompleteStartupBuffer().SelectMany(batch => batch.Entries).ToArray();
        Assert.That(delivered, Is.EqualTo([
            new VolumeChangeEntry(Path.Combine(files.TreePath, "outer", "repo"), VolumeEntryKind.Deleted),
            new VolumeChangeEntry(Path.Combine(files.TreePath, "outer", "nested", "repo"), VolumeEntryKind.MovedIn)
        ]));
    }

    static async Task<(IndexVolumeChangeSource Source, MftIndexSession Owner, ScriptedWatchSource Scripted)>
        ArmDirectoryRootsAsync(PackedIndexFixture files, params string[] roots)
    {
        var scripted = new ScriptedWatchSource();
        var indexSource = SyntheticIndexSource.Create(files.Producer, scripted);
        var indices = await Task.WhenAll(roots.Select(root =>
            files.OpenMftIndexAsync(indexSource, 'X', RootKey.FromDirectory(root))));
        var owner = new MftIndexSession(indices, null, roots, new Dictionary<string, string>());
        var source = IndexVolumeChangeSource.CreatePrepared(owner);
        source.StartStartupBuffering();
        var arming = source.ArmAndCatchUpAsync(roots, CancellationToken.None);
        await scripted.WaitForStartAsync('X', 1, CancellationToken.None).WaitAsync(TestTimeouts.HangGuard);
        await scripted.WatchFor('X').PublishCaughtUpAsync();
        await arming;
        return (source, owner, scripted);
    }

    static async Task PublishAndWaitForViewsAsync(
        MftIndexSession owner, ScriptedWatchSource scripted, params SyntheticJournalRecord[] records)
    {
        var applied = owner.Indices.Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        for (var index = 0; index < owner.Indices.Count; index++)
        {
            var completion = applied[index];
            owner.Indices[index].Changed += _ => completion.TrySetResult();
        }
        await scripted.WatchFor('X').PublishBatchAsync(records,
            new SyntheticJournalCursor(PackedIndexFixture.JournalIdentifier, 1002));
        await Task.WhenAll(applied.Select(item => item.Task)).WaitAsync(TestTimeouts.HangGuard);
    }

    static async Task<IReadOnlyList<RepositoryChangeEvent>> RunStartupServiceAsync(
        IndexVolumeChangeSource source,
        ScriptedWatchSource scripted,
        IReadOnlyList<string> searchRoots,
        IReadOnlyList<string> ignoredRoots,
        IReadOnlyList<string> trackedRoots,
        IReadOnlyList<VolumeChangeBatch> startupBatches)
    {
        var configuration = new GitWizardConfiguration { IncludeTempDirectories = true };
        foreach (var root in searchRoots)
        {
            configuration.SearchPaths.Add(root);
        }
        foreach (var root in ignoredRoots)
        {
            configuration.IgnoredPaths.Add(root);
        }
        var request = new DiscoveryRequest(configuration, DiscoveryPurpose.Watch, false, false, trackedRoots);
        var scope = DiscoveryScope.CreateExpanded(request, searchRoots, trackedRoots, ignoredRoots);
        var inventory = new DiscoveryInventory
        {
            Outcome = DiscoveryOutcome.CompleteNonempty,
            Route = DiscoveryRoute.Indexed,
            Paths = trackedRoots,
            RequestedNtfsDrives = ["X"],
            Observations = [],
            RootStatuses = [],
            Errors = new Dictionary<string, string>(),
            WatchExcludedRoots = []
        };
        await using var startup = new WatchStartupResult(
            inventory, new VolumeArmResult(new Dictionary<string, string>(), []), source, ["X"], scope, startupBatches);
        foreach (var root in startup.SearchRoots)
        {
            Assert.That(startup.GetObservedVolume(root), Is.EqualTo("X"), $"No observed volume for resolved root {root}");
        }
        var service = new RepositoryWatchService(startup, debounce: TimeSpan.Zero);
        service.Ready += _ => scripted.WatchFor('X').LoseChannel(new IOException("stop"));
        var events = new List<RepositoryChangeEvent>();
        using var cancellation = new CancellationTokenSource(TestTimeouts.HangGuard);
        await foreach (var changeEvent in service.RunAsync(cancellation.Token))
        {
            events.Add(changeEvent);
        }
        return events;
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task ObservedVolumeControlsAliasAndMountedRootFilters(bool useAlias)
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repo");
        var root = files.TreePath;
        if (useAlias)
        {
            root = Path.Combine(files.DirectoryPath, "alias");
            Directory.CreateSymbolicLink(root, files.TreePath);
        }
        var (source, owner, scripted) = await ArmDirectoryRootsAsync(files, root);
        var ignored = Path.Combine(root, "ignored");
        await PublishAndWaitForViewsAsync(owner, scripted,
            PackedIndexFixture.Entry(50, 5, "newrepo", SyntheticJournalReason.FileCreate, directory: true),
            PackedIndexFixture.Entry(51, 50, ".git", SyntheticJournalReason.FileCreate, directory: true),
            PackedIndexFixture.Entry(52, 5, "ignored", SyntheticJournalReason.FileCreate, directory: true),
            PackedIndexFixture.Entry(53, 52, ".git", SyntheticJournalReason.FileCreate, directory: true));

        var events = await RunStartupServiceAsync(source, scripted, [root], [ignored],
            [Path.Combine(files.TreePath, "repo")], source.CompleteStartupBuffer());
        Assert.That(events, Is.EqualTo([
            new RepositoryChangeEvent(Path.Combine(files.TreePath, "newrepo"), RepositoryChangeKind.Created)
        ]));
    }

    [Test]
    public async Task NestedViewsEmitOneCreatedEventThroughService()
    {
        using var files = new PackedIndexFixture();
        var (source, owner, scripted, outerRoot, nestedRoot) = await ArmNestedViewsAsync(files);
        await PublishAndWaitForBothViewsAsync(owner, scripted,
            PackedIndexFixture.Entry(50, NestedDirectoryRow, "newrepo", SyntheticJournalReason.FileCreate, directory: true),
            PackedIndexFixture.Entry(51, 50, ".git", SyntheticJournalReason.FileCreate, directory: true));

        var events = await RunStartupServiceAsync(source, scripted,
            [outerRoot.ToString(), nestedRoot.ToString()], [],
            [Path.Combine(files.TreePath, "outer", "repo")], source.CompleteStartupBuffer());
        Assert.That(events, Is.EqualTo([
            new RepositoryChangeEvent(Path.Combine(files.TreePath, "outer", "nested", "newrepo"), RepositoryChangeKind.Created)
        ]));
    }
}

using GitWizard;
using GitWizardTests.TestSupport;
using MFTLib;
using MFTLib.Index;

namespace GitWizardTests.Discovery;

[TestFixture]
public class WindowsWatchStartupTests
{
    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);


    [Test]
    public async Task WarmReplay_WithholdsCatchUp_ReconcilesMembershipAfterCatchUp()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repo");
        var scripted = new ScriptedIndexWatchSource { AutoCatchUp = false };
        var index = await files.OpenMftIndexAsync(scripted, 'X');
        var connection = new CountingOwner();
        var session = new MftIndexSession(index, connection, ["X"], [new IndexedDrive('X', files.TreePath, 1)], new Dictionary<string, string>());

        var listWrites = new List<IReadOnlyList<string>>();
        var treePath = files.TreePath;
        var coordinator = new RepositoryDiscoveryCoordinator(new RepositoryDiscoveryDependencies
        {
            IsWindows = true,
            IsLinux = false,
            ClassifyRoot = _ => new RootClassification(treePath, RootFileSystem.Ntfs, "X", null),
            CrawlRootAsync = (_, _, _) => throw new AssertionException("NTFS roots must not be crawled."),
            OpenIndexAsync = (_, _, _, _) => Task.FromResult(session),
            ReadListAsync = _ => Task.FromResult<string[]?>([]),
            WriteListAsync = (paths, _) => { listWrites.Add(paths); return Task.CompletedTask; },
            CandidateExists = (_, _) => true,
            CreateLinuxSource = () => throw new PlatformNotSupportedException()
        });

        var config = new GitWizardConfiguration { SearchPaths = { files.TreePath } };
        var request = new DiscoveryRequest(config, DiscoveryPurpose.Watch, false, false, []);
        using var cts = new CancellationTokenSource(HangGuard);

        var startupTask = coordinator.PrepareWatchAsync(request, null, cts.Token);

        await scripted.Started.WaitAsync(cts.Token);
        Assert.That(startupTask.IsCompleted, Is.False, "Startup must withhold completion until catch-up drains.");
        Assert.That(listWrites, Is.Empty, "List writes must not happen before catch-up.");

        // Publish journal changes and caught up marker
        scripted.Publish(new JournalBatch('X',
        [
            Entry(50, 6, "ordinary.txt", UsnReason.FileCreate)
        ], PackedIndexFixture.JournalId, 1002));
        scripted.Publish(new DriveCaughtUp('X'));

        await using var startup = await startupTask;

        Assert.That(startup.Inventory.Outcome, Is.EqualTo(DiscoveryOutcome.CompleteNonempty));
        Assert.That(startup.Inventory.Paths, Does.Contain(Path.Combine(files.TreePath, "repo")));
        Assert.That(startup.Source, Is.Not.Null);
        Assert.That(startup.WatchVolumes, Is.EquivalentTo(new[] { "X" }));
        Assert.That(listWrites, Is.Not.Empty);
    }

    [Test]
    public async Task ReadableOldList_DoesNotSkipIndexOpenOrCatchUp()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repo");
        var scripted = new ScriptedIndexWatchSource { AutoCatchUp = true };
        var index = await files.OpenMftIndexAsync(scripted, 'X');
        var connection = new CountingOwner();
        var session = new MftIndexSession(index, connection, ["X"], [new IndexedDrive('X', files.TreePath, 1)], new Dictionary<string, string>());
        var openCalled = false;
        var treePath = files.TreePath;

        var coordinator = new RepositoryDiscoveryCoordinator(new RepositoryDiscoveryDependencies
        {
            IsWindows = true,
            IsLinux = false,
            ClassifyRoot = _ => new RootClassification(treePath, RootFileSystem.Ntfs, "X", null),
            CrawlRootAsync = (_, _, _) => throw new AssertionException("NTFS roots must not be crawled."),
            OpenIndexAsync = (_, _, _, _) => { openCalled = true; return Task.FromResult(session); },
            ReadListAsync = _ => Task.FromResult<string[]?>([@"X:\old\repo"]),
            WriteListAsync = (_, _) => Task.CompletedTask,
            CandidateExists = (_, _) => true,
            CreateLinuxSource = () => throw new PlatformNotSupportedException()
        });

        var config = new GitWizardConfiguration { SearchPaths = { files.TreePath } };
        var request = new DiscoveryRequest(config, DiscoveryPurpose.Watch, true, false, []);
        using var cts = new CancellationTokenSource(HangGuard);

        await using var startup = await coordinator.PrepareWatchAsync(request, null, cts.Token);

        Assert.That(openCalled, Is.True, "Watch preparation must open index even when a cached list exists.");
        Assert.That(startup.Inventory.Outcome, Is.EqualTo(DiscoveryOutcome.CompleteNonempty));
    }

    [Test]
    public async Task EmptyOldSnapshot_WithNoReplay_ProducesCompleteEmptyAndDisposedSource()
    {
        using var files = new PackedIndexFixture();
        var scripted = new ScriptedIndexWatchSource { AutoCatchUp = true };
        var index = await files.OpenMftIndexAsync(scripted, 'X');
        var connection = new CountingOwner();
        var session = new MftIndexSession(index, connection, ["X"], [new IndexedDrive('X', files.TreePath, 1)], new Dictionary<string, string>());
        var treePath = files.TreePath;

        var coordinator = new RepositoryDiscoveryCoordinator(new RepositoryDiscoveryDependencies
        {
            IsWindows = true,
            IsLinux = false,
            ClassifyRoot = _ => new RootClassification(treePath, RootFileSystem.Ntfs, "X", null),
            CrawlRootAsync = (_, _, _) => throw new AssertionException("NTFS roots must not be crawled."),
            OpenIndexAsync = (_, _, _, _) => Task.FromResult(session),
            ReadListAsync = _ => Task.FromResult<string[]?>([]),
            WriteListAsync = (_, _) => Task.CompletedTask,
            CandidateExists = (_, _) => true,
            CreateLinuxSource = () => throw new PlatformNotSupportedException()
        });

        var config = new GitWizardConfiguration { SearchPaths = { files.TreePath } };
        var request = new DiscoveryRequest(config, DiscoveryPurpose.Watch, false, false, []);
        using var cts = new CancellationTokenSource(HangGuard);

        await using var startup = await coordinator.PrepareWatchAsync(request, null, cts.Token);

        Assert.That(startup.Inventory.Outcome, Is.EqualTo(DiscoveryOutcome.CompleteEmpty));
        Assert.That(startup.Source, Is.Null, "Clean complete empty must dispose and clear the source.");
    }

    [Test]
    public async Task RetainedRootOutsideConfiguredScope_IsArmed()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repo");
        var retainedBase = Path.Combine(files.DirectoryPath, "retained");
        IReadOnlyList<string>? armedDrives = null;
        var fixture = files;

        var coordinator = new RepositoryDiscoveryCoordinator(new RepositoryDiscoveryDependencies
        {
            IsWindows = true,
            IsLinux = false,
            ClassifyRoot = root => root.StartsWith(retainedBase, StringComparison.OrdinalIgnoreCase)
                ? new RootClassification(root, RootFileSystem.Ntfs, "E", null)
                : new RootClassification(root, RootFileSystem.Ntfs, "X", null),
            CrawlRootAsync = (_, _, _) => throw new AssertionException("NTFS roots must not be crawled."),
            OpenIndexAsync = (drives, _, _, _) =>
            {
                armedDrives = drives;
                return Task.FromResult(new MftIndexSession(fixture.OpenIndexAsync().GetAwaiter().GetResult(), new CountingOwner(), drives, [], new Dictionary<string, string>()));
            },
            ReadListAsync = _ => Task.FromResult<string[]?>([]),
            WriteListAsync = (_, _) => Task.CompletedTask,
            CandidateExists = (_, _) => true,
            CreateLinuxSource = () => throw new PlatformNotSupportedException()
        });

        var config = new GitWizardConfiguration { SearchPaths = { files.TreePath } };
        var request = new DiscoveryRequest(config, DiscoveryPurpose.Watch, false, false, [Path.Combine(retainedBase, "repo")]);
        using var cts = new CancellationTokenSource(HangGuard);

        await using var startup = await coordinator.PrepareWatchAsync(request, null, cts.Token);

        Assert.That(armedDrives, Is.EquivalentTo(new[] { "E", "X" }));
    }

    [Test]
    public async Task NonNtfsRoot_ExcludedFromJournalWatch_AppearsInExcludedRoots()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repo");
        var nonNtfsDir = Directory.CreateDirectory(Path.Combine(files.DirectoryPath, "shared")).FullName;
        IReadOnlyList<string>? openedDrives = null;
        var fixture = files;

        var coordinator = new RepositoryDiscoveryCoordinator(new RepositoryDiscoveryDependencies
        {
            IsWindows = true,
            IsLinux = false,
            ClassifyRoot = root => string.Equals(root, nonNtfsDir, StringComparison.OrdinalIgnoreCase)
                ? new RootClassification(root, RootFileSystem.Recursive, null, null)
                : new RootClassification(root, RootFileSystem.Ntfs, "X", null),
            CrawlRootAsync = (_, root, _) => Task.FromResult(new RecursiveDiscovery([Path.Combine(root, "repo")], [], new Dictionary<string, string>())),
            OpenIndexAsync = (drives, _, _, _) =>
            {
                openedDrives = drives;
                return Task.FromResult(new MftIndexSession(fixture.OpenIndexAsync().GetAwaiter().GetResult(), new CountingOwner(), drives, [], new Dictionary<string, string>()));
            },
            ReadListAsync = _ => Task.FromResult<string[]?>([]),
            WriteListAsync = (_, _) => Task.CompletedTask,
            CandidateExists = (_, _) => true,
            CreateLinuxSource = () => throw new PlatformNotSupportedException()
        });

        var config = new GitWizardConfiguration { SearchPaths = { files.TreePath, nonNtfsDir } };
        var request = new DiscoveryRequest(config, DiscoveryPurpose.Watch, false, false, []);
        using var cts = new CancellationTokenSource(HangGuard);

        await using var startup = await coordinator.PrepareWatchAsync(request, null, cts.Token);

        Assert.That(openedDrives, Is.EquivalentTo(new[] { "X" }), "Z: must not be sent to MFT opener.");
        Assert.That(startup.Inventory.WatchExcludedRoots, Is.EquivalentTo(new[] { nonNtfsDir }).IgnoreCase);
    }

    [Test]
    public async Task FailedDrive_DoesNotTakeDownHealthyDrive_ProducesPartial()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repo");
        var yDir = Directory.CreateDirectory(Path.Combine(files.DirectoryPath, "y-repos")).FullName;
        var scripted = new ScriptedIndexWatchSource { AutoCatchUp = false };
        var index = await files.OpenMftIndexAsync(scripted, 'X', 'Y');
        var connection = new CountingOwner();
        var session = new MftIndexSession(index, connection, ["X", "Y"], [
            new IndexedDrive('X', files.TreePath, 1),
            new IndexedDrive('Y', yDir, 2)
        ], new Dictionary<string, string>());

        var coordinator = new RepositoryDiscoveryCoordinator(new RepositoryDiscoveryDependencies
        {
            IsWindows = true,
            IsLinux = false,
            ClassifyRoot = root => string.Equals(root, yDir, StringComparison.OrdinalIgnoreCase)
                ? new RootClassification(root, RootFileSystem.Ntfs, "Y", null)
                : new RootClassification(root, RootFileSystem.Ntfs, "X", null),
            CrawlRootAsync = (_, _, _) => throw new AssertionException("NTFS roots must not be crawled."),
            OpenIndexAsync = (_, _, _, _) => Task.FromResult(session),
            ReadListAsync = _ => Task.FromResult<string[]?>([]),
            WriteListAsync = (_, _) => Task.CompletedTask,
            CandidateExists = (_, _) => true,
            CreateLinuxSource = () => throw new PlatformNotSupportedException()
        });

        var config = new GitWizardConfiguration { SearchPaths = { files.TreePath, yDir } };
        var request = new DiscoveryRequest(config, DiscoveryPurpose.Watch, false, false, []);
        using var cts = new CancellationTokenSource(HangGuard);

        var startupTask = coordinator.PrepareWatchAsync(request, null, cts.Token);
        await scripted.Started.WaitAsync(cts.Token);

        // Drive X catches up, Drive Y faults
        scripted.Publish(new DriveWatchFailure('Y', new IOException("Y journal corrupt")));
        scripted.Publish(new DriveCaughtUp('X'));

        await using var startup = await startupTask;

        Assert.That(startup.Inventory.Outcome, Is.EqualTo(DiscoveryOutcome.Partial));
        Assert.That(startup.Source, Is.Not.Null, "Healthy drive X must keep source active.");
        Assert.That(startup.Inventory.Errors, Does.ContainKey("Y"));
    }

    [Test]
    public async Task StrictMismatch_DisposesOldSessionAndCallsOpener()
    {
        using var files = new PackedIndexFixture();
        var connection = new CountingOwner();
        var oldSession = new MftIndexSession(await files.OpenIndexAsync(), connection, ["X"], [], new Dictionary<string, string>());
        var openerCalled = false;
        var fixture = files;

        var coordinator = new RepositoryDiscoveryCoordinator(new RepositoryDiscoveryDependencies
        {
            IsWindows = true,
            IsLinux = false,
            ClassifyRoot = _ => new RootClassification(fixture.TreePath, RootFileSystem.Ntfs, "Y", null),
            CrawlRootAsync = (_, _, _) => throw new AssertionException("NTFS roots must not be crawled."),
            OpenIndexAsync = (drives, _, _, _) =>
            {
                openerCalled = true;
                return Task.FromResult(new MftIndexSession(fixture.OpenIndexAsync().GetAwaiter().GetResult(), new CountingOwner(), drives, [], new Dictionary<string, string>()));
            },
            ReadListAsync = _ => Task.FromResult<string[]?>([]),
            WriteListAsync = (_, _) => Task.CompletedTask,
            CandidateExists = (_, _) => true,
            CreateLinuxSource = () => throw new PlatformNotSupportedException()
        });

        var config = new GitWizardConfiguration { SearchPaths = { files.TreePath } };
        var request = new DiscoveryRequest(config, DiscoveryPurpose.Watch, false, false, []);
        using var cts = new CancellationTokenSource(HangGuard);

        await using var startup = await coordinator.PrepareWatchAsync(request, oldSession, cts.Token);

        Assert.That(oldSession.IsDisposed, Is.True, "Mismatched session must be disposed.");
        Assert.That(openerCalled, Is.True, "Fallback opener must be called for new drives.");
    }

    [Test]
    public async Task StartupBuffering_Invalidation_CreateThenDeleteDoesNotResurrect()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repo");
        var scripted = new ScriptedIndexWatchSource { AutoCatchUp = false };
        var index = await files.OpenMftIndexAsync(scripted, 'X');
        var connection = new CountingOwner();
        var session = new MftIndexSession(index, connection, ["X"], [new IndexedDrive('X', files.TreePath, 1)], new Dictionary<string, string>());
        var treePath = files.TreePath;

        var coordinator = new RepositoryDiscoveryCoordinator(new RepositoryDiscoveryDependencies
        {
            IsWindows = true,
            IsLinux = false,
            ClassifyRoot = _ => new RootClassification(treePath, RootFileSystem.Ntfs, "X", null),
            CrawlRootAsync = (_, _, _) => throw new AssertionException("NTFS roots must not be crawled."),
            OpenIndexAsync = (_, _, _, _) => Task.FromResult(session),
            ReadListAsync = _ => Task.FromResult<string[]?>([]),
            WriteListAsync = (_, _) => Task.CompletedTask,
            CandidateExists = (path, _) => !path.EndsWith("temp-repo", StringComparison.OrdinalIgnoreCase),
            CreateLinuxSource = () => throw new PlatformNotSupportedException()
        });

        var config = new GitWizardConfiguration { SearchPaths = { files.TreePath } };
        var request = new DiscoveryRequest(config, DiscoveryPurpose.Watch, false, false, []);
        using var cts = new CancellationTokenSource(HangGuard);

        var startupTask = coordinator.PrepareWatchAsync(request, null, cts.Token);
        await scripted.Started.WaitAsync(cts.Token);

        // While buffering, simulate Create followed by Delete for temp-repo
        scripted.Publish(new JournalBatch('X',
        [
            Entry(60, 5, "temp-repo", UsnReason.FileCreate, isDirectory: true),
            Entry(61, 60, ".git", UsnReason.FileCreate, isDirectory: true),
            Entry(61, 60, ".git", UsnReason.FileDelete, isDirectory: true),
            Entry(60, 5, "temp-repo", UsnReason.FileDelete, isDirectory: true)
        ], PackedIndexFixture.JournalId, 1002));
        scripted.Publish(new DriveCaughtUp('X'));

        await using var startup = await startupTask;

        Assert.That(startup.Inventory.Paths.Any(p => p.EndsWith("temp-repo", StringComparison.OrdinalIgnoreCase)), Is.False, "Replay create-then-delete must not resurrect deleted repo.");
    }

    [Test]
    public async Task StartupBuffering_BufferedBatches_AttachedToStartupForTheService()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repo");
        var scripted = new ScriptedIndexWatchSource { AutoCatchUp = false };
        var index = await files.OpenMftIndexAsync(scripted, 'X');
        var connection = new CountingOwner();
        var session = new MftIndexSession(index, connection, ["X"], [new IndexedDrive('X', files.TreePath, 1)], new Dictionary<string, string>());
        var treePath = files.TreePath;

        var coordinator = new RepositoryDiscoveryCoordinator(new RepositoryDiscoveryDependencies
        {
            IsWindows = true,
            IsLinux = false,
            ClassifyRoot = _ => new RootClassification(treePath, RootFileSystem.Ntfs, "X", null),
            CrawlRootAsync = (_, _, _) => throw new AssertionException("NTFS roots must not be crawled."),
            OpenIndexAsync = (_, _, _, _) => Task.FromResult(session),
            ReadListAsync = _ => Task.FromResult<string[]?>([]),
            WriteListAsync = (_, _) => Task.CompletedTask,
            CandidateExists = RepositoryCandidateProbe.Exists,
            CreateLinuxSource = () => throw new PlatformNotSupportedException()
        });

        var config = new GitWizardConfiguration { SearchPaths = { files.TreePath } };
        var request = new DiscoveryRequest(config, DiscoveryPurpose.Watch, false, false, []);
        using var cts = new CancellationTokenSource(HangGuard);

        var startupTask = coordinator.PrepareWatchAsync(request, null, cts.Token);
        await scripted.Started.WaitAsync(cts.Token);

        // A modification under the already-tracked repository arrives during catch-up.
        scripted.Publish(new JournalBatch('X',
        [
            Entry(50, 6, "notes.txt", UsnReason.FileCreate)
        ], PackedIndexFixture.JournalId, 1002));
        scripted.Publish(new DriveCaughtUp('X'));

        await using var startup = await startupTask;

        Assert.Multiple(() =>
        {
            Assert.That(startup.StartupBatches, Is.Not.Empty,
                "Catch-up batches must survive membership reconciliation for the watch service.");
            Assert.That(startup.StartupBatches.SelectMany(batch => batch.Entries)
                .Any(entry => entry.FullPath.EndsWith("notes.txt", StringComparison.OrdinalIgnoreCase)), Is.True,
                "The buffered modification must reach the service, not vanish with the buffer.");
        });
    }

    [Test]
    public async Task StartupBuffering_ReplayCandidate_PolicyFiltersHiddenDirectory()
    {
        using var files = new PackedIndexFixture();
        var plainRepo = Path.Combine(files.TreePath, "newrepo");
        var hiddenRepo = Path.Combine(files.TreePath, ".hidden", "repo");
        var scripted = new ScriptedIndexWatchSource { AutoCatchUp = false };
        var index = await files.OpenMftIndexAsync(scripted, 'X');
        var connection = new CountingOwner();
        var session = new MftIndexSession(index, connection, ["X"], [new IndexedDrive('X', files.TreePath, 1)], new Dictionary<string, string>());
        var treePath = files.TreePath;

        // Both repositories exist on disk only after the index block was packed: the plain
        // one must enter the inventory through replay, the hidden one must not.
        Directory.CreateDirectory(Path.Combine(plainRepo, ".git"));
        Directory.CreateDirectory(Path.Combine(hiddenRepo, ".git"));

        var coordinator = new RepositoryDiscoveryCoordinator(new RepositoryDiscoveryDependencies
        {
            IsWindows = true,
            IsLinux = false,
            ClassifyRoot = _ => new RootClassification(treePath, RootFileSystem.Ntfs, "X", null),
            CrawlRootAsync = (_, _, _) => throw new AssertionException("NTFS roots must not be crawled."),
            OpenIndexAsync = (_, _, _, _) => Task.FromResult(session),
            ReadListAsync = _ => Task.FromResult<string[]?>([]),
            WriteListAsync = (_, _) => Task.CompletedTask,
            CandidateExists = RepositoryCandidateProbe.Exists,
            CreateLinuxSource = () => throw new PlatformNotSupportedException()
        });

        var config = new GitWizardConfiguration { SearchPaths = { files.TreePath } };
        var request = new DiscoveryRequest(config, DiscoveryPurpose.Watch, false, false, []);
        using var cts = new CancellationTokenSource(HangGuard);

        var startupTask = coordinator.PrepareWatchAsync(request, null, cts.Token);
        await scripted.Started.WaitAsync(cts.Token);

        scripted.Publish(new JournalBatch('X',
        [
            Entry(50, 5, "newrepo", UsnReason.FileCreate, isDirectory: true),
            Entry(51, 50, ".git", UsnReason.FileCreate, isDirectory: true),
            Entry(60, 5, ".hidden", UsnReason.FileCreate, isDirectory: true),
            Entry(61, 60, "repo", UsnReason.FileCreate, isDirectory: true),
            Entry(62, 61, ".git", UsnReason.FileCreate, isDirectory: true)
        ], PackedIndexFixture.JournalId, 1002));
        scripted.Publish(new DriveCaughtUp('X'));

        await using var startup = await startupTask;

        Assert.Multiple(() =>
        {
            Assert.That(startup.Inventory.Paths, Does.Contain(plainRepo).IgnoreCase,
                "A filesystem-valid replay candidate under a search root joins the inventory.");
            Assert.That(startup.Inventory.Paths.Any(path =>
                    path.Contains(".hidden", StringComparison.OrdinalIgnoreCase)), Is.False,
                "A replay candidate under a hidden directory must respect SkipHiddenDirectories.");
        });
    }

    static UsnJournalEntry Entry(ulong recordNumber, ulong parentNumber, string name, UsnReason reason, bool isDirectory = false) =>
        UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = recordNumber,
            ParentRecordNumber = parentNumber,
            SequenceNumber = 1,
            FileName = name,
            Reason = reason,
            FileAttributes = isDirectory ? FileAttributes.Directory : FileAttributes.Normal,
            Timestamp = PackedIndexFixture.CompletionTimestamp,
            Usn = 1001
        });
}

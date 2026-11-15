using GitWizard;
using GitWizard.Watch;

namespace GitWizardTests.Discovery;

// Covers the startup-buffer reconcile pass: target deduplication, cancellation and progress.
public class ReconcileBatchesTests
{
    // Reconcile derives a target's directory with Path.GetDirectoryName, so the paths use the
    // platform's own separator to exercise the deduplication on every platform.
    static readonly string RepositoriesRoot = OperatingSystem.IsWindows() ? @"C:\repos" : "/repos";
    static readonly string Directory = Path.Combine(RepositoriesRoot, "a", "bin");

    sealed class ProbeRecorder
    {
        public Dictionary<(string Path, RepositoryCandidateKind Kind), int> Calls { get; } = [];
        public int Total { get; private set; }
        public Func<string, RepositoryCandidateKind, bool> Exists { get; init; } = (_, _) => false;
        public Action? OnProbe { get; init; }

        public bool Probe(string path, RepositoryCandidateKind kind)
        {
            Total++;
            Calls[(path, kind)] = Calls.GetValueOrDefault((path, kind)) + 1;
            OnProbe?.Invoke();
            return Exists(path, kind);
        }

        public int CallsFor(string path) => Calls.Where(pair => pair.Key.Path == path).Sum(pair => pair.Value);
    }

    static (RepositoryDiscoveryCoordinator Coordinator, DiscoveryAccumulator Accumulator) Create(
        ProbeRecorder recorder, int progressInterval = 50_000)
    {
        var coordinator = new RepositoryDiscoveryCoordinator(new RepositoryDiscoveryDependencies
        {
            IsWindows = true,
            IsLinux = false,
            CrawlRootAsync = (_, _, _) => throw new AssertionException("Reconcile must not crawl."),
            ReadListAsync = _ => Task.FromResult<string[]?>([]),
            WriteListAsync = (_, _) => Task.CompletedTask,
            CandidateExists = recorder.Probe,
            CreateLinuxSource = () => throw new PlatformNotSupportedException(),
            ReconcileProgressInterval = progressInterval
        });
        var request = new DiscoveryRequest(new GitWizardConfiguration(), DiscoveryPurpose.Watch, false, false, []);
        var scope = DiscoveryScope.CreateExpanded(request, [RepositoriesRoot], [], []);
        return (coordinator, new DiscoveryAccumulator(scope, new HashSet<string>(WatchPath.StringComparer)));
    }

    static VolumeChangeBatch RepositoryMarker(int index) =>
        Batch(Path.Combine(RepositoriesRoot, $"repository{index}", ".git"), VolumeEntryKind.Created);

    static VolumeChangeBatch Batch(string path, VolumeEntryKind kind = VolumeEntryKind.Modified) =>
        new("C", [new VolumeChangeEntry(path, kind)]);

    [Test]
    public void RepeatedPath_IsProbedOnce()
    {
        var recorder = new ProbeRecorder();
        var (coordinator, accumulator) = Create(recorder);
        accumulator.Paths.Add(Directory);
        var batches = Enumerable.Repeat(Batch(Path.Combine(Directory, "file.obj")), 1000).ToList();

        coordinator.ReconcileBatches(batches, null, accumulator, null, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(recorder.Calls.Values, Is.All.EqualTo(1));
            Assert.That(recorder.CallsFor(Directory), Is.EqualTo(3));
            Assert.That(recorder.Total, Is.EqualTo(3));
        }
    }

    [Test]
    public void DistinctFilesInOneKnownRoot_ProbeTheRootOnce()
    {
        var recorder = new ProbeRecorder();
        var (coordinator, accumulator) = Create(recorder);
        accumulator.Paths.Add(Directory);
        var batches = Enumerable.Range(0, 500).Select(index => Batch(Path.Combine(Directory, $"file{index}.obj"))).ToList();

        coordinator.ReconcileBatches(batches, null, accumulator, null, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(recorder.CallsFor(Directory), Is.EqualTo(3));
            Assert.That(recorder.Total, Is.EqualTo(3));
        }
    }

    [Test]
    public void DistinctFilesOutsideAnyRepository_ProbeNothing()
    {
        var recorder = new ProbeRecorder();
        var (coordinator, accumulator) = Create(recorder);
        var batches = Enumerable.Range(0, 10_000).Select(index => Batch(Path.Combine(Directory, $"file{index}.obj"))).ToList();

        coordinator.ReconcileBatches(batches, null, accumulator, null, CancellationToken.None);

        Assert.That(recorder.Total, Is.Zero, "An ordinary file can never be a repository root, so it costs no filesystem call.");
    }

    [Test]
    public void ProbeCalls_AreBoundedByRepositoryRelevantPaths()
    {
        var recorder = new ProbeRecorder();
        var (coordinator, accumulator) = Create(recorder);
        var knownRoot = Path.Combine(RepositoriesRoot, "known");
        accumulator.Paths.Add(knownRoot);
        var batches = new List<VolumeChangeBatch>();
        batches.AddRange(Enumerable.Range(0, 5_000).Select(index => Batch(Path.Combine(knownRoot, "src", $"file{index}.cs"))));
        batches.AddRange(Enumerable.Range(0, 5_000).Select(index => Batch(Path.Combine(Directory, $"file{index}.obj"))));
        batches.AddRange(Enumerable.Range(0, 7).Select(RepositoryMarker));
        batches.AddRange(Enumerable.Range(0, 2).Select(index => Batch(Path.Combine(RepositoriesRoot, $"bare{index}.git", "HEAD"))));

        coordinator.ReconcileBatches(batches, null, accumulator, null, CancellationToken.None);

        // Three kinds per repository-relevant path: the known root, seven .git parents, two bare directories.
        Assert.That(recorder.Total, Is.EqualTo(3 * (1 + 7 + 2)));
    }

    [Test]
    public void KnownRootDeleted_WithOnlyADeepChildEntry_IsRemoved()
    {
        var known = Path.Combine(RepositoriesRoot, "known");
        var recorder = new ProbeRecorder();
        var (coordinator, accumulator) = Create(recorder);
        accumulator.Paths.Add(known);
        var batches = new List<VolumeChangeBatch>
        {
            Batch(Path.Combine(known, "src", "deep", "file.cs"), VolumeEntryKind.Deleted)
        };

        coordinator.ReconcileBatches(batches, null, accumulator, null, CancellationToken.None);

        Assert.That(accumulator.Paths, Does.Not.Contain(known));
    }

    [Test]
    public void KnownRootThatStillExists_WithOnlyADeepChildEntry_IsKept()
    {
        var known = Path.Combine(RepositoriesRoot, "known");
        var recorder = new ProbeRecorder
        {
            Exists = (path, kind) => path == known && kind == RepositoryCandidateKind.Ordinary
        };
        var (coordinator, accumulator) = Create(recorder);
        accumulator.Paths.Add(known);

        coordinator.ReconcileBatches([Batch(Path.Combine(known, "src", "file.cs"))], null, accumulator, null, CancellationToken.None);

        Assert.That(accumulator.Paths, Does.Contain(known));
    }

    [Test]
    public void NestedKnownRoots_ProbeOnlyTheNearestRoot()
    {
        var outer = Path.Combine(RepositoriesRoot, "outer");
        var inner = Path.Combine(outer, "vendor", "inner");
        var recorder = new ProbeRecorder();
        var (coordinator, accumulator) = Create(recorder);
        accumulator.Paths.Add(outer);
        accumulator.Paths.Add(inner);

        coordinator.ReconcileBatches([Batch(Path.Combine(inner, "src", "file.cs"))], null, accumulator, null, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(recorder.CallsFor(inner), Is.EqualTo(3));
            Assert.That(recorder.CallsFor(outer), Is.Zero);
        }
    }

    [Test]
    public void KnownRootLookup_FoldsCaseLikeTheWatchPaths()
    {
        var recorder = new ProbeRecorder();
        var (coordinator, accumulator) = Create(recorder);
        accumulator.Paths.Add(Path.Combine(RepositoriesRoot, "Known"));

        coordinator.ReconcileBatches(
            [Batch(Path.Combine(RepositoriesRoot, OperatingSystem.IsWindows() ? "KNOWN" : "Known", "file.cs"))],
            null, accumulator, null, CancellationToken.None);

        Assert.That(recorder.Total, Is.EqualTo(3));
    }

    [Test]
    public void UnknownDirectory_WithOnlyAWorkingTreeFileEntry_IsNotProbed()
    {
        // The cached index and the buffered journal together carry every repository's .git entry, so a
        // directory that is neither a known root nor the parent of a .git entry cannot be a new root.
        var unknown = Path.Combine(RepositoriesRoot, "unknown");
        var recorder = new ProbeRecorder { Exists = (path, _) => path == unknown };
        var (coordinator, accumulator) = Create(recorder);

        coordinator.ReconcileBatches([Batch(Path.Combine(unknown, "file.cs"))], null, accumulator, null, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(recorder.Total, Is.Zero);
            Assert.That(accumulator.Paths, Is.Empty);
        }
    }

    [TestCase("bare", "HEAD")]
    [TestCase("bare", "objects")]
    [TestCase("bare", "refs")]
    [TestCase("created.git", "config")]
    [TestCase("created.git", "description")]
    public void BareRepositoryCreatedInTheStartupWindow_IsAdded(string directoryName, string leaf)
    {
        var bare = Path.Combine(RepositoriesRoot, directoryName);
        var recorder = new ProbeRecorder
        {
            Exists = (path, kind) => path == bare && kind == RepositoryCandidateKind.Bare
        };
        var (coordinator, accumulator) = Create(recorder);

        coordinator.ReconcileBatches(
            [Batch(Path.Combine(bare, leaf), VolumeEntryKind.Created)], null, accumulator, null, CancellationToken.None);

        Assert.That(accumulator.Paths, Does.Contain(bare));
    }

    [Test]
    public void BareRepositoryDeletedInTheStartupWindow_IsRemoved()
    {
        var bare = Path.Combine(RepositoriesRoot, "gone.git");
        var recorder = new ProbeRecorder();
        var (coordinator, accumulator) = Create(recorder);
        accumulator.Paths.Add(bare);

        coordinator.ReconcileBatches(
            [Batch(Path.Combine(bare, "HEAD"), VolumeEntryKind.Deleted)], null, accumulator, null, CancellationToken.None);

        Assert.That(accumulator.Paths, Does.Not.Contain(bare));
    }

    [Test]
    public void EntriesInsideAGitDirectory_ProbeNothing()
    {
        var recorder = new ProbeRecorder();
        var (coordinator, accumulator) = Create(recorder);
        var gitDirectory = Path.Combine(RepositoriesRoot, "repository", ".git");

        coordinator.ReconcileBatches(
            [Batch(Path.Combine(gitDirectory, "index")), Batch(Path.Combine(gitDirectory, "refs")),
                Batch(Path.Combine(gitDirectory, "refs", "HEAD"))],
            null, accumulator, null, CancellationToken.None);

        Assert.That(recorder.Total, Is.Zero);
    }

    [Test]
    public void LastStateWins_ExistingRepositoryIsAdded_VanishedOneIsRemoved()
    {
        var present = Path.Combine(RepositoriesRoot, "present");
        var vanished = Path.Combine(RepositoriesRoot, "vanished");
        var recorder = new ProbeRecorder
        {
            Exists = (path, kind) => path == present && kind == RepositoryCandidateKind.Ordinary
        };
        var (coordinator, accumulator) = Create(recorder);
        accumulator.Paths.Add(vanished);
        var batches = new List<VolumeChangeBatch>
        {
            Batch(Path.Combine(present, ".git"), VolumeEntryKind.Created),
            Batch(Path.Combine(vanished, ".git"), VolumeEntryKind.Deleted),
            Batch(Path.Combine(present, ".git"))
        };

        coordinator.ReconcileBatches(batches, null, accumulator, null, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(accumulator.Paths, Does.Contain(present));
            Assert.That(accumulator.Paths, Does.Not.Contain(vanished));
        }
    }

    [TestCase(0)]
    [TestCase(-1)]
    public void ProgressInterval_BelowOne_IsRejected(int interval)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(new ProbeRecorder(), progressInterval: interval));
    }

    [Test]
    public void Cancellation_StopsTheReconcileLoop()
    {
        using var cancellation = new CancellationTokenSource();
        var recorder = new ProbeRecorder { OnProbe = cancellation.Cancel };
        var (coordinator, accumulator) = Create(recorder);
        var batches = Enumerable.Range(0, 1000).Select(RepositoryMarker).ToList();

        Assert.Throws<OperationCanceledException>(() =>
            coordinator.ReconcileBatches(batches, null, accumulator, null, cancellation.Token));

        Assert.That(recorder.Total, Is.LessThan(10), "A cancelled reconcile must stop probing instead of draining the buffer.");
    }

    [Test]
    public void CancelledBeforeStart_ProbesNothing()
    {
        var recorder = new ProbeRecorder();
        var (coordinator, accumulator) = Create(recorder);
        var batches = new List<VolumeChangeBatch> { RepositoryMarker(0) };

        Assert.Throws<OperationCanceledException>(() =>
            coordinator.ReconcileBatches(batches, null, accumulator, null, new CancellationToken(true)));

        Assert.That(recorder.Total, Is.Zero);
    }

    [Test]
    public void Progress_IsReportedAtTheInterval_WithCompletedAndTotalTargets()
    {
        var recorder = new ProbeRecorder();
        var (coordinator, accumulator) = Create(recorder, progressInterval: 2);
        var batches = Enumerable.Range(0, 4).Select(RepositoryMarker).ToList();
        var reports = new List<WatchStartupProgress>();

        coordinator.ReconcileBatches(batches, null, accumulator, new SynchronousProgress<WatchStartupProgress>(reports.Add),
            CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reports.Select(report => report.Phase), Is.All.EqualTo(WatchStartupPhase.Reconciling));
            Assert.That(reports.Select(report => (report.Completed, report.Total)),
                Is.EqualTo(new (int?, int?)[] { (2, 4), (4, 4) }));
        }
    }
}

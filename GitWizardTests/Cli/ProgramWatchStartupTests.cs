using GitWizard;
using GitWizard.CLI;
using GitWizard.Watch;
using GitWizardTests.TestSupport;
using LibGit2Sharp;
using MFTLib.Index;
using MFTLibTestExtensions;

namespace GitWizardTests;

[TestFixture]
[NonParallelizable]
public class ProgramWatchStartupTests
{
    LocalFilesSandbox? _sandbox;
    string _root = null!;

    [SetUp]
    public void SetUp()
    {
        VolumeChangeSourceFactory._isElevatedOverride = () => true;
        GitWizardLog.SilentMode = true;
        _sandbox = new LocalFilesSandbox();
        _root = Path.Combine(_sandbox.Path, "search");
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            _sandbox?.Dispose();
            _sandbox = null;
        }
        finally
        {
            VolumeChangeSourceFactory.ResetToDefaults();
        }
    }

    static RepositoryDiscoveryCoordinator CreateCoordinator(PackedIndexFixture files)
    {
        var dependencies = new RepositoryDiscoveryDependencies
        {
            IsWindows = true,
            IsLinux = false,
            ClassifyRoot = root => new RootClassification(root, RootFileSystem.Ntfs, "C", null),
            CrawlRootAsync = (_, _, _) => throw new AssertionException("NTFS roots must not be crawled"),
            OpenIndexAsync = async (drives, _, _, _) =>
            {
                var index = await files.OpenIndexAsync(drives.Select(d => d[0]).ToArray()).ConfigureAwait(false);
                var indexed = drives.Select(d => new IndexedDrive(d[0], files.TreePath, 1)).ToList();
                return new MftIndexSession(index, new CountingOwner(), drives, indexed, new Dictionary<string, string>());
            },
            ReadListAsync = GitWizardApi.GetCachedRepositoryPathsAsync,
            WriteListAsync = GitWizardApi.SaveCachedRepositoryPathsAsync,
            CandidateExists = (path, _) => Directory.Exists(path),
            CreateLinuxSource = () => throw new PlatformNotSupportedException()
        };
        return new RepositoryDiscoveryCoordinator(dependencies);
    }

    // Discover before arming so repositories created since the last cache write are tracked.
    static async Task<List<string>> LoadTrackedRepositoryPathsAsync(
        GitWizardConfiguration configuration,
        RepositoryDiscoveryCoordinator coordinator)
    {
        var retainedRoots = await GitWizardApi.GetCachedRepositoryPathsAsync() ?? [];
        var request = new DiscoveryRequest(configuration, DiscoveryPurpose.Watch, false, false, retainedRoots);

        await using var startup = await coordinator.PrepareWatchAsync(request, null, CancellationToken.None);
        return startup.Inventory.Paths.ToList();
    }

    [Test]
    public async Task LoadTrackedRepositoryPathsAsync_ColdPath_DiscoversRepositoryAndWritesList()
    {
        using var files = new PackedIndexFixture();
        var freshMarker = files.AddMarker("fresh");
        var fresh = GitWizardApi.NormalizePath(Path.GetDirectoryName(freshMarker)!);
        var config = new GitWizardConfiguration { SearchPaths = { files.TreePath }, IncludeTempDirectories = true };
        var coordinator = CreateCoordinator(files);

        var paths = await LoadTrackedRepositoryPathsAsync(config, coordinator);
        var cached = await GitWizardApi.GetCachedRepositoryPathsAsync();

        Assert.Multiple(() =>
        {
            Assert.That(paths, Does.Contain(fresh).IgnoreCase);
            Assert.That(cached, Does.Contain(fresh).IgnoreCase);
        });
    }

    [Test]
    public async Task LoadTrackedRepositoryPathsAsync_WarmReplay_DiscoversRepositoryCreatedAfterCacheWrite()
    {
        using var files = new PackedIndexFixture();
        var existingDir = Path.Combine(files.TreePath, "existing");
        Directory.CreateDirectory(Path.Combine(existingDir, ".git"));
        var existing = GitWizardApi.NormalizePath(existingDir);
        await GitWizardApi.SaveCachedRepositoryPathsAsync([existing]);

        var freshMarker = files.AddMarker("fresh");
        var fresh = GitWizardApi.NormalizePath(Path.GetDirectoryName(freshMarker)!);

        var scripted = new ScriptedWatchSource { CatchUpOnStart = true };
        var index = await files.OpenMftIndexAsync(scripted, 'C');
        var session = new MftIndexSession(index, new CountingOwner(), ["C"], [new IndexedDrive('C', files.TreePath, 1)], new Dictionary<string, string>());

        var coordinator = new RepositoryDiscoveryCoordinator(new RepositoryDiscoveryDependencies
        {
            IsWindows = true,
            IsLinux = false,
            ClassifyRoot = root => new RootClassification(root, RootFileSystem.Ntfs, "C", null),
            CrawlRootAsync = (_, _, _) => throw new AssertionException("NTFS roots must not be crawled"),
            OpenIndexAsync = (_, _, _, _) => Task.FromResult(session),
            ReadListAsync = GitWizardApi.GetCachedRepositoryPathsAsync,
            WriteListAsync = GitWizardApi.SaveCachedRepositoryPathsAsync,
            CandidateExists = RepositoryCandidateProbe.Exists,
            CreateLinuxSource = () => throw new PlatformNotSupportedException()
        });

        var config = new GitWizardConfiguration { SearchPaths = { files.TreePath }, IncludeTempDirectories = true };
        var paths = await LoadTrackedRepositoryPathsAsync(config, coordinator);
        var cached = await GitWizardApi.GetCachedRepositoryPathsAsync();

        Assert.Multiple(() =>
        {
            Assert.That(paths, Is.EquivalentTo(new[] { existing, fresh }).IgnoreCase);
            Assert.That(cached, Is.EquivalentTo(new[] { existing, fresh }).IgnoreCase);
        });
    }

    [Test]
    public async Task LoadTrackedRepositoryPathsAsync_LinuxMarksFirst_DiscoversTempRepo()
    {
        var repoPath = Path.Combine(_root, "linux-repo");
        Repository.Init(repoPath);
        var normalized = GitWizardApi.NormalizePath(repoPath);

        var source = new DiagnosticVolumeChangeSource();
        var marksInstalled = false;

        var coordinator = new RepositoryDiscoveryCoordinator(new RepositoryDiscoveryDependencies
        {
            IsWindows = false,
            IsLinux = true,
            ClassifyRoot = root => new RootClassification(root, RootFileSystem.Recursive, null, null),
            CrawlRootAsync = (_, _, _) =>
            {
                Assert.That(marksInstalled, Is.True, "Marks must be installed before recursive crawling.");
                return Task.FromResult(new RecursiveDiscovery([normalized], [], new Dictionary<string, string>()));
            },
            OpenIndexAsync = (_, _, _, _) => throw new PlatformNotSupportedException(),
            ReadListAsync = GitWizardApi.GetCachedRepositoryPathsAsync,
            WriteListAsync = GitWizardApi.SaveCachedRepositoryPathsAsync,
            CandidateExists = RepositoryCandidateProbe.Exists,
            CreateLinuxSource = () =>
            {
                marksInstalled = true;
                return source;
            }
        });

        var config = new GitWizardConfiguration { SearchPaths = { _root } };
        var paths = await LoadTrackedRepositoryPathsAsync(config, coordinator);
        var cached = await GitWizardApi.GetCachedRepositoryPathsAsync();

        Assert.Multiple(() =>
        {
            Assert.That(paths, Is.EquivalentTo(new[] { normalized }));
            Assert.That(cached, Is.EquivalentTo(new[] { normalized }));
        });
    }

    [Test]
    public async Task PrepareWatchAsync_Refusal_NoSecondSourceCreation()
    {
        using var files = new PackedIndexFixture();
        var openCount = 0;
        var coordinator = new RepositoryDiscoveryCoordinator(new RepositoryDiscoveryDependencies
        {
            IsWindows = true,
            IsLinux = false,
            ClassifyRoot = root => new RootClassification(root, RootFileSystem.Ntfs, "C", null),
            CrawlRootAsync = (_, _, _) => throw new AssertionException("NTFS roots must not be crawled"),
            OpenIndexAsync = (_, _, _, _) =>
            {
                Interlocked.Increment(ref openCount);
                throw new InvalidOperationException("MFT access denied");
            },
            ReadListAsync = GitWizardApi.GetCachedRepositoryPathsAsync,
            WriteListAsync = GitWizardApi.SaveCachedRepositoryPathsAsync,
            CandidateExists = RepositoryCandidateProbe.Exists,
            CreateLinuxSource = () => throw new PlatformNotSupportedException()
        });

        var request = new DiscoveryRequest(new GitWizardConfiguration { SearchPaths = { files.TreePath } }, DiscoveryPurpose.Watch, false, false, []);
        await using var startup = await coordinator.PrepareWatchAsync(request, null, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(openCount, Is.EqualTo(1));
            Assert.That(startup.Source, Is.Null);
            Assert.That(startup.Inventory.Outcome, Is.EqualTo(DiscoveryOutcome.Unusable));
        });
    }

    [Test]
    public async Task PrepareWatchAsync_PartialHealthyDrive_ArmsHealthyDriveOnly()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("c-repo");
        var dRoot = Directory.CreateDirectory(Path.Combine(files.DirectoryPath, "d-repos")).FullName;

        var scripted = new ScriptedWatchSource { CatchUpOnStart = true };
        var session = await StatusSessions.OpenAsync(files, scripted,
            ('C', SyntheticDriveBehavior.Ready), ('D', SyntheticDriveBehavior.ProducerFails));

        var coordinator = new RepositoryDiscoveryCoordinator(new RepositoryDiscoveryDependencies
        {
            IsWindows = true,
            IsLinux = false,
            ClassifyRoot = root => string.Equals(root, dRoot, StringComparison.OrdinalIgnoreCase)
                ? new RootClassification(root, RootFileSystem.Ntfs, "D", null)
                : new RootClassification(root, RootFileSystem.Ntfs, "C", null),
            CrawlRootAsync = (_, _, _) => throw new AssertionException("NTFS roots must not be crawled"),
            OpenIndexAsync = (_, _, _, _) => Task.FromResult(session),
            ReadListAsync = GitWizardApi.GetCachedRepositoryPathsAsync,
            WriteListAsync = GitWizardApi.SaveCachedRepositoryPathsAsync,
            CandidateExists = RepositoryCandidateProbe.Exists,
            CreateLinuxSource = () => throw new PlatformNotSupportedException()
        });

        var request = new DiscoveryRequest(new GitWizardConfiguration { SearchPaths = { files.TreePath, dRoot } }, DiscoveryPurpose.Watch, false, false, []);
        await using var startup = await coordinator.PrepareWatchAsync(request, null, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(startup.Inventory.Outcome, Is.EqualTo(DiscoveryOutcome.Partial));
            Assert.That(startup.Source, Is.Not.Null);
            Assert.That(startup.WatchVolumes, Is.EquivalentTo(new[] { "C" }));
        });
    }

    [Test]
    public async Task RunWatchAsync_EmptySuccess_DoesNotConsumeWatchEvents()
    {
        using var files = new PackedIndexFixture();
        var coordinator = CreateCoordinator(files);
        var config = new GitWizardConfiguration { SearchPaths = { files.TreePath } };
        var runConfig = CliParser.Parse(["--watch"]);

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exitCode = await Program.RunWatchAsync(runConfig, config, coordinator, stdout, stderr, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(stdout.ToString(), Does.Contain("No tracked repositories found; nothing to watch."));
        });
    }

    [Test]
    public void GetUnsupportedPlatformMessage_UnsupportedPlatform_ReturnsExactMessage()
    {
        var message = Program.GetUnsupportedPlatformMessage(isWindows: false, isLinux: false);

        Assert.That(message, Is.EqualTo("--watch requires Windows or Linux."));
    }

    [Test]
    public void GetUnsupportedPlatformMessage_WindowsPlatform_ReturnsNull()
    {
        var message = Program.GetUnsupportedPlatformMessage(isWindows: true, isLinux: false);

        Assert.That(message, Is.Null);
    }

    [Test]
    public void GetUnsupportedPlatformMessage_LinuxPlatform_ReturnsNull()
    {
        var message = Program.GetUnsupportedPlatformMessage(isWindows: false, isLinux: true);

        Assert.That(message, Is.Null);
    }

    [Test]
    public async Task RunWatchAsync_CachedRepositoryOutsideSearchRoots_StaysInDriveSetAndList()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("search-repo");
        var retainedRoot = Path.Combine(files.DirectoryPath, "elsewhere", "cached-repo");
        Directory.CreateDirectory(Path.Combine(retainedRoot, ".git"));
        var normalizedRetained = GitWizardApi.NormalizePath(retainedRoot);
        await GitWizardApi.SaveCachedRepositoryPathsAsync([normalizedRetained]);

        var treePath = files.TreePath;
        IReadOnlyList<string>? requestedDrives = null;
        var coordinator = new RepositoryDiscoveryCoordinator(new RepositoryDiscoveryDependencies
        {
            IsWindows = true,
            IsLinux = false,
            ClassifyRoot = root => root.StartsWith(treePath, StringComparison.OrdinalIgnoreCase)
                ? new RootClassification(root, RootFileSystem.Ntfs, "C", null)
                : new RootClassification(root, RootFileSystem.Ntfs, "E", null),
            CrawlRootAsync = (_, _, _) => throw new AssertionException("NTFS roots must not be crawled"),
            OpenIndexAsync = (drives, _, _, _) =>
            {
                requestedDrives = drives;
                throw new InvalidOperationException("MFT access denied");
            },
            ReadListAsync = GitWizardApi.GetCachedRepositoryPathsAsync,
            WriteListAsync = GitWizardApi.SaveCachedRepositoryPathsAsync,
            CandidateExists = RepositoryCandidateProbe.Exists,
            CreateLinuxSource = () => throw new PlatformNotSupportedException()
        });

        var config = new GitWizardConfiguration { SearchPaths = { files.TreePath }, IncludeTempDirectories = true };
        var runConfig = CliParser.Parse(["--watch"]);

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exitCode = await Program.RunWatchAsync(runConfig, config, coordinator, stdout, stderr, CancellationToken.None);
        var cached = await GitWizardApi.GetCachedRepositoryPathsAsync();

        Assert.Multiple(() =>
        {
            Assert.That(requestedDrives, Is.EquivalentTo(new[] { "C", "E" }),
                "The retained root's drive must join the selected set, not just the search roots' drives.");
            Assert.That(exitCode, Is.EqualTo(1), "An unusable discovery exits nonzero.");
            Assert.That(cached, Is.Not.Null, "An unusable discovery preserves the previous repository list.");
            Assert.That(cached, Does.Contain(normalizedRetained).IgnoreCase);
        });
    }

    [Test]
    public async Task LoadTrackedRepositoryPathsAsync_EmptyDiscoveryClearsStaleCache()
    {
        using var files = new PackedIndexFixture();
        var missing = GitWizardApi.NormalizePath(Path.Combine(files.TreePath, "missing"));
        await GitWizardApi.SaveCachedRepositoryPathsAsync([missing]);

        var coordinator = CreateCoordinator(files);
        var config = new GitWizardConfiguration { SearchPaths = { files.TreePath } };
        var paths = await LoadTrackedRepositoryPathsAsync(config, coordinator);
        var cached = await GitWizardApi.GetCachedRepositoryPathsAsync();

        Assert.Multiple(() =>
        {
            Assert.That(paths, Is.Empty);
            Assert.That(cached, Is.Null);
        });
    }
}

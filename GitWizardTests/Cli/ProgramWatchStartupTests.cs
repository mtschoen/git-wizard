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
            CrawlRootAsync = (_, _, _) => throw new AssertionException("NTFS roots must not be crawled"),
            OpenIndexAsync = (roots, _, _, _) => files.OpenDirectorySessionAsync(roots),
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
        return [.. startup.Inventory.Paths];
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(paths, Does.Contain(fresh).IgnoreCase);
            Assert.That(cached, Does.Contain(fresh).IgnoreCase);
        }
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
        var session = await files.OpenDirectorySessionAsync([files.TreePath], scripted);

        var coordinator = new RepositoryDiscoveryCoordinator(new RepositoryDiscoveryDependencies
        {
            IsWindows = true,
            IsLinux = false,
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(paths, Is.EquivalentTo([existing, fresh]).IgnoreCase);
            Assert.That(cached, Is.EquivalentTo([existing, fresh]).IgnoreCase);
        }
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(paths, Is.EquivalentTo([normalized]));
            Assert.That(cached, Is.EquivalentTo([normalized]));
        }
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(openCount, Is.EqualTo(1));
            Assert.That(startup.Source, Is.Null);
            Assert.That(startup.Inventory.Outcome, Is.EqualTo(DiscoveryOutcome.Unusable));
        }
    }

    [Test]
    public async Task PrepareWatchAsync_PartialHealthyDrive_ArmsHealthyDriveOnly()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("c-repo");
        var dRoot = Directory.CreateDirectory(Path.Combine(files.DirectoryPath, "d-repos")).FullName;

        var scripted = new ScriptedWatchSource { CatchUpOnStart = true };
        var session = await StatusSessions.OpenRequestedAsync(files, [files.TreePath, dRoot],
            [('C', SyntheticDriveBehavior.Ready), ('D', SyntheticDriveBehavior.ProducerFails)], scripted);

        var coordinator = new RepositoryDiscoveryCoordinator(new RepositoryDiscoveryDependencies
        {
            IsWindows = true,
            IsLinux = false,
            CrawlRootAsync = (_, _, _) => throw new AssertionException("NTFS roots must not be crawled"),
            OpenIndexAsync = (_, _, _, _) => Task.FromResult(session),
            ReadListAsync = GitWizardApi.GetCachedRepositoryPathsAsync,
            WriteListAsync = GitWizardApi.SaveCachedRepositoryPathsAsync,
            CandidateExists = RepositoryCandidateProbe.Exists,
            CreateLinuxSource = () => throw new PlatformNotSupportedException()
        });

        var request = new DiscoveryRequest(new GitWizardConfiguration { SearchPaths = { files.TreePath, dRoot } }, DiscoveryPurpose.Watch, false, false, []);
        await using var startup = await coordinator.PrepareWatchAsync(request, null, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(startup.Inventory.Outcome, Is.EqualTo(DiscoveryOutcome.Partial));
            Assert.That(startup.Source, Is.Not.Null);
            Assert.That(startup.WatchVolumes, Is.EquivalentTo(["C"]));
        }
    }

    [Test]
    public async Task RunWatchAsync_EmptySuccess_DoesNotConsumeWatchEvents()
    {
        using var files = new PackedIndexFixture();
        var coordinator = CreateCoordinator(files);
        var config = new GitWizardConfiguration { SearchPaths = { files.TreePath } };
        var runConfig = CliParser.Parse(["--watch"]);

        await using var stdout = new StringWriter();
        await using var stderr = new StringWriter();
        var exitCode = await Program.RunWatchAsync(runConfig, config, coordinator, stdout, stderr, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exitCode, Is.Zero);
            Assert.That(stdout.ToString(), Does.Contain("No tracked repositories found; nothing to watch."));
        }
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

        await using var stdout = new StringWriter();
        await using var stderr = new StringWriter();
        var exitCode = await Program.RunWatchAsync(runConfig, config, coordinator, stdout, stderr, CancellationToken.None);
        var cached = await GitWizardApi.GetCachedRepositoryPathsAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(requestedDrives, Is.EquivalentTo([treePath, retainedRoot]).IgnoreCase,
                "The retained directory must join the configured search roots.");
            Assert.That(exitCode, Is.EqualTo(1), "An unusable discovery exits nonzero.");
            Assert.That(cached, Is.Not.Null, "An unusable discovery preserves the previous repository list.");
            Assert.That(cached, Does.Contain(normalizedRetained).IgnoreCase);
        }
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(paths, Is.Empty);
            Assert.That(cached, Is.Null);
        }
    }
}

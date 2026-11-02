using System.Collections.Concurrent;
using GitWizard;
using GitWizardTests.TestSupport;
using LibGit2Sharp;
using MFTLib.Index;
using MFTLibTestExtensions;

namespace GitWizardTests.Discovery;

/// <summary>
/// Repository sizes come from the MFT index in one pass instead of a directory walk per repository. They must
/// equal the walk's total, and any repository the index cannot size completely must be left to the walk.
/// </summary>
[TestFixture]
[Platform("Win")]
public class RepositorySizeCalculatorTests
{
    const uint RootRow = 5;
    const string UnsizedFileName = "unsized.bin";

    PackedIndexFixture _files = null!;

    [SetUp]
    public void SetUp() => _files = new PackedIndexFixture();

    [TearDown]
    public void TearDown() => _files.Dispose();

    void WriteFile(string relativePath, int length)
    {
        var path = Path.Combine(_files.TreePath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[length]);
    }

    string CreateRepositoryDirectory(string relativePath)
    {
        var path = Path.Combine(_files.TreePath, relativePath);
        Directory.CreateDirectory(Path.Combine(path, ".git", "objects"));
        WriteFile(Path.Combine(relativePath, ".git", "objects", "pack"), 700);
        return path;
    }

    // One row per directory and file of the fixture tree, sized from disk like a full MFT scan.
    IEnumerable<SyntheticRow> FullTreeRows()
    {
        yield return new SyntheticRow(RootRow, "root", RootRow)
        {
            IsDirectory = true,
            Attributes = FileAttributes.Directory,
            LastWriteTime = PackedIndexFixture.CompletionTimestamp,
            SequenceNumber = 1
        };
        var rows = new List<SyntheticRow>();
        var nextRow = RootRow + 1;
        AddDirectory(_files.TreePath, RootRow, ref nextRow, rows);
        foreach (var row in rows)
        {
            yield return row;
        }
    }

    static void AddDirectory(string path, uint parent, ref uint nextRow, List<SyntheticRow> rows)
    {
        foreach (var directory in Directory.EnumerateDirectories(path).Order(StringComparer.Ordinal))
        {
            var row = nextRow++;
            rows.Add(new SyntheticRow(row, Path.GetFileName(directory), parent)
            {
                IsDirectory = true,
                Attributes = FileAttributes.Directory,
                LastWriteTime = PackedIndexFixture.CompletionTimestamp,
                SequenceNumber = 1
            });
            AddDirectory(directory, row, ref nextRow, rows);
        }

        foreach (var file in Directory.EnumerateFiles(path).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            rows.Add(new SyntheticRow(nextRow++, name, parent)
            {
                Attributes = FileAttributes.Normal,
                Size = name == UnsizedFileName ? null : new FileInfo(file).Length,
                LastWriteTime = PackedIndexFixture.CompletionTimestamp,
                SequenceNumber = 1
            });
        }
    }

    GitWizardConfiguration Configuration() =>
        new() { SearchPaths = { _files.TreePath }, IncludeTempDirectories = true };

    RepositoryDiscoveryCoordinator CreateCoordinator(
        Func<FileIndexSet, IReadOnlyCollection<string>, CancellationToken, IReadOnlyDictionary<string, long>>? computeSizes = null)
    {
        var producer = new SyntheticMftProducer(_ => FullTreeRows())
        {
            JournalCursor = new SyntheticJournalCursor(PackedIndexFixture.JournalIdentifier, PackedIndexFixture.NextUsn),
            ScanTimestamp = PackedIndexFixture.CompletionTimestamp
        };
        var source = SyntheticIndexSource.Create(producer);
        var configureOptions = _files.ConfigureOptions;
        var dependencies = RepositoryDiscoveryDependencies.CreateDefault(options =>
            configureOptions(options) with { CacheMode = CacheMode.Disabled, MftSource = source });
        if (computeSizes is not null)
        {
            dependencies.ComputeRepositorySizes = computeSizes;
        }

        return new RepositoryDiscoveryCoordinator(dependencies);
    }

    async Task<(DiscoveryInventory Inventory, IReadOnlyDictionary<string, long> Sizes)> DiscoverAsync(bool walkOnly = false)
    {
        await using var result = await CreateCoordinator().DiscoverAsync(
            new DiscoveryRequest(Configuration(), DiscoveryPurpose.Rebuild, false, walkOnly, []), null, CancellationToken.None);
        return (result.Inventory, await result.RepositorySizes);
    }

    string InitializeRepository()
    {
        var path = Path.Combine(_files.TreePath, "repo");
        using var repository = new Repository(Repository.Init(path));
        var signature = new Signature("test", "test@example.com", DateTimeOffset.UnixEpoch);
        repository.Commit("initial", signature, signature, new CommitOptions { AllowEmptyCommit = true });
        return path;
    }

    [Test]
    public async Task NestedAndSiblingRepositories_SizeLikeTheDirectoryWalk()
    {
        var outer = CreateRepositoryDirectory("outer");
        var inner = CreateRepositoryDirectory(Path.Combine("outer", "vendor", "inner"));
        var solo = CreateRepositoryDirectory("solo");
        WriteFile(Path.Combine("outer", "src", "a.txt"), 10);
        WriteFile(Path.Combine("outer", "vendor", "inner", "b.txt"), 20);
        WriteFile(Path.Combine("solo", "c.txt"), 30);
        WriteFile(Path.Combine("elsewhere", "d.txt"), 40);

        var (_, sizes) = await DiscoverAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sizes.Keys, Is.EquivalentTo([outer, inner, solo]));
            Assert.That(sizes[outer], Is.EqualTo(GitWizardRepository.ComputeDirectorySize(outer)));
            Assert.That(sizes[inner], Is.EqualTo(GitWizardRepository.ComputeDirectorySize(inner)));
            Assert.That(sizes[solo], Is.EqualTo(GitWizardRepository.ComputeDirectorySize(solo)));
            Assert.That(sizes[outer], Is.EqualTo(700 + 700 + 10 + 20));
        }
    }

    [Test, NonParallelizable]
    public async Task UnknownSize_IsCompletedFromTheFileSystemInsteadOfWalkingTheRepositories()
    {
        var outer = CreateRepositoryDirectory("outer");
        var inner = CreateRepositoryDirectory(Path.Combine("outer", "vendor", "inner"));
        var sibling = CreateRepositoryDirectory("sibling");
        WriteFile(Path.Combine("outer", "vendor", "inner", UnsizedFileName), 50);

        var (inventory, sizes, log) = await DiscoverWithLogAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(inventory.Paths, Is.EquivalentTo([outer, inner, sibling]));
            Assert.That(sizes.Keys, Is.EquivalentTo([outer, inner, sibling]));
            Assert.That(sizes[outer], Is.EqualTo(GitWizardRepository.ComputeDirectorySize(outer)));
            Assert.That(sizes[inner], Is.EqualTo(GitWizardRepository.ComputeDirectorySize(inner)));
            Assert.That(sizes[inner], Is.EqualTo(700 + 50));
            Assert.That(log, Does.Contain("3 of 3 from the index (2 completed by reading 2 unknown-size file(s) from disk); 0 walked."));
        }
    }

    [Test, NonParallelizable]
    public async Task UnknownSize_AboveTheLimit_WalksTheRepositoriesAroundItAndCountsThem()
    {
        var outer = CreateRepositoryDirectory("outer");
        var sibling = CreateRepositoryDirectory("sibling");
        WriteFile(Path.Combine("outer", "a", UnsizedFileName), 50);
        WriteFile(Path.Combine("outer", "b", UnsizedFileName), 60);
        var previousLimit = RepositorySizeCalculator.UnknownSizeFileLimit;
        RepositorySizeCalculator.UnknownSizeFileLimit = 1;
        try
        {
            var (inventory, sizes, log) = await DiscoverWithLogAsync();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(inventory.Paths, Is.EquivalentTo([outer, sibling]));
                Assert.That(sizes.Keys, Is.EqualTo([sibling]));
                Assert.That(log, Does.Contain(
                    "1 of 2 from the index; 1 walked (1 more unknown-size files than the 1 read limit (2 file(s)))."));
            }
        }
        finally
        {
            RepositorySizeCalculator.UnknownSizeFileLimit = previousLimit;
        }
    }

    [Test, NonParallelizable]
    public void ProductionReader_WhenFileIsMissing_ReturnsZero()
    {
        var path = Path.Combine(_files.TreePath, "missing.bin");

        Assert.That(RepositorySizeCalculator.ReadFileLength(path), Is.EqualTo(0));
    }

    [Test, NonParallelizable]
    public void ProductionReader_WhenMetadataReadFails_ReturnsNull()
    {
        var path = Path.Combine(_files.TreePath, new string('x', 300));
        using (Assert.EnterMultipleScope())
        {
            var exception = Assert.Catch<Exception>(() => _ = new FileInfo(path).Length);
            Assert.That(exception, Is.InstanceOf<IOException>());
            Assert.That(exception, Is.Not.InstanceOf<FileNotFoundException>());
            Assert.That(exception, Is.Not.InstanceOf<DirectoryNotFoundException>());
            Assert.That(RepositorySizeCalculator.ReadFileLength(path), Is.Null);
        }
    }

    [Test, NonParallelizable]
    public async Task UnknownSize_WhenTheFileCannotBeRead_WalksTheRepositoryAndCountsIt()
    {
        var outer = CreateRepositoryDirectory("outer");
        var sibling = CreateRepositoryDirectory("sibling");
        WriteFile(Path.Combine("outer", UnsizedFileName), 50);
        var previousReader = RepositorySizeCalculator.ReadFileLength;
        RepositorySizeCalculator.ReadFileLength = _ => null;
        try
        {
            var (inventory, sizes, log) = await DiscoverWithLogAsync();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(inventory.Paths, Is.EquivalentTo([outer, sibling]));
                Assert.That(sizes.Keys, Is.EqualTo([sibling]));
                Assert.That(log, Does.Contain("1 of 2 from the index; 1 walked (1 an unknown-size file could not be read)."));
            }
        }
        finally
        {
            RepositorySizeCalculator.ReadFileLength = previousReader;
        }
    }

    [Test, NonParallelizable]
    public async Task SizeSummary_CountsPathsTheCompleteIndexCannotLocate()
    {
        var repository = CreateRepositoryDirectory("repo");
        var missing = Path.Combine(_files.TreePath, "never-indexed");
        var uncovered = Path.Combine(Path.GetPathRoot(_files.TreePath)!, "outside-every-index", "repo");

        var (_, sizes, log) = await DiscoverWithLogAsync((index, paths, token) =>
            RepositorySizeCalculator.Compute(index, [.. paths, missing, uncovered], token));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sizes.Keys, Is.EqualTo([repository]));
            Assert.That(log, Does.Contain("1 of 3 from the index; 2 walked (1 not found in a complete index; 1 no index covers their root)."));
        }
    }

    [Test, NonParallelizable]
    public async Task SizeSummary_NamesTheFieldThatMakesAnIndexIncomplete()
    {
        CreateRepositoryDirectory("repo");

        var (_, sizes, log) = await DiscoverWithLogAsync(walkOnly: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sizes, Is.Empty);
            Assert.That(log, Does.Contain("0 of 1 from the index; 1 walked (1 on ").And.Contain("(index not a complete snapshot: Producer=Enumeration)"));
        }
    }

    [Test, NonParallelizable]
    public async Task Refresh_LogsOneSummaryOfWhereSizesCameFrom()
    {
        var path = InitializeRepository();
        var configuration = Configuration();
        await using var report = new GitWizardReport(configuration);
        await using var discovery = await report.DiscoverRepositoryPathsAsync(
            new DiscoveryRequest(configuration, DiscoveryPurpose.Rebuild, false, false, []),
            CreateCoordinator(), null, CancellationToken.None);

        var log = await CaptureLogAsync(() =>
        {
            report.Refresh([path]);
            return Task.CompletedTask;
        });

        Assert.That(log, Does.Contain("Refresh sizes: 1 from the discovery index; 0 walked (0 had no index size, 0 refreshed without"));
    }

    [TestCase(false), TestCase(true), NonParallelizable]
    public async Task Refresh_WhenValidationFails_DoesNotRecordSizeSources(bool fetchRemotes)
    {
        var path = InitializeRepository();
        var configuration = Configuration();
        await using var report = new GitWizardReport(configuration);
        await using var discovery = await report.DiscoverRepositoryPathsAsync(
            new DiscoveryRequest(configuration, DiscoveryPurpose.Rebuild, false, false, []),
            CreateCoordinator(), null, CancellationToken.None);
        Directory.Move(Path.Combine(path, ".git"), Path.Combine(path, "not-git"));

        var log = await CaptureLogAsync(() =>
        {
            report.Refresh([path], fetchRemotes: fetchRemotes);
            return Task.CompletedTask;
        });

        Assert.That(log, Does.Not.Contain("Refresh sizes:"));
    }

    [Test, NonParallelizable]
    public async Task Refresh_AfterAFetch_LogsTheRepositoryAsRefreshedWithoutAnOfferedSize()
    {
        var path = InitializeRepository();
        var configuration = Configuration();
        await using var report = new GitWizardReport(configuration);
        await using var discovery = await report.DiscoverRepositoryPathsAsync(
            new DiscoveryRequest(configuration, DiscoveryPurpose.Rebuild, false, false, []),
            CreateCoordinator(), null, CancellationToken.None);

        var log = await CaptureLogAsync(() =>
        {
            report.Refresh([path], fetchRemotes: true);
            return Task.CompletedTask;
        });

        Assert.That(log, Does.Contain("Refresh sizes: 0 from the discovery index; 1 walked (0 had no index size, 1 refreshed without"));
    }

    static async Task<string> CaptureLogAsync(Func<Task> action)
    {
        var messages = new ConcurrentQueue<string>();
        var previousMethod = GitWizardLog.LogMethod;
        var previousSilent = GitWizardLog.SilentMode;
        // Logging also writes a file under the local-files home; keep it in an owned home and close it afterwards.
        using var sandbox = new LocalFilesSandbox();
        try
        {
            GitWizardLog.LogMethod = message => messages.Enqueue(message ?? string.Empty);
            GitWizardLog.SilentMode = false;
            await action();
        }
        finally
        {
            GitWizardLog.LogMethod = previousMethod;
            GitWizardLog.SilentMode = previousSilent;
            GitWizardLog.CloseCurrentLogFile();
        }

        return string.Join(Environment.NewLine, messages);
    }

    async Task<(DiscoveryInventory Inventory, IReadOnlyDictionary<string, long> Sizes, string Log)> DiscoverWithLogAsync(
        Func<FileIndexSet, IReadOnlyCollection<string>, CancellationToken, IReadOnlyDictionary<string, long>>? computeSizes = null,
        bool walkOnly = false)
    {
        DiscoveryInventory? inventory = null;
        IReadOnlyDictionary<string, long>? sizes = null;
        var log = await CaptureLogAsync(async () =>
        {
            await using var result = await CreateCoordinator(computeSizes).DiscoverAsync(
                new DiscoveryRequest(Configuration(), DiscoveryPurpose.Rebuild, false, walkOnly, []), null, CancellationToken.None);
            inventory = result.Inventory;
            sizes = await result.RepositorySizes;
        });
        return (inventory!, sizes!, log);
    }

    [Test]
    public async Task WalkProducedIndex_IsNeverUsedForSizes()
    {
        CreateRepositoryDirectory("repo");

        var (inventory, sizes) = await DiscoverAsync(walkOnly: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(inventory.Paths, Has.Count.EqualTo(1));
            Assert.That(sizes, Is.Empty);
        }
    }

    [Test]
    public async Task Refresh_UsesTheIndexedSizeOnceAndThenWalksAgain()
    {
        var path = InitializeRepository();
        var configuration = Configuration();
        await using var report = new GitWizardReport(configuration);
        await using var discovery = await report.DiscoverRepositoryPathsAsync(
            new DiscoveryRequest(configuration, DiscoveryPurpose.Rebuild, false, false, []),
            CreateCoordinator(), null, CancellationToken.None);
        var indexedSize = (await discovery.RepositorySizes)[path];
        WriteFile(Path.Combine("repo", "written-after-discovery.bin"), 5000);

        report.Refresh([path]);
        var afterDiscovery = report.Repositories[path].SizeOnDisk;
        report.Refresh([path]);
        var afterSecondRefresh = report.Repositories[path].SizeOnDisk;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(afterDiscovery, Is.EqualTo(indexedSize));
            Assert.That(afterSecondRefresh, Is.EqualTo(indexedSize + 5000));
            Assert.That(afterSecondRefresh, Is.EqualTo(GitWizardRepository.ComputeDirectorySize(path)));
        }
    }

    [Test]
    public async Task Refresh_AfterAFetch_WalksInsteadOfUsingTheIndexedSize()
    {
        var path = InitializeRepository();
        var configuration = Configuration();
        await using var report = new GitWizardReport(configuration);
        await using var discovery = await report.DiscoverRepositoryPathsAsync(
            new DiscoveryRequest(configuration, DiscoveryPurpose.Rebuild, false, false, []),
            CreateCoordinator(), null, CancellationToken.None);
        WriteFile(Path.Combine("repo", "written-after-discovery.bin"), 5000);

        report.Refresh([path], fetchRemotes: true);

        Assert.That(report.Repositories[path].SizeOnDisk, Is.EqualTo(GitWizardRepository.ComputeDirectorySize(path)));
    }

    [Test]
    public async Task GenerateReport_CreatesRepositoryRowsBeforePendingSizeCompletes()
    {
        var path = InitializeRepository();
        var configuration = Configuration();
        var sizeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSize = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var repositoryCreated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IReadOnlyDictionary<string, long>? batchSizes = null;
        var handler = new RecordingUpdateHandler
        {
            CreatingRepository = _ => repositoryCreated.TrySetResult()
        };
        var reportTask = GitWizardReport.GenerateReportAsync(configuration, null, handler,
            new GitWizardReportOptions
            {
                DiscoveryCoordinator = CreateCoordinator((index, paths, cancellationToken) =>
                {
                    batchSizes = RepositorySizeCalculator.Compute(index, paths, cancellationToken);
                    sizeStarted.TrySetResult();
                    releaseSize.Task.GetAwaiter().GetResult();
                    return batchSizes;
                }),
                RetainDiscoveryIndex = true,
                DiscoveryPurpose = DiscoveryPurpose.Rebuild
            });
        GitWizardReport report;
        try
        {
            await sizeStarted.Task.WaitAsync(TestTimeouts.HangGuard);
            await repositoryCreated.Task.WaitAsync(TestTimeouts.HangGuard);
            Assert.That(reportTask.IsCompleted, Is.False);
            WriteFile(Path.Combine("repo", "written-after-discovery.bin"), 5000);
        }
        finally
        {
            releaseSize.TrySetResult();
            report = await reportTask.WaitAsync(TestTimeouts.HangGuard);
        }

        await using (report)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(batchSizes, Is.Not.Null);
                Assert.That(report.Repositories[path].SizeOnDisk, Is.EqualTo(batchSizes![path]));
            }
        }
    }

    [Test]
    public async Task GenerateReport_WhenSizePassFails_WalksTheRepository()
    {
        var path = InitializeRepository();
        var configuration = Configuration();
        GitWizardReport report = null!;
        var log = await CaptureLogAsync(() =>
        {
            Assert.DoesNotThrowAsync(async () => report = await GitWizardReport.GenerateReportAsync(configuration, null, null,
                new GitWizardReportOptions
                {
                    DiscoveryCoordinator = CreateCoordinator((_, _, _) => throw new InvalidDataException("Size pass failed.")),
                    RetainDiscoveryIndex = true,
                    DiscoveryPurpose = DiscoveryPurpose.Rebuild
                }));
            return Task.CompletedTask;
        });

        await using (report)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(report.Repositories[path].SizeOnDisk, Is.EqualTo(GitWizardRepository.ComputeDirectorySize(path)));
                Assert.That(report.Repositories[path].RefreshError, Is.Null);
                Assert.That(log, Does.Contain("Refresh sizes: 0 from the discovery index; 1 walked (1 had no index size, 0 refreshed without"));
            }
        }
    }
}

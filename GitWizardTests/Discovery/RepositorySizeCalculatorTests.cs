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
public class RepositorySizeCalculatorTests
{
    const uint RootRow = 5;
    const string UnsizedFileName = "unsized.bin";

    PackedIndexFixture _files = null!;

    [SetUp]
    public void SetUp()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Ignore("The MFT route is the Windows discovery route.");
        }

        _files = new PackedIndexFixture();
    }

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

    RepositoryDiscoveryCoordinator CreateCoordinator()
    {
        var producer = new SyntheticMftProducer(_ => FullTreeRows())
        {
            JournalCursor = new SyntheticJournalCursor(PackedIndexFixture.JournalIdentifier, PackedIndexFixture.NextUsn),
            ScanTimestamp = PackedIndexFixture.CompletionTimestamp
        };
        var source = SyntheticIndexSource.Create(producer);
        var configureOptions = _files.ConfigureOptions;
        return new RepositoryDiscoveryCoordinator(RepositoryDiscoveryDependencies.CreateDefault(options =>
            configureOptions(options) with { CacheMode = CacheMode.Disabled, MftSource = source }));
    }

    async Task<DiscoveryInventory> DiscoverAsync(bool walkOnly = false)
    {
        await using var result = await CreateCoordinator().DiscoverAsync(
            new DiscoveryRequest(Configuration(), DiscoveryPurpose.Rebuild, false, walkOnly, []), null, CancellationToken.None);
        return result.Inventory;
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

        var inventory = await DiscoverAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(inventory.RepositorySizes.Keys, Is.EquivalentTo([outer, inner, solo]));
            Assert.That(inventory.RepositorySizes[outer], Is.EqualTo(GitWizardRepository.ComputeDirectorySize(outer)));
            Assert.That(inventory.RepositorySizes[inner], Is.EqualTo(GitWizardRepository.ComputeDirectorySize(inner)));
            Assert.That(inventory.RepositorySizes[solo], Is.EqualTo(GitWizardRepository.ComputeDirectorySize(solo)));
            Assert.That(inventory.RepositorySizes[outer], Is.EqualTo(700 + 700 + 10 + 20));
        }
    }

    [Test]
    public async Task UnknownSize_LeavesOnlyTheRepositoriesAroundItToTheWalk()
    {
        var outer = CreateRepositoryDirectory("outer");
        var inner = CreateRepositoryDirectory(Path.Combine("outer", "vendor", "inner"));
        var sibling = CreateRepositoryDirectory("sibling");
        WriteFile(Path.Combine("outer", "vendor", "inner", UnsizedFileName), 50);

        var inventory = await DiscoverAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(inventory.Paths, Is.EquivalentTo([outer, inner, sibling]));
            Assert.That(inventory.RepositorySizes.Keys, Is.EqualTo([sibling]));
        }
    }

    [Test]
    public async Task WalkProducedIndex_IsNeverUsedForSizes()
    {
        CreateRepositoryDirectory("repo");

        var inventory = await DiscoverAsync(walkOnly: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(inventory.Paths, Has.Count.EqualTo(1));
            Assert.That(inventory.RepositorySizes, Is.Empty);
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
        var indexedSize = report.DiscoveryInventory!.RepositorySizes[path];
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
}

using GitWizard;
using GitWizardTests.TestSupport;
using MFTLib.Index;
using MFTLibTestExtensions;

namespace GitWizardTests;

[TestFixture]
public class IndexDiscoveryTests
{
    [Test]
    public async Task MarkerDirectoryParent()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repository");
        await using var index = await files.OpenIndexAsync();
        var markers = GitWizardApi.SelectGitEntries(index, includeGitFiles: false);
        Assert.That(markers, Has.Count.EqualTo(1));
        Assert.That(markers[0].Name, Is.EqualTo(".git"));
        Assert.That(markers[0].IsDirectory, Is.True);
        Assert.That(markers[0].Parent, Is.EqualTo(index.Search(new SearchQuery("repository", NameMatchMode.Exact)).Single()));
        Assert.That(markers[0].Parent!.Value.IsDirectory, Is.True);
    }

    [Test]
    public async Task ScopedMarkerFileParent()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("worktree", isDirectory: false);
        await using var index = await files.OpenIndexAsync();
        var markers = GitWizardApi.SelectGitEntries(index, includeGitFiles: true);
        Assert.That(markers, Has.Count.EqualTo(1));
        Assert.That(markers[0].Name, Is.EqualTo(".git"));
        Assert.That(markers[0].IsDirectory, Is.False);
        Assert.That(markers[0].Parent, Is.EqualTo(index.Search(new SearchQuery("worktree", NameMatchMode.Exact)).Single()));
    }

    [Test]
    public async Task DriveRootExcludesMarkerFiles()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repository");
        files.AddMarker("worktree", isDirectory: false);
        await using var index = await files.OpenIndexAsync();
        Assert.That(index.Search(new SearchQuery(".git", NameMatchMode.Exact)), Has.Count.EqualTo(2));
        var markers = GitWizardApi.SelectGitEntries(index, includeGitFiles: false);
        Assert.That(markers, Has.Count.EqualTo(1));
        Assert.That(markers[0].IsDirectory, Is.True);
        Assert.That(markers[0].Parent!.Value.Name, Is.EqualTo("repository"));
    }

    [Test]
    public async Task CaseInsensitiveMarkerLookup()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repository", markerName: ".GIT");
        await using var index = await files.OpenIndexAsync();
        var markers = GitWizardApi.SelectGitEntries(index, includeGitFiles: false);
        Assert.That(markers, Has.Count.EqualTo(1));
        Assert.That(markers[0].Name, Is.EqualTo(".GIT"));
        Assert.That(markers[0].IsDirectory, Is.True);
        Assert.That(markers[0].Parent, Is.EqualTo(index.Search(new SearchQuery("repository", NameMatchMode.Exact)).Single()));
    }
    [Test]
    public async Task DirectoryAndMarkerFileSelection()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repository");
        files.AddMarker("worktree", false);
        await using var index = await files.OpenIndexAsync();
        Assert.That(GitWizardApi.SelectGitEntries(index, false).Single().Parent!.Value.Name,
            Is.EqualTo("repository"));
        Assert.That(GitWizardApi.SelectGitEntries(index, true).Select(entry => entry.Parent!.Value.Name),
            Is.EquivalentTo(new[] { "repository", "worktree" }));
    }

    [Test]
    public async Task IgnoresFreeAndDeletedRows()
    {
        using var files = new PackedIndexFixture();
        await using var index = await OpenRowsAsync(files,
            File(6, "freed", 5) with { IsFree = true },
            Directory(7, ".git", 5) with { IsTombstone = true },
            File(8, "ordinary", 5));
        Assert.That(index.Search(new SearchQuery("ordinary", NameMatchMode.Exact)).Single().IsDirectory, Is.False);
        Assert.That(index.Search(new SearchQuery("freed", NameMatchMode.Exact)), Is.Empty);
        Assert.That(index.Search(new SearchQuery(".git", NameMatchMode.Exact)), Is.Empty);
        Assert.That(GitWizardApi.SelectGitEntries(index, true), Is.Empty);
    }

    [Test]
    public async Task RootRowNeedNotBeZero()
    {
        using var files = new PackedIndexFixture();
        await using var index = await OpenRowsAsync(files,
            File(0, "$MFT", 5),
            Directory(7, "repository", 5),
            Directory(9, ".git", 7));
        var marker = index.Search(new SearchQuery(".git", NameMatchMode.Exact)).Single();
        Assert.That(marker.IsDirectory, Is.True);
        Assert.That(marker.Parent!.Value.Name, Is.EqualTo("repository"));
        var root = marker.Parent.Value.Parent!.Value;
        Assert.That(root.Name, Is.EqualTo("root"));
        Assert.That(root.Parent, Is.Null);
    }

    [Test]
    public async Task MalformedParentChainHonorsDocumentedDepthGuard()
    {
        using var files = new PackedIndexFixture();
        var depth = (uint)SyntheticBlock.MaximumPathDepth;
        var rows = new List<SyntheticRow>();
        for (uint row = 6; row < 6 + depth; row++)
        {
            rows.Add(Directory(row, "parent", row - 1));
        }

        rows.Add(Directory(6 + depth, ".git", 5 + depth));
        await using var index = await OpenRowsAsync(files, [.. rows]);
        var marker = index.Search(new SearchQuery(".git", NameMatchMode.Exact)).Single();
        Assert.That(marker.Parent!.Value.Name, Is.EqualTo("parent"));
        Assert.That(() => marker.Path, Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public async Task SelectRepositoryCandidates_ExcessivePathDepth_SkipsMalformedEntry()
    {
        using var files = new PackedIndexFixture();
        var depth = (uint)SyntheticBlock.MaximumPathDepth;
        var rows = new List<SyntheticRow> { Directory(6, "validrepo", 5), Directory(7, ".git", 6) };
        for (uint row = 8; row < 8 + depth; row++)
        {
            rows.Add(Directory(row, "parent", row - 1));
        }

        rows.Add(Directory(8 + depth, ".git", 7 + depth));
        await using var index = await OpenRowsAsync(files, [.. rows]);
        var entries = GitWizardApi.SelectGitEntries(index, includeGitFiles: true);
        Assert.That(entries, Has.Count.EqualTo(2));
        var candidates = CandidateSelection.Paths(index, @"X:\", Array.Empty<string>(),
            includeTempDirectories: true);
        Assert.That(candidates, Is.EqualTo(new[] { Path.Combine(files.TreePath, "validrepo") }));
    }

    static Task<FileIndex> OpenRowsAsync(PackedIndexFixture files, params SyntheticRow[] rows) =>
        PackedRepositoryRows.OpenRowsAsync(files, rows);

    static SyntheticRow Directory(uint row, string name, uint parent) =>
        PackedRepositoryRows.Directory(row, name, parent);

    static SyntheticRow File(uint row, string name, uint parent) => PackedRepositoryRows.File(row, name, parent);
}

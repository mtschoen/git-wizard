using GitWizard;
using GitWizardTests.TestSupport;
using MFTLib.Index;

namespace GitWizardTests;

public class GitFileIndexMappingTests
{
    [Test]
    public async Task SelectGitEntries_IncludeGitFilesTrue_KeepsBothKinds()
    {
        using var fixture = new PackedIndexFixture();
        fixture.AddMarker("repo");
        fixture.AddMarker("worktree", false);
        await using var index = await fixture.OpenIndexAsync();
        var entries = GitWizardApi.SelectGitEntries(new FileIndexSet([index]), true);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries.Select(entry => entry.Parent!.Value.Name), Is.EquivalentTo(["repo", "worktree"]));
            Assert.That(entries.Count(entry => entry.IsDirectory), Is.EqualTo(1));
        }
    }

    [Test]
    public async Task SelectGitEntries_IncludeGitFilesFalse_KeepsDirectories()
    {
        using var fixture = new PackedIndexFixture();
        fixture.AddMarker("repo");
        fixture.AddMarker("worktree", false);
        await using var index = await fixture.OpenIndexAsync();
        var entries = GitWizardApi.SelectGitEntries(new FileIndexSet([index]), false);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries.Single().Parent!.Value.Name, Is.EqualTo("repo"));
            Assert.That(entries.Single().IsDirectory, Is.True);
        }
    }

    [Test]
    public async Task SelectGitEntries_OnlyMatchesMarkerNamesCaseInsensitively()
    {
        using var fixture = new PackedIndexFixture();
        fixture.AddMarker("repo");
        fixture.AddMarker("upper", markerName: ".GIT");
        await File.WriteAllTextAsync(Path.Combine(fixture.TreePath, "gitconfig"), "content");
        await using var index = await fixture.OpenIndexAsync();
        Assert.That(GitWizardApi.SelectGitEntries(new FileIndexSet([index]), true).Select(entry => entry.Parent!.Value.Name),
            Is.EquivalentTo(["repo", "upper"]));
    }

    [Test]
    public async Task GitFileParentIsRepositoryRoot()
    {
        using var fixture = new PackedIndexFixture();
        fixture.AddMarker(Path.Combine("worktree"), false);
        await using var index = await fixture.OpenIndexAsync();
        var root = Path.Combine(fixture.TreePath, "worktree");
        var entries = GitWizardApi.SelectGitEntries(new FileIndexSet([index]), true);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries.Single().Name, Is.EqualTo(".git"));
            Assert.That(entries.Single().IsDirectory, Is.False);
        }
        var candidates = CandidateSelection.Paths(index, root, [], includeTempDirectories: true);
        Assert.That(candidates, Has.Length.EqualTo(1));
    }

    [Test]
    public async Task OutsideSearchRootIsExcluded()
    {
        using var fixture = new PackedIndexFixture();
        fixture.AddMarker(Path.Combine("worktree"), false);
        await using var index = await fixture.OpenIndexAsync();
        var root = Path.Combine(fixture.TreePath, "other");
        var entries = GitWizardApi.SelectGitEntries(new FileIndexSet([index]), true);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries.Single().Name, Is.EqualTo(".git"));
            Assert.That(entries.Single().IsDirectory, Is.False);
        }
        var candidates = CandidateSelection.Paths(index, root, [], includeTempDirectories: true);
        Assert.That(candidates, Is.Empty);
    }

    [Test]
    public async Task IgnoredRootIsExcluded()
    {
        using var fixture = new PackedIndexFixture();
        fixture.AddMarker(Path.Combine("scope", "ignored", "worktree"), false);
        await using var index = await fixture.OpenIndexAsync();
        var root = Path.Combine(fixture.TreePath, "scope");
        var entries = GitWizardApi.SelectGitEntries(new FileIndexSet([index]), true);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries.Single().Name, Is.EqualTo(".git"));
            Assert.That(entries.Single().IsDirectory, Is.False);
        }
        var candidates = CandidateSelection.Paths(index, root, [Path.Combine(root, "ignored")], includeTempDirectories: true);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(candidates, Is.Empty);
            Assert.That(CandidateSelection.Paths(index, root, [], includeTempDirectories: true), Is.Not.Empty);
            Assert.That(CandidateSelection.Paths(index, root, [root], includeTempDirectories: true), Is.Empty);
        }
    }

    [Test]
    public async Task HiddenSegmentExcludedWhenSkipHiddenDirectoriesTrue()
    {
        using var fixture = new PackedIndexFixture();
        fixture.AddMarker(Path.Combine("scope", ".hidden", "worktree"), false);
        await using var index = await fixture.OpenIndexAsync();
        var root = Path.Combine(fixture.TreePath, "scope");
        var entries = GitWizardApi.SelectGitEntries(new FileIndexSet([index]), true);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries.Single().Name, Is.EqualTo(".git"));
            Assert.That(entries.Single().IsDirectory, Is.False);
        }
        var candidates = CandidateSelection.Paths(index, root, [], skipHiddenDirectories: true, includeTempDirectories: true);
        Assert.That(candidates, Is.Empty);
    }

    [Test]
    public async Task HiddenSegmentIncludedByDefault()
    {
        using var fixture = new PackedIndexFixture();
        fixture.AddMarker(Path.Combine("scope", ".hidden", "worktree"), false);
        await using var index = await fixture.OpenIndexAsync();
        var root = Path.Combine(fixture.TreePath, "scope");
        var entries = GitWizardApi.SelectGitEntries(new FileIndexSet([index]), true);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries.Single().Name, Is.EqualTo(".git"));
            Assert.That(entries.Single().IsDirectory, Is.False);
        }
        var candidates = CandidateSelection.Paths(index, root, [], includeTempDirectories: true);
        Assert.That(candidates, Has.Length.EqualTo(1));
    }

    [Test]
    public async Task ScopedSearchIncludesGitFile()
    {
        using var fixture = new PackedIndexFixture();
        var marker = fixture.AddMarker("worktree", false);
        await using var index = await fixture.OpenIndexAsync();
        var root = Path.Combine(fixture.TreePath, "worktree");
        var entries = GitWizardApi.SelectGitEntries(new FileIndexSet([index]), true);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries.Single().Name, Is.EqualTo(".git"));
            Assert.That(entries.Single().IsDirectory, Is.False);
        }
        var candidates = CandidateSelection.Paths(index, root, [], includeTempDirectories: true);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(candidates, Has.Length.EqualTo(1));
            Assert.That(entries.Single().Parent!.Value.Name, Is.EqualTo("worktree"));
        }
        // The recursive collector receives an independent host path, never an index path.
        var repositories = new List<string>();
        EnumeratedDiscovery.GetRepositoryPaths(fixture.TreePath, repositories, []);
        Assert.That(repositories, Does.Contain(GitWizardApi.ResolveOnDiskPath(Path.GetDirectoryName(marker)!)));
    }

    [Test]
    public async Task DriveRootExcludesGitFile()
    {
        using var fixture = new PackedIndexFixture();
        fixture.AddMarker(Path.Combine("worktree"), false);
        await using var index = await fixture.OpenIndexAsync();
        var root = Path.GetPathRoot(fixture.TreePath)!;
        var entries = GitWizardApi.SelectGitEntries(new FileIndexSet([index]), true);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries.Single().Name, Is.EqualTo(".git"));
            Assert.That(entries.Single().IsDirectory, Is.False);
        }
        var candidates = CandidateSelection.Paths(index, root, [], includeTempDirectories: true);
        Assert.That(candidates, Is.Empty);
    }

    [Test]
    public async Task NestedWorktreeAndOuterRepositoryBothYieldParents()
    {
        using var fixture = new PackedIndexFixture();
        fixture.AddMarker("outer");
        fixture.AddMarker(Path.Combine("outer", "vendor", "worktree"), false);
        await using var index = await fixture.OpenIndexAsync();
        var entries = GitWizardApi.SelectGitEntries(new FileIndexSet([index]), true);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries.Select(entry => entry.Parent!.Value.Name), Is.EquivalentTo(["outer", "worktree"]));
            Assert.That(CandidateSelection.Paths(index, Path.Combine(fixture.TreePath, "outer"), [], includeTempDirectories: true), Has.Length.EqualTo(2));
        }
    }

    [Test]
    public async Task TempDirectoryMarkerExcludedByDefault()
    {
        // The PackedIndexFixture tree lives under the host temp directory, so every candidate
        // it yields is a temp path: excluded by default, restored by includeTempDirectories.
        using var fixture = new PackedIndexFixture();
        fixture.AddMarker(Path.Combine("Users", "gitea-runner", "AppData", "Local", "Temp", "pytest-1", "repo"));
        await using var index = await fixture.OpenIndexAsync();
        var root = Path.GetPathRoot(fixture.TreePath)!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(CandidateSelection.Paths(index, root, []), Is.Empty);
            Assert.That(
                CandidateSelection.Paths(index, root, [], includeTempDirectories: true),
                Is.Not.Empty);
        }
    }
}


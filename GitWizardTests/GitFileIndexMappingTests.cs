using GitWizard;
using GitWizardTests.TestSupport;

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
        var entries = GitWizardApi.SelectGitEntries(index, true);
        Assert.That(entries.Select(entry => entry.Parent!.Value.Name), Is.EquivalentTo(new[] { "repo", "worktree" }));
        Assert.That(entries.Count(entry => entry.IsDirectory), Is.EqualTo(1));
    }

    [Test]
    public async Task SelectGitEntries_IncludeGitFilesFalse_KeepsDirectories()
    {
        using var fixture = new PackedIndexFixture();
        fixture.AddMarker("repo");
        fixture.AddMarker("worktree", false);
        await using var index = await fixture.OpenIndexAsync();
        var entries = GitWizardApi.SelectGitEntries(index, false);
        Assert.That(entries.Single().Parent!.Value.Name, Is.EqualTo("repo"));
        Assert.That(entries.Single().IsDirectory, Is.True);
    }

    [Test]
    public async Task SelectGitEntries_OnlyMatchesMarkerNamesCaseInsensitively()
    {
        using var fixture = new PackedIndexFixture();
        fixture.AddMarker("repo");
        fixture.AddMarker("upper", markerName: ".GIT");
        await File.WriteAllTextAsync(Path.Combine(fixture.TreePath, "gitconfig"), "content");
        await using var index = await fixture.OpenIndexAsync();
        Assert.That(GitWizardApi.SelectGitEntries(index, true).Select(entry => entry.Parent!.Value.Name),
            Is.EquivalentTo(new[] { "repo", "upper" }));
    }

    [Test]
    public async Task GitFileParentIsRepositoryRoot()
    {
        using var fixture = new PackedIndexFixture();
        fixture.AddMarker(Path.Combine("worktree"), false);
        await using var index = await fixture.OpenIndexAsync();
        const string root = "X:/worktree";
        var entries = GitWizardApi.SelectGitEntries(index, true);
        Assert.That(entries.Single().Name, Is.EqualTo(".git"));
        Assert.That(entries.Single().IsDirectory, Is.False);
        var candidates = GitWizardApi.ResolveRepositoryRootCandidates(index, entries, root, []).ToArray();
        Assert.That(candidates, Has.Length.EqualTo(1));
    }

    [Test]
    public async Task OutsideSearchRootIsExcluded()
    {
        using var fixture = new PackedIndexFixture();
        fixture.AddMarker(Path.Combine("worktree"), false);
        await using var index = await fixture.OpenIndexAsync();
        const string root = "X:/other";
        var entries = GitWizardApi.SelectGitEntries(index, true);
        Assert.That(entries.Single().Name, Is.EqualTo(".git"));
        Assert.That(entries.Single().IsDirectory, Is.False);
        var candidates = GitWizardApi.ResolveRepositoryRootCandidates(index, entries, root, []).ToArray();
        Assert.That(candidates, Is.Empty);
    }

    [Test]
    public async Task IgnoredRootIsExcluded()
    {
        using var fixture = new PackedIndexFixture();
        fixture.AddMarker(Path.Combine("scope", "ignored", "worktree"), false);
        await using var index = await fixture.OpenIndexAsync();
        const string root = "X:/scope";
        var entries = GitWizardApi.SelectGitEntries(index, true);
        Assert.That(entries.Single().Name, Is.EqualTo(".git"));
        Assert.That(entries.Single().IsDirectory, Is.False);
        var candidates = GitWizardApi.ResolveRepositoryRootCandidates(index, entries, root, ["X:/scope/ignored"]).ToArray();
        Assert.That(candidates, Is.Empty);
        Assert.That(GitWizardApi.ResolveRepositoryRootCandidates(index, entries, root, []), Is.Not.Empty);
        Assert.That(GitWizardApi.ResolveRepositoryRootCandidates(index, entries, root, [root]), Is.Empty);
    }

    [Test]
    public async Task HiddenSegmentExcludedByDefault()
    {
        using var fixture = new PackedIndexFixture();
        fixture.AddMarker(Path.Combine("scope", ".hidden", "worktree"), false);
        await using var index = await fixture.OpenIndexAsync();
        const string root = "X:/scope";
        var entries = GitWizardApi.SelectGitEntries(index, true);
        Assert.That(entries.Single().Name, Is.EqualTo(".git"));
        Assert.That(entries.Single().IsDirectory, Is.False);
        var candidates = GitWizardApi.ResolveRepositoryRootCandidates(index, entries, root, []).ToArray();
        Assert.That(candidates, Is.Empty);
    }

    [Test]
    public async Task HiddenSegmentIncludedWhenConfigured()
    {
        using var fixture = new PackedIndexFixture();
        fixture.AddMarker(Path.Combine("scope", ".hidden", "worktree"), false);
        await using var index = await fixture.OpenIndexAsync();
        const string root = "X:/scope";
        var entries = GitWizardApi.SelectGitEntries(index, true);
        Assert.That(entries.Single().Name, Is.EqualTo(".git"));
        Assert.That(entries.Single().IsDirectory, Is.False);
        var candidates = GitWizardApi.ResolveRepositoryRootCandidates(index, entries, root, [], false).ToArray();
        Assert.That(candidates, Has.Length.EqualTo(1));
    }

    [Test]
    public async Task ScopedSearchIncludesGitFile()
    {
        using var fixture = new PackedIndexFixture();
        var marker = fixture.AddMarker("worktree", false);
        await using var index = await fixture.OpenIndexAsync();
        const string root = "X:/worktree";
        var entries = GitWizardApi.SelectGitEntries(index, true);
        Assert.That(entries.Single().Name, Is.EqualTo(".git"));
        Assert.That(entries.Single().IsDirectory, Is.False);
        var candidates = GitWizardApi.ResolveRepositoryRootCandidates(index, entries, root, []).ToArray();
        Assert.That(candidates, Has.Length.EqualTo(1));
        Assert.That(entries.Single().Parent!.Value.Name, Is.EqualTo("worktree"));
        // The recursive collector receives an independent host path, never an index path.
        var repositories = new List<string>();
        GitWizardApi.GetRepositoryPaths(fixture.TreePath, repositories, []);
        Assert.That(repositories, Does.Contain(GitWizardApi.NormalizePath(Path.GetDirectoryName(marker)!)));
    }

    [Test]
    public async Task DriveRootExcludesGitFile()
    {
        using var fixture = new PackedIndexFixture();
        fixture.AddMarker(Path.Combine("worktree"), false);
        await using var index = await fixture.OpenIndexAsync();
        const string root = "X:/";
        var entries = GitWizardApi.SelectGitEntries(index, true);
        Assert.That(entries.Single().Name, Is.EqualTo(".git"));
        Assert.That(entries.Single().IsDirectory, Is.False);
        var candidates = GitWizardApi.ResolveRepositoryRootCandidates(index, entries, root, []).ToArray();
        Assert.That(candidates, Is.Empty);
    }

    [Test]
    public async Task NestedWorktreeAndOuterRepositoryBothYieldParents()
    {
        using var fixture = new PackedIndexFixture();
        fixture.AddMarker("outer");
        fixture.AddMarker(Path.Combine("outer", "vendor", "worktree"), false);
        await using var index = await fixture.OpenIndexAsync();
        var entries = GitWizardApi.SelectGitEntries(index, true);
        Assert.That(entries.Select(entry => entry.Parent!.Value.Name), Is.EquivalentTo(new[] { "outer", "worktree" }));
        Assert.That(GitWizardApi.ResolveRepositoryRootCandidates(index, entries, "X:/outer", []).Count(), Is.EqualTo(2));
    }
}

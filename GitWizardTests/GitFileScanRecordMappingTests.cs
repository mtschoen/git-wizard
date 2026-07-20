using GitWizard;
using MFTLib;

namespace GitWizardTests;

/// <summary>
/// Covers the ScanRecord-to-repository-root mapping used by MFT discovery
/// (<see cref="GitWizardApi.CollectGitRepositoriesFromScan"/> and its pure building blocks
/// <see cref="GitWizardApi.SelectGitEntryPaths"/> / <see cref="GitWizardApi.ResolveRepositoryRootCandidates"/>):
/// a `.git` FILE record (a worktree or submodule pointer, whose real gitdir may live outside
/// every search path) must yield its parent directory as a repository root, the same way a
/// `.git` directory record does. These are pure/synthetic-record tests - no MFT/native
/// dependency - so they run on every OS, unlike <see cref="BrokerDiscoveryTests"/> which is
/// gated to Windows by the outer <see cref="GitWizardApi.TryFindAllRepositoriesUsingMftAsync"/>
/// entry point.
/// </summary>
public class GitFileScanRecordMappingTests
{
    static ScanRecord GitDirectoryRecord(string repoPath) =>
        new(0, 0, 0, 0, 0, IsDirectory: true, Name: ".git", Path: Path.Combine(repoPath, ".git"));

    static ScanRecord GitFileRecord(string worktreePath) =>
        new(0, 0, 0, 0, 0, IsDirectory: false, Name: ".git", Path: Path.Combine(worktreePath, ".git"));

    [Test]
    public void SelectGitEntryPaths_IncludeGitFilesTrue_KeepsBothFileAndDirectoryEntries()
    {
        var records = new[] { GitDirectoryRecord(@"C:\repos\repo-a"), GitFileRecord(@"C:\repos\worktree-a") };

        var selected = GitWizardApi.SelectGitEntryPaths(records, includeGitFiles: true).ToArray();

        Assert.That(selected, Is.EquivalentTo(new[]
        {
            Path.Combine(@"C:\repos\repo-a", ".git"),
            Path.Combine(@"C:\repos\worktree-a", ".git"),
        }));
    }

    [Test]
    public void SelectGitEntryPaths_IncludeGitFilesFalse_DropsFileEntriesKeepsDirectories()
    {
        var records = new[] { GitDirectoryRecord(@"C:\repos\repo-a"), GitFileRecord(@"C:\repos\worktree-a") };

        var selected = GitWizardApi.SelectGitEntryPaths(records, includeGitFiles: false).ToArray();

        Assert.That(selected, Is.EquivalentTo(new[] { Path.Combine(@"C:\repos\repo-a", ".git") }));
    }

    [Test]
    public void SelectGitEntryPaths_IgnoresRecordsNotNamedGit()
    {
        var records = new[]
        {
            GitDirectoryRecord(@"C:\repos\repo-a"),
            new ScanRecord(0, 0, 0, 0, 0, IsDirectory: false, Name: "gitconfig", Path: @"C:\repos\repo-a\gitconfig"),
        };

        var selected = GitWizardApi.SelectGitEntryPaths(records, includeGitFiles: true).ToArray();

        Assert.That(selected, Is.EquivalentTo(new[] { Path.Combine(@"C:\repos\repo-a", ".git") }));
    }

    [Test]
    public void ResolveRepositoryRootCandidates_GitFileEntry_YieldsParentAsRepositoryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "gw-map-" + Guid.NewGuid().ToString("N"));
        var worktreePath = Path.Combine(root, "worktree-a");
        var gitFileEntry = Path.Combine(worktreePath, ".git");

        var candidates = GitWizardApi.ResolveRepositoryRootCandidates([gitFileEntry], root, Array.Empty<string>())
            .ToArray();

        Assert.That(candidates, Has.Length.EqualTo(1));
        Assert.That(candidates[0], Does.EndWith("worktree-a").IgnoreCase);
    }

    [Test]
    public void ResolveRepositoryRootCandidates_GitFileEntryOutsideSearchRoot_IsExcluded()
    {
        var root = Path.Combine(Path.GetTempPath(), "gw-map-" + Guid.NewGuid().ToString("N"));
        var outsideWorktree = Path.Combine(Path.GetTempPath(), "gw-map-outside-" + Guid.NewGuid().ToString("N"));
        var gitFileEntry = Path.Combine(outsideWorktree, ".git");

        var candidates = GitWizardApi.ResolveRepositoryRootCandidates([gitFileEntry], root, Array.Empty<string>())
            .ToArray();

        Assert.That(candidates, Is.Empty);
    }

    [Test]
    public void ResolveRepositoryRootCandidates_GitFileEntryUnderIgnoredPath_IsExcluded()
    {
        var root = Path.Combine(Path.GetTempPath(), "gw-map-" + Guid.NewGuid().ToString("N"));
        var worktreePath = Path.Combine(root, "ignored", "worktree-a");
        var gitFileEntry = Path.Combine(worktreePath, ".git");
        var ignored = Path.Combine(root, "ignored");
        // ExpandIgnoredPaths (via ExpandSearchPath) only honors an ignored path that exists on
        // disk, so the ignored directory must be real for this test to exercise the filter.
        Directory.CreateDirectory(ignored);
        try
        {
            var candidates = GitWizardApi.ResolveRepositoryRootCandidates([gitFileEntry], root, [ignored]).ToArray();

            Assert.That(candidates, Is.Empty);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void ResolveRepositoryRootCandidates_GitFileEntryUnderHiddenSegment_IsExcludedByDefault()
    {
        var root = Path.Combine(Path.GetTempPath(), "gw-map-" + Guid.NewGuid().ToString("N"));
        var worktreePath = Path.Combine(root, ".hidden", "worktree-a");
        var gitFileEntry = Path.Combine(worktreePath, ".git");

        var candidates = GitWizardApi.ResolveRepositoryRootCandidates([gitFileEntry], root, Array.Empty<string>())
            .ToArray();

        Assert.That(candidates, Is.Empty);
    }

    [Test]
    public void ResolveRepositoryRootCandidates_GitFileEntryUnderHiddenSegment_KeptWhenSkipHiddenDirectoriesFalse()
    {
        var root = Path.Combine(Path.GetTempPath(), "gw-map-" + Guid.NewGuid().ToString("N"));
        var worktreePath = Path.Combine(root, ".hidden", "worktree-a");
        var gitFileEntry = Path.Combine(worktreePath, ".git");

        var candidates = GitWizardApi.ResolveRepositoryRootCandidates(
            [gitFileEntry], root, Array.Empty<string>(), skipHiddenDirectories: false).ToArray();

        Assert.That(candidates, Has.Length.EqualTo(1));
    }

    [Test]
    public void ResolveRepositoryRootCandidates_NestedWorktreeUnderAnotherRepo_BothParentsYielded()
    {
        // A worktree's .git FILE living inside another repository's own tree (e.g. a scratch
        // worktree checked out under the outer repo's working directory) must still surface
        // as its own distinct repository-root candidate alongside the outer repo's .git
        // directory - discovery doesn't collapse nested repos, it reports every valid parent.
        var root = Path.Combine(Path.GetTempPath(), "gw-map-" + Guid.NewGuid().ToString("N"));
        var outerRepo = root;
        var innerWorktree = Path.Combine(root, "vendor", "worktree-a");
        var outerGitDirectory = Path.Combine(outerRepo, ".git");
        var innerGitFile = Path.Combine(innerWorktree, ".git");

        var candidates = GitWizardApi
            .ResolveRepositoryRootCandidates([outerGitDirectory, innerGitFile], root, Array.Empty<string>())
            .ToArray();

        Assert.That(candidates, Has.Length.EqualTo(2));
        Assert.That(candidates, Has.Some.EndsWith("worktree-a").IgnoreCase);
        Assert.That(candidates, Has.Some.EndsWith(new DirectoryInfo(root).Name).IgnoreCase);
    }

    [Test]
    public void CollectGitRepositoriesFromScan_ScopedSearchPath_FindsWorktreeFromGitFileRecord()
    {
        var root = Path.Combine(Path.GetTempPath(), "gw-scan-" + Guid.NewGuid().ToString("N"));
        var worktreePath = Path.Combine(root, "worktree-a");
        Directory.CreateDirectory(worktreePath);
        File.WriteAllText(Path.Combine(worktreePath, ".git"), "gitdir: /elsewhere/.git/worktrees/worktree-a\n");
        try
        {
            var records = new[] { GitFileRecord(worktreePath) };
            var paths = new List<string>();

            GitWizardApi.CollectGitRepositoriesFromScan(records, root, Array.Empty<string>(), paths, skipHiddenDirectories: null);

            Assert.That(paths.Any(p => p.Contains("worktree-a", StringComparison.OrdinalIgnoreCase)), Is.True,
                "A .git FILE record's parent must be discovered as a repository root under a scoped search path.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void CollectGitRepositoriesFromScan_DriveRootSearchPath_IgnoresGitFileRecords()
    {
        // At a drive root, only .git directories count - .git files (worktrees/submodules)
        // are left for discovery during refresh of their parent repo, matching the in-process
        // MFT scan's FindDirectories-only rule at a drive root.
        var driveRoot = Path.GetPathRoot(Path.GetTempPath())!;
        var worktreePath = Path.Combine(Path.GetTempPath(), "gw-scan-driveroot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(worktreePath);
        try
        {
            var records = new[] { GitFileRecord(worktreePath) };
            var paths = new List<string>();

            GitWizardApi.CollectGitRepositoriesFromScan(records, driveRoot, Array.Empty<string>(), paths, skipHiddenDirectories: null);

            Assert.That(paths, Is.Empty);
        }
        finally
        {
            Directory.Delete(worktreePath, recursive: true);
        }
    }
}

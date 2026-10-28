using GitWizard;
using GitWizardTests.TestSupport;
using MFTLib.Index;

namespace GitWizardTests.Discovery;

[TestFixture]
public class BareIndexDiscoveryTests
{
    [Test]
    public async Task SelectRepositoryCandidates_FindsOrdinaryBareAndWorktree_ExcludesInternalGit()
    {
        using var files = new PackedIndexFixture();

        // 1. Ordinary repository
        var ordinaryPath = Path.Combine(files.TreePath, "ordinary");
        Directory.CreateDirectory(Path.Combine(ordinaryPath, ".git"));
        // Standard .git internal contents
        await File.WriteAllTextAsync(Path.Combine(ordinaryPath, ".git", "HEAD"), "ref: refs/heads/main");
        Directory.CreateDirectory(Path.Combine(ordinaryPath, ".git", "objects"));
        Directory.CreateDirectory(Path.Combine(ordinaryPath, ".git", "refs"));

        // 2. Bare repository
        var barePath = Path.Combine(files.TreePath, "bare");
        Directory.CreateDirectory(barePath);
        await File.WriteAllTextAsync(Path.Combine(barePath, "HEAD"), "ref: refs/heads/main");
        Directory.CreateDirectory(Path.Combine(barePath, "objects"));
        Directory.CreateDirectory(Path.Combine(barePath, "refs"));

        // 3. Worktree (.git file)
        var worktreePath = Path.Combine(files.TreePath, "worktree");
        Directory.CreateDirectory(worktreePath);
        await File.WriteAllTextAsync(Path.Combine(worktreePath, ".git"), "gitdir: elsewhere");

        // 4. Standalone unrelated HEAD
        var unrelatedPath = Path.Combine(files.TreePath, "unrelated");
        Directory.CreateDirectory(unrelatedPath);
        await File.WriteAllTextAsync(Path.Combine(unrelatedPath, "HEAD"), "not a repo");

        // 5. Bare missing refs (should not be bare repo)
        var missingRefs = Path.Combine(files.TreePath, "missing_refs");
        Directory.CreateDirectory(missingRefs);
        await File.WriteAllTextAsync(Path.Combine(missingRefs, "HEAD"), "ref: refs/heads/main");
        Directory.CreateDirectory(Path.Combine(missingRefs, "objects"));

        await using var index = await files.OpenIndexAsync();

        var configuration = new GitWizardConfiguration
        {
            SearchPaths = { files.TreePath },
            IncludeTempDirectories = true,
            SkipHiddenDirectories = false
        };
        var request = new DiscoveryRequest(configuration, DiscoveryPurpose.Ordinary, false, false, []);
        var treePath = files.TreePath;
        var scope = DiscoveryScope.CreateExpanded(request, [treePath], [], []);

        var candidates = GitWizardApi.SelectRepositoryCandidates(new FileIndexSet([index]), scope, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(candidates.Select(item => (item.Path, item.Kind)), Is.EquivalentTo([
                    (ordinaryPath, RepositoryCandidateKind.Ordinary),
            (barePath, RepositoryCandidateKind.Bare),
            (worktreePath, RepositoryCandidateKind.GitFile)
                ]));
            Assert.That(candidates.Any(item => item.Path.Contains("/.git/", StringComparison.OrdinalIgnoreCase) ||
                                               item.Path.Contains(@"\.git\", StringComparison.OrdinalIgnoreCase)), Is.False);
        }
    }

    [Test]
    public void CandidateProbe_ValidatesExistingRepositories()
    {
        using var directory = new TempDirectory("CandidateProbeTest_");
        var tempDir = directory.Path;

        var ordinary = Path.Combine(tempDir, "ordinary");
        Directory.CreateDirectory(Path.Combine(ordinary, ".git"));

        var worktree = Path.Combine(tempDir, "worktree");
        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: /somewhere");

        var bare = Path.Combine(tempDir, "bare");
        Directory.CreateDirectory(bare);
        File.WriteAllText(Path.Combine(bare, "HEAD"), "ref: refs/heads/main");
        Directory.CreateDirectory(Path.Combine(bare, "objects"));
        Directory.CreateDirectory(Path.Combine(bare, "refs"));

        var internalGit = Path.Combine(ordinary, ".git");

        var unrelated = Path.Combine(tempDir, "unrelated");
        Directory.CreateDirectory(unrelated);
        File.WriteAllText(Path.Combine(unrelated, "HEAD"), "not a repo");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(RepositoryCandidateProbe.Exists(ordinary, RepositoryCandidateKind.Ordinary), Is.True);
            Assert.That(RepositoryCandidateProbe.Exists(worktree, RepositoryCandidateKind.GitFile), Is.True);
            Assert.That(RepositoryCandidateProbe.Exists(bare, RepositoryCandidateKind.Bare), Is.True);

            // Internal .git is excluded
            Assert.That(RepositoryCandidateProbe.Exists(internalGit, RepositoryCandidateKind.Bare), Is.False);
            Assert.That(RepositoryCandidateProbe.Exists(internalGit, RepositoryCandidateKind.Ordinary), Is.False);

            // Unrelated HEAD is not a bare repo
            Assert.That(RepositoryCandidateProbe.Exists(unrelated, RepositoryCandidateKind.Bare), Is.False);
        }
    }
}

using System.Runtime.Versioning;
using GitWizard;

namespace GitWizardTests.Discovery;

[TestFixture]
public class EnumerationDiscoverySelectionTests
{
    // The pinned index builds a path from at most 128 parent hops, so this is deeper than any path it can name.
    const int DeepLevelCount = 140;

    string _tempDir = null!;
    readonly List<string> _restoreAccess = [];

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "GitWizardTests_Select_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (!OperatingSystem.IsWindows())
        {
            foreach (var directory in _restoreAccess.Where(Directory.Exists))
            {
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        _restoreAccess.Clear();

        try
        {
            Directory.Delete(_tempDir, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort cleanup in temp
        }
    }

    [Test]
    public void Discover_ScansTheIndexTheSameNumberOfTimes_RegardlessOfHeadCandidateCount()
    {
        var fewScans = CountIndexScans(headFileCount: 2);
        var manyScans = CountIndexScans(headFileCount: 24);

        Assert.That(manyScans, Is.EqualTo(fewScans));
    }

    [Test]
    public void Discover_FindsEveryBareRepository_AmongUnrelatedHeadFiles()
    {
        var bareRepositories = new List<string>();
        for (var index = 0; index < 5; index++)
        {
            bareRepositories.Add(CreateBare(Path.Combine(_tempDir, $"bare{index}.git")));
            var notBare = Path.Combine(_tempDir, $"plain{index}");
            Directory.CreateDirectory(notBare);
            File.WriteAllText(Path.Combine(notBare, "HEAD"), "unrelated");
        }

        var result = Discover(new CandidateSelectionDiagnostics());

        Assert.That(result.Paths, Is.EquivalentTo(bareRepositories.Select(GitWizardApi.ResolveOnDiskPath)));
    }

    [TestCase("ordinary")]
    [TestCase("gitfile")]
    [TestCase("bare")]
    public void Discover_ReportsRootIncomplete_ForDeepRepository(string layout)
    {
        var repository = CreateDeepDirectory(layout);
        switch (layout)
        {
            case "ordinary":
                Directory.CreateDirectory(Path.Combine(repository, ".git"));
                break;
            case "gitfile":
                File.WriteAllText(Path.Combine(repository, ".git"), "gitdir: /somewhere/else");
                break;
            default:
                CreateBare(repository);
                break;
        }

        AssertReportedIncomplete();
    }

    [Test]
    public void IsConfirmed_RejectsCandidateInsideMixedCaseGitDirectory()
    {
        var inside = Path.Combine(_tempDir, "repo", ".GIT", "internal");
        Directory.CreateDirectory(Path.Combine(inside, ".git"));

        Assert.That(EnumerationDiscovery.IsConfirmed(new RepositoryCandidate(inside, RepositoryCandidateKind.Ordinary)),
            Is.False);
    }

    [Test]
    public void IsConfirmed_RejectsGitFileAndBareCandidates_TheFilesystemDoesNotConfirm()
    {
        var folder = Path.Combine(_tempDir, "folder");
        Directory.CreateDirectory(folder);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(EnumerationDiscovery.IsConfirmed(new RepositoryCandidate(folder, RepositoryCandidateKind.GitFile)),
                Is.False);
            Assert.That(EnumerationDiscovery.IsConfirmed(new RepositoryCandidate(folder, RepositoryCandidateKind.Bare)),
                Is.False);
        }
    }

    [Test]
    [UnsupportedOSPlatform("windows")]
    public void Discover_RejectsMixedCaseMarkers_OnCaseSensitiveFilesystems()
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            Assert.Ignore("Mixed-case markers are the same names on a case-insensitive filesystem.");
        }

        var upperGitFile = Path.Combine(_tempDir, "upper-gitfile");
        Directory.CreateDirectory(upperGitFile);
        File.WriteAllText(Path.Combine(upperGitFile, ".GIT"), "gitdir: /somewhere/else");

        var lowerLayout = Path.Combine(_tempDir, "lower-layout");
        Directory.CreateDirectory(Path.Combine(lowerLayout, "objects"));
        Directory.CreateDirectory(Path.Combine(lowerLayout, "refs"));
        File.WriteAllText(Path.Combine(lowerLayout, "head"), "ref: refs/heads/main");

        var upperLayout = Path.Combine(_tempDir, "upper-layout");
        Directory.CreateDirectory(Path.Combine(upperLayout, "OBJECTS"));
        Directory.CreateDirectory(Path.Combine(upperLayout, "REFS"));
        File.WriteAllText(Path.Combine(upperLayout, "HEAD"), "ref: refs/heads/main");

        var insideUpperGit = Path.Combine(_tempDir, "repo", ".GIT", "internal");
        Directory.CreateDirectory(Path.Combine(insideUpperGit, ".git"));
        var deepInsideUpperGit = CreateDeepDirectory(Path.Combine("repo", ".GIT", "deep"));
        Directory.CreateDirectory(Path.Combine(deepInsideUpperGit, ".git"));

        var inaccessible = Path.Combine(_tempDir, "repo", ".GIT", "inaccessible");
        Directory.CreateDirectory(inaccessible);
        File.SetUnixFileMode(inaccessible, UnixFileMode.None);
        _restoreAccess.Add(inaccessible);

        var diagnostics = new CandidateSelectionDiagnostics();
        var result = Discover(diagnostics);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(diagnostics.UnresolvedPathCount, Is.Zero);
            Assert.That(result.FailedSubtrees, Is.Empty);
            Assert.That(result.Paths,
                Is.EqualTo([GitWizardApi.ResolveOnDiskPath(Path.Combine(_tempDir, "repo"))]));
        }
    }

    int CountIndexScans(int headFileCount)
    {
        var root = Path.Combine(_tempDir, "scans" + headFileCount);
        for (var index = 0; index < headFileCount; index++)
        {
            var directory = Path.Combine(root, "dir" + index);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "HEAD"), "ref: refs/heads/main");
            Directory.CreateDirectory(Path.Combine(directory, "objects"));
            Directory.CreateDirectory(Path.Combine(directory, "refs"));
        }

        var diagnostics = new CandidateSelectionDiagnostics();
        var result = Discover(diagnostics, root);

        Assert.That(result.Paths, Has.Count.EqualTo(headFileCount));
        return diagnostics.IndexScanCount;
    }

    void AssertReportedIncomplete()
    {
        var diagnostics = new CandidateSelectionDiagnostics();
        var result = Discover(diagnostics);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(diagnostics.UnresolvedPathCount, Is.GreaterThan(0));
            Assert.That(result.Paths, Is.Empty);
            Assert.That(result.FailedSubtrees, Has.Count.EqualTo(1));
            Assert.That(result.Errors, Has.Count.EqualTo(1));
        }
    }

    string CreateDeepDirectory(string name)
    {
        var path = Path.Combine(_tempDir, name);
        for (var level = 0; level < DeepLevelCount; level++)
        {
            path = Path.Combine(path, "d");
        }

        Directory.CreateDirectory(path);
        return path;
    }

    static string CreateBare(string path)
    {
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "HEAD"), "ref: refs/heads/main");
        Directory.CreateDirectory(Path.Combine(path, "objects"));
        Directory.CreateDirectory(Path.Combine(path, "refs"));
        return path;
    }

    RecursiveDiscovery Discover(CandidateSelectionDiagnostics diagnostics, string? root = null)
    {
        var configuration = new GitWizardConfiguration
        {
            SearchPaths = { root ?? _tempDir },
            IncludeTempDirectories = true
        };
        var request = new DiscoveryRequest(configuration, DiscoveryPurpose.Ordinary, false, true, []);
        var expanded = GitWizardApi.ExpandSearchPath(root ?? _tempDir)!;
        return EnumerationDiscovery.DiscoverAsync(request, expanded, CancellationToken.None, diagnostics)
            .GetAwaiter().GetResult();
    }
}

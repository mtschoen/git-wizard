using GitWizard;
using GitWizardTests.TestSupport;
using MFTLib.Index;

namespace GitWizardTests.Discovery;

[TestFixture]
[NonParallelizable]
public class EnumerationDiscoveryTests
{
    string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "GitWizardTests_Enum_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        TempDirectories.IsTempPathOverride = null;
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // Best effort cleanup in temp.
        }
    }

    [Test]
    public void IndexOptionsRetainOnlyGitMarkersAndOpenWithoutAnyCache()
    {
        var options = EnumerationDiscovery.CreateIndexOptions(new GitWizardConfiguration(), _root, []);

        Assert.That(options.RetainedFileNames, Is.EqualTo([".git", "HEAD"]));
        Assert.That(options.CacheMode, Is.EqualTo(CacheMode.Disabled));
        Assert.That(options.CacheDirectory, Is.Null);
        Assert.That(options.Drives.Single().Producer, Is.EqualTo(ProducerKind.Enumeration));
        Assert.That(options.Drives.Single().RootDirectory, Is.EqualTo(_root));
    }

    [Test]
    public void WalkAlwaysPrunesGitDirectories_InEveryCasing()
    {
        var walk = EnumerationDiscovery.CreateDirectoryWalk(new GitWizardConfiguration(), _root, []);

        Assert.That(walk.PrunedDirectoryNames, Is.EquivalentTo([
            ".git", ".Git", ".gIt", ".giT", ".GIt", ".GiT", ".gIT", ".GIT"
        ]));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void HiddenSettingMapsToDotAndHiddenAttributePruning(bool skipHidden)
    {
        var configuration = new GitWizardConfiguration { SkipHiddenDirectories = skipHidden };

        var walk = EnumerationDiscovery.CreateDirectoryWalk(configuration, _root, []);

        Assert.That(walk.PruneDotDirectories, Is.EqualTo(skipHidden));
        Assert.That(walk.PrunedDirectoryAttributes, Is.EqualTo(skipHidden ? FileAttributes.Hidden : 0));
    }

    [Test]
    public void UnsetHiddenSettingPrunesNothingByHiddenness()
    {
        var walk = EnumerationDiscovery.CreateDirectoryWalk(new GitWizardConfiguration(), _root, []);

        Assert.That(walk.PruneDotDirectories, Is.False);
        Assert.That(walk.PrunedDirectoryAttributes, Is.EqualTo((FileAttributes)0));
    }

    [Test]
    public void IgnoredPathsArePrunedByFullPath()
    {
        var ignored = Path.Combine(_root, "skipped");

        var walk = EnumerationDiscovery.CreateDirectoryWalk(new GitWizardConfiguration(), _root, [ignored]);

        Assert.That(walk.PrunedDirectoryPaths, Does.Contain(ignored));
    }

    [Test]
    public void TemporaryDirectoriesBecomeConcretePrunedPathsWhenTheRootIsNotTemporary()
    {
        var temporary = Path.Combine(_root, "scratch");
        Directory.CreateDirectory(temporary);
        TempDirectories.IsTempPathOverride = path => path.StartsWith(temporary, StringComparison.OrdinalIgnoreCase);
        var previous = Environment.GetEnvironmentVariable("TMPDIR");
        try
        {
            Environment.SetEnvironmentVariable("TMPDIR", temporary);
            var configuration = new GitWizardConfiguration { IncludeTempDirectories = false };
            var walk = EnumerationDiscovery.CreateDirectoryWalk(configuration, _root, []);

            Assert.That(walk.PrunedDirectoryPaths, Does.Contain(temporary));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TMPDIR", previous);
        }
    }

    [Test]
    public void TemporaryDirectoriesAreWalkedWhenIncludedOrWhenTheRootIsTemporary()
    {
        TempDirectories.IsTempPathOverride = _ => true;

        var rootIsTemporary = EnumerationDiscovery.CreateDirectoryWalk(
            new GitWizardConfiguration { IncludeTempDirectories = false }, _root, []);
        Assert.That(rootIsTemporary.PrunedDirectoryPaths, Is.Empty);

        TempDirectories.IsTempPathOverride = path => !path.Equals(_root, StringComparison.OrdinalIgnoreCase);
        var included = EnumerationDiscovery.CreateDirectoryWalk(
            new GitWizardConfiguration { IncludeTempDirectories = true }, _root, []);
        Assert.That(included.PrunedDirectoryPaths, Is.Empty);
    }

    [Test]
    public void DefaultCrawlDependencyDiscoversRepositoriesThroughTheEnumerationIndex()
    {
        var repository = Path.Combine(_root, "repo");
        Directory.CreateDirectory(Path.Combine(repository, ".git"));
        var configuration = new GitWizardConfiguration { IncludeTempDirectories = true };
        var request = new DiscoveryRequest(configuration, DiscoveryPurpose.Ordinary, false, true, []);

        var result = RepositoryDiscoveryDependencies.CreateDefault()
            .CrawlRootAsync(request, _root, CancellationToken.None).GetAwaiter().GetResult();

        Assert.That(result.Paths, Is.EqualTo([GitWizardApi.NormalizePath(repository)]));
        Assert.That(result.Errors, Is.Empty);
    }

    [Test]
    public void DirectoriesBeneathAnIgnoredPathAreNeverEntered()
    {
        var ignored = Path.Combine(_root, "ignored");
        Directory.CreateDirectory(Path.Combine(ignored, "nested", ".git"));
        Directory.CreateDirectory(Path.Combine(_root, "kept", ".git"));

        var paths = new List<string>();
        EnumeratedDiscovery.GetRepositoryPaths(_root, paths, [ignored], includeTempDirectories: true);

        Assert.That(paths, Is.EqualTo([GitWizardApi.NormalizePath(Path.Combine(_root, "kept"))]));
    }

    [Test]
    public void MissingRootIsReportedAsFailedSubtree()
    {
        var missing = Path.Combine(_root, "absent");
        var request = new DiscoveryRequest(new GitWizardConfiguration(), DiscoveryPurpose.Ordinary, false, true, []);

        var result = EnumerationDiscovery.DiscoverAsync(request, missing, CancellationToken.None).GetAwaiter().GetResult();

        Assert.That(result.FailedSubtrees, Is.EqualTo([missing]));
        Assert.That(result.Errors, Does.ContainKey(missing));
    }
}

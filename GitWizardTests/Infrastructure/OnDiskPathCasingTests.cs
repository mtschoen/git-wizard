using GitWizard;
using GitWizardTests.TestSupport;
using LibGit2Sharp;

namespace GitWizardTests;

/// <summary>
/// Windows matches paths case-insensitively, so the casing a path is typed with can differ from the casing the
/// directory is stored with. Published paths and root keys must carry the stored casing.
/// </summary>
[TestFixture]
[NonParallelizable]
public class OnDiskPathCasingTests
{
    TempDirectory _tempDirectory = null!;
    string _storedRoot = null!;

    [SetUp]
    public void SetUp()
    {
        GitWizardLog.SilentMode = true;
        _tempDirectory = new TempDirectory("gw-casing-");
        _storedRoot = Path.Combine(_tempDirectory.Path, "MixedCaseRoot");
        Directory.CreateDirectory(_storedRoot);
    }

    [TearDown]
    public void TearDown()
    {
        TempDirectories.IsTempPathOverride = null;
        _tempDirectory.Dispose();
    }

    static string Typed(string path) => path.ToUpperInvariant();

    static void RequireCaseInsensitiveFilesystem()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Ignore("Typed casing only differs from stored casing on case-insensitive Windows paths.");
        }
    }

    [Test]
    public void ExpandSearchPath_PublishesStoredCasingOfATypedRoot()
    {
        RequireCaseInsensitiveFilesystem();

        var expanded = GitWizardApi.ExpandSearchPath(Typed(_storedRoot) + Path.DirectorySeparatorChar);

        Assert.That(expanded, Is.EqualTo(_storedRoot));
    }

    [Test]
    public void ExpandIgnoredPath_PublishesStoredCasingOfATypedRoot()
    {
        RequireCaseInsensitiveFilesystem();

        var expanded = GitWizardApi.ExpandIgnoredPath(Typed(_storedRoot));

        Assert.That(expanded, Is.EqualTo(_storedRoot));
    }

    [Test]
    public void ExpandIgnoredPath_KeepsTypedCasingBelowTheFirstMissingSegment()
    {
        var typed = Path.Combine(_storedRoot, "Absent", "Child");

        var expanded = GitWizardApi.ExpandIgnoredPath(typed);

        Assert.That(expanded, Is.EqualTo(typed));
    }

    [Test]
    public void WalkRoute_PublishesStoredCasingOfRepositories()
    {
        var repository = Path.Combine(_storedRoot, "MixedRepo");
        Directory.CreateDirectory(Path.Combine(repository, ".git"));
        TempDirectories.IsTempPathOverride = _ => false;
        var request = new DiscoveryRequest(
            new GitWizardConfiguration { IncludeTempDirectories = true }, DiscoveryPurpose.Ordinary, false, true, []);

        var result = EnumerationDiscovery.DiscoverAsync(request, _storedRoot, CancellationToken.None)
            .GetAwaiter().GetResult();

        Assert.That(result.Paths, Is.EqualTo([repository]));
    }

    [Test]
    public void ExpandAllClonePaths_PublishesStoredCasingAndDedupsTypedCasings()
    {
        RequireCaseInsensitiveFilesystem();
        var repository = Path.Combine(_storedRoot, "MixedRepo");
        Repository.Init(repository);

        var expanded = GitWizardApi.ExpandAllClonePaths([Typed(repository), repository + Path.DirectorySeparatorChar]);

        Assert.That(expanded, Is.EqualTo([repository]));
    }
}

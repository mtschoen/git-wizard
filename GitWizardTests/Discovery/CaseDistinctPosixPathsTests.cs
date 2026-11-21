using System.Runtime.Versioning;
using GitWizard;
using GitWizard.Watch;
using GitWizard.Watch.Linux;
using GitWizardTests.TestSupport;

namespace GitWizardTests.Discovery;

// Two POSIX paths that differ only by case are distinct directories, so error maps keyed by path
// must keep both instead of throwing on a duplicate key or merging them.
[TestFixture]
[Platform("Linux")]
[SupportedOSPlatform("linux")]
public class CaseDistinctPosixPathsTests
{
    TempDirectory _tempDirectory = null!;
    string _upperPath = null!;
    string _lowerPath = null!;

    [SetUp]
    public void CreateTemporaryParent()
    {
        _tempDirectory = new TempDirectory("gw-case-distinct-");
        _upperPath = Path.Combine(_tempDirectory.Path, "Repos");
        _lowerPath = Path.Combine(_tempDirectory.Path, "repos");
    }

    [TearDown]
    public void DeleteTemporaryParent() => _tempDirectory.Dispose();

    DiscoveryScope CreateScopeWithCaseDistinctMissingPaths()
    {
        var configuration = new GitWizardConfiguration
        {
            IncludeTempDirectories = true,
            SearchPaths = { _upperPath, _lowerPath }
        };
        var request = new DiscoveryRequest(configuration, DiscoveryPurpose.Watch, false, false, []);
        return DiscoveryScope.Create(request);
    }

    [Test]
    public void Accumulator_CaseDistinctExpansionErrors_AreBothRetained()
    {
        var scope = CreateScopeWithCaseDistinctMissingPaths();

        var accumulator = new DiscoveryAccumulator(scope, new HashSet<string>(WatchPath.StringComparer));

        Assert.That(accumulator.Errors.Keys, Is.EquivalentTo([_upperPath, _lowerPath]));
    }

    [Test]
    public void GetNoRootsErrors_CaseDistinctExpansionErrors_KeepsBothAndNoRootsError()
    {
        var scope = CreateScopeWithCaseDistinctMissingPaths();

        var errors = scope.GetNoRootsErrors();

        Assert.That(errors.Keys, Is.EquivalentTo([_upperPath, _lowerPath, DiscoveryScope.NoRootsErrorKey]));
    }

    [Test]
    public async Task FanotifyArm_CaseDistinctVolumes_ReportsBothErrors()
    {
        var native = new FakeFanotifyNative
        {
            InitializeResult = new FanotifyNativeCallResult(-1, FanotifyError.NotPermitted)
        };
        var source = new FanotifyVolumeChangeSource(native);

        var result = await source.ArmAndCatchUpAsync([_upperPath, _lowerPath], CancellationToken.None);

        Assert.That(result.Errors.Keys, Is.EquivalentTo([_upperPath, _lowerPath]));
    }
}

using GitWizardTests.TestSupport;

namespace GitWizardTests.Discovery;

/// <summary>Linux retained roots pass through the same admission policy as every other route.</summary>
[TestFixture]
public class LinuxRetainedRootAdmissionTests
{

    static readonly string Root = Path.Combine(TempDirectory.SystemTempPath, "gw-linux-admission");


    [Test]
    public async Task RetainedRootOutsideSearchRoots_IsAdmitted()
    {
        var paths = await CandidateSelection.InventoryAsync(Path.Combine(Root, "search"), Path.Combine(Root, "retained", "repo"), null, false, true);
        Assert.That(paths, Is.EqualTo([Path.Combine(Root, "retained", "repo")]).IgnoreCase);
    }

    [Test]
    public async Task RetainedRootUnderIgnoredRoot_IsRejected()
    {
        var paths = await CandidateSelection.InventoryAsync(Path.Combine(Root, "search"), Path.Combine(Root, "retained", "ignored", "repo"), Path.Combine(Root, "retained", "ignored"), false, true);
        Assert.That(paths, Is.Empty);
    }

    [Test]
    public async Task RetainedRootUnderHiddenSegmentOfSearchRoot_IsRejectedWhenHiddenDirectoriesAreSkipped()
    {
        var paths = await CandidateSelection.InventoryAsync(Path.Combine(Root, "search"), Path.Combine(Root, "search", ".hidden", "repo"), null, true, true);
        Assert.That(paths, Is.Empty);
    }
}

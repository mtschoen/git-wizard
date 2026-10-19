using GitWizard;

namespace GitWizardTests.Discovery;

/// <summary>
/// Direct policy tests for <see cref="CandidateAdmissionPolicy.AdmitsInventoryCandidate(DiscoveryScope, string)"/>:
/// a replay (journal-buffered) candidate must satisfy the same ignored-, hidden-, and
/// temporary-directory rules as indexed candidate selection, with exact retained roots outside
/// every search root still claimed.
/// </summary>
[TestFixture]
public class ReplayCandidatePolicyTests
{
    static DiscoveryScope Scope(
        IReadOnlyList<string> searchRoots,
        IReadOnlyList<string>? retainedRoots = null,
        IReadOnlyList<string>? ignoredRoots = null,
        bool? skipHiddenDirectories = null,
        bool? includeTempDirectories = null)
    {
        var configuration = new GitWizardConfiguration
        {
            SkipHiddenDirectories = skipHiddenDirectories,
            IncludeTempDirectories = includeTempDirectories
        };
        var request = new DiscoveryRequest(configuration, DiscoveryPurpose.Watch, false, false, retainedRoots ?? []);
        return DiscoveryScope.CreateExpanded(request, searchRoots, retainedRoots ?? [], ignoredRoots ?? [],
            root => new RootClassification(root, RootFileSystem.Ntfs, "X", null));
    }

    [Test]
    public void PlainCandidateUnderSearchRoot_IsAllowed()
    {
        var scope = Scope([@"X:\repos"]);
        Assert.That(CandidateAdmissionPolicy.AdmitsInventoryCandidate(scope, @"X:\repos\repo"), Is.True);
    }

    [TestCase(null, true)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    public void CandidateUnderHiddenSegment(bool? skipHiddenDirectories, bool expectedAllowed)
    {
        var scope = Scope([@"X:\repos"], skipHiddenDirectories: skipHiddenDirectories);
        Assert.That(CandidateAdmissionPolicy.AdmitsInventoryCandidate(scope, @"X:\repos\.hidden\repo"), Is.EqualTo(expectedAllowed));
    }

    [Test]
    public void CandidateUnderTempDirectory_IsRejected()
    {
        // A drive-root scope is not itself temp, so candidates under a profile temp subtree are excluded.
        var scope = Scope([@"X:\"]);
        Assert.That(CandidateAdmissionPolicy.AdmitsInventoryCandidate(
            scope, @"X:\Users\alice\AppData\Local\Temp\repo"), Is.False);
    }

    [Test]
    public void CandidateUnderTempDirectory_WithTempIncluded_IsAllowed()
    {
        var scope = Scope([@"X:\"], includeTempDirectories: true);
        Assert.That(CandidateAdmissionPolicy.AdmitsInventoryCandidate(
            scope, @"X:\Users\alice\AppData\Local\Temp\repo"), Is.True);
    }

    [Test]
    public void CandidateUnderSearchRootInsideTemp_IsAllowed()
    {
        // A search root configured inside a temp directory opts that subtree back in.
        var scope = Scope([@"X:\Users\alice\AppData\Local\Temp\scratch"]);
        Assert.That(CandidateAdmissionPolicy.AdmitsInventoryCandidate(
            scope, @"X:\Users\alice\AppData\Local\Temp\scratch\repo"), Is.True);
    }

    [Test]
    public void CandidateUnderIgnoredRoot_IsRejected()
    {
        var scope = Scope([@"X:\repos"], ignoredRoots: [@"X:\repos\ignored"]);
        Assert.That(CandidateAdmissionPolicy.AdmitsInventoryCandidate(scope, @"X:\repos\ignored\repo"), Is.False);
    }

    [Test]
    public void ExactRetainedRootOutsideSearchRoots_IsAllowed()
    {
        var scope = Scope([@"X:\repos"], retainedRoots: [@"Y:\elsewhere\repo"]);
        Assert.That(CandidateAdmissionPolicy.AdmitsInventoryCandidate(scope, @"Y:\elsewhere\repo"), Is.True);
    }

    [Test]
    public void RetainedRootUnderHiddenSearchSubtree_WithSkipHiddenDirectories_IsRejected()
    {
        // Mirrors SelectRepositoryCandidates: a retained root under a search root is not an
        // exact scope, so the search root's hidden-directory rule decides when it is enabled.
        var scope = Scope([@"X:\repos"], retainedRoots: [@"X:\repos\.hidden\repo"], skipHiddenDirectories: true);
        Assert.That(CandidateAdmissionPolicy.AdmitsInventoryCandidate(scope, @"X:\repos\.hidden\repo"), Is.False);
    }

    [Test]
    public void UnrelatedCandidate_IsRejected()
    {
        var scope = Scope([@"X:\repos"], retainedRoots: [@"Y:\elsewhere\repo"]);
        Assert.That(CandidateAdmissionPolicy.AdmitsInventoryCandidate(scope, @"Z:\other\repo"), Is.False);
    }
}

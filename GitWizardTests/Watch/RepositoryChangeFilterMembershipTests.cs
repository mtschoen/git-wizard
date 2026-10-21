using GitWizard;
using GitWizard.Watch;

namespace GitWizardTests;

// Watch tests are split by concern because the combined fixture exceeds 500 lines.
// Covers RepositoryChangeFilter (dynamic repository root addition and removal).
public class RepositoryChangeFilterMembershipTests
{
    const string RepoRoot = @"C:\repos\repo-a";
    static VolumeChangeEntry Modified(string fullPath) => new(fullPath, VolumeEntryKind.Modified);
    static VolumeChangeEntry Created(string fullPath) => new(fullPath, VolumeEntryKind.Created);

    [Test]
    public void AddRepositoryRoot_SubsequentModifiedUnderAddedRoot_MapsToChanged()
    {
        var filter = new RepositoryChangeFilter([], [@"C:\repos"]);
        var created = filter.Classify([Created(@"C:\repos\new-repo\.git")]);
        Assert.That(created.Created, Is.EquivalentTo(new[] { @"C:\repos\new-repo" }));

        filter.AddRepositoryRoot(@"C:\repos\new-repo");

        var result = filter.Classify([Modified(@"C:\repos\new-repo\index.lock")]);
        Assert.That(result.Changed, Is.EquivalentTo(new[] { @"C:\repos\new-repo" }));
        Assert.That(result.Created, Is.Empty);
        Assert.That(result.Deleted, Is.Empty);
    }

    [Test]
    public void AddRepositoryRoot_NestedUnderExistingRoot_LongestMatchWins()
    {
        var filter = new RepositoryChangeFilter([@"C:\repos\outer"], [@"C:\repos"]);

        filter.AddRepositoryRoot(@"C:\repos\outer\inner");

        var result = filter.Classify([Modified(@"C:\repos\outer\inner\index.lock")]);
        Assert.That(result.Changed, Is.EquivalentTo(new[] { @"C:\repos\outer\inner" }));
    }

    [Test]
    public void AddRepositoryRoot_DuplicateWithDifferentCasing_DoesNotTrackTwice()
    {
        var filter = new RepositoryChangeFilter([], [@"C:\repos"]);

        filter.AddRepositoryRoot(@"C:\repos\New-Repo");
        filter.AddRepositoryRoot(@"C:\repos\new-repo");

        var recreated = filter.Classify([Created(@"C:\repos\NEW-REPO\.git")]);
        Assert.That(recreated.Created, Is.Empty);
        Assert.That(recreated.Changed, Is.EquivalentTo(new[] { @"C:\repos\New-Repo" }));
    }

    [Test]
    public void AddRepositoryRoot_PosixRoot_PreservesPathShape()
    {
        var filter = new RepositoryChangeFilter([], ["/repos"]);

        filter.AddRepositoryRoot("/repos/new-repo");

        var result = filter.Classify([Modified("/repos/new-repo/index.lock")]);
        Assert.That(result.Changed, Is.EquivalentTo(new[] { "/repos/new-repo" }));
    }

    [Test]
    public void AddRepositoryRoot_ManyAdoptions_MaintainsDescendingOrderAndClassifiesCorrectly()
    {
        var filter = new RepositoryChangeFilter([], [@"C:\repos"]);

        // Add 5000 roots of varying lengths in arbitrary order
        for (var index = 0; index < 5000; index++)
        {
            var depth = index % 37;
            filter.AddRepositoryRoot($@"C:\repos\dir_{depth}\project_{index:D4}\sub_{index % 11}");
        }

        // Add nested root
        var targetRoot = $@"C:\repos\dir_{42 % 37}\project_0042\sub_{42 % 11}";
        filter.AddRepositoryRoot($@"{targetRoot}\nested_component");

        // Verify nested root matches first (longest match)
        var nestedResult = filter.ClassifyEntry(Modified($@"{targetRoot}\nested_component\file.txt"));
        Assert.That(nestedResult, Is.EqualTo((RepositoryChangeKind.Changed, $@"{targetRoot}\nested_component")));

        // Verify outer root matches when outside nested
        var outerResult = filter.ClassifyEntry(Modified($@"{targetRoot}\file.txt"));
        Assert.That(outerResult, Is.EqualTo((RepositoryChangeKind.Changed, targetRoot)));
    }

    [Test]
    public void RemoveRepositoryRoot_SubsequentChanges_IgnoredUntilGitRecreated()
    {
        var filter = new RepositoryChangeFilter([RepoRoot], [@"C:\repos"]);

        filter.RemoveRepositoryRoot(RepoRoot);

        var afterRemoval = filter.Classify([Modified(Path.Combine(RepoRoot, "index.lock"))]);
        Assert.That(afterRemoval.Changed, Is.Empty);

        var recreated = filter.Classify([Created(Path.Combine(RepoRoot, ".git"))]);
        Assert.That(recreated.Created, Is.EquivalentTo(new[] { RepoRoot }));
        Assert.That(recreated.Changed, Is.Empty);
    }

    [Test]
    public void RemoveRepositoryRoot_UnknownRoot_IsANoOp()
    {
        var filter = new RepositoryChangeFilter([RepoRoot], [@"C:\repos"]);

        filter.RemoveRepositoryRoot(@"C:\repos\never-tracked");

        var result = filter.Classify([Modified(Path.Combine(RepoRoot, "index.lock"))]);
        Assert.That(result.Changed, Is.EquivalentTo(new[] { RepoRoot }));
    }

    [Test]
    public void RemoveRepositoryRoot_AllowsReAddingAfterRemoval()
    {
        var filter = new RepositoryChangeFilter([], [@"C:\repos"]);

        filter.AddRepositoryRoot(@"C:\repos\temp");
        Assert.That(filter.ClassifyEntry(Modified(@"C:\repos\temp\file.txt")),
            Is.EqualTo((RepositoryChangeKind.Changed, @"C:\repos\temp")));

        filter.RemoveRepositoryRoot(@"C:\repos\temp");
        Assert.That(filter.ClassifyEntry(Modified(@"C:\repos\temp\file.txt")), Is.Null);

        filter.AddRepositoryRoot(@"C:\repos\temp");
        Assert.That(filter.ClassifyEntry(Modified(@"C:\repos\temp\file.txt")),
            Is.EqualTo((RepositoryChangeKind.Changed, @"C:\repos\temp")));
    }
}

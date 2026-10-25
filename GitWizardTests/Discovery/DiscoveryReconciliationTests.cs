using GitWizard;

namespace GitWizardTests.Discovery;

[TestFixture]
public class DiscoveryReconciliationTests
{
    [TestCase(DiscoveryOutcome.Partial)]
    [TestCase(DiscoveryOutcome.Unusable)]
    public void IncompleteInventoryCannotPublish(DiscoveryOutcome outcome)
    {
        var inventory = new DiscoveryInventory
        {
            Outcome = outcome,
            Route = DiscoveryRoute.Indexed,
            Paths = [],
            RequestedNtfsDrives = ["C", "D"],
            Observations = [],
            DriveStatuses = [],
            Errors = new Dictionary<string, string> { ["D"] = "offline" },
            WatchExcludedRoots = []
        };
        Assert.That(inventory.CanPublishFullInventory, Is.False);
    }

    [TestCase(DiscoveryOutcome.CompleteEmpty)]
    [TestCase(DiscoveryOutcome.CompleteNonempty)]
    public void CompleteInventoryCanPublish(DiscoveryOutcome outcome)
    {
        var inventory = new DiscoveryInventory
        {
            Outcome = outcome,
            Route = DiscoveryRoute.Indexed,
            Paths = [],
            RequestedNtfsDrives = ["C"],
            Observations = [],
            DriveStatuses = [],
            Errors = new Dictionary<string, string>(),
            WatchExcludedRoots = []
        };
        Assert.That(inventory.CanPublishFullInventory, Is.True);
    }

    [Test]
    public void PartialZeroRetainsFailedAndUnobservedScope()
    {
        var inventory = new DiscoveryInventory
        {
            Outcome = DiscoveryOutcome.Partial,
            Route = DiscoveryRoute.Indexed,
            Paths = [],
            RequestedNtfsDrives = ["C", "D"],
            Observations = [new RootObservation(@"C:\repos", false, true, DiscoverySource.Replay, null, null),
             new RootObservation(@"D:\repos", false, false, DiscoverySource.Replay, null, "offline")],
            DriveStatuses = [],
            Errors = new Dictionary<string, string> { ["D"] = "offline" },
            WatchExcludedRoots = []
        };
        var reconciled = RepositoryDiscoveryCoordinator.ReconcilePaths(
            [@"C:\repos\gone", @"D:\repos\keep", @"E:\outside\keep"], inventory);
        Assert.That(reconciled, Is.EquivalentTo([@"D:\repos\keep", @"E:\outside\keep"]));
    }

    [Test]
    public void CompleteEmptyClearsPreviousList()
    {
        var inventory = new DiscoveryInventory
        {
            Outcome = DiscoveryOutcome.CompleteEmpty,
            Route = DiscoveryRoute.Indexed,
            Paths = [],
            RequestedNtfsDrives = ["C"],
            Observations = [new RootObservation(@"C:\repos", false, true, DiscoverySource.Scan, DateTime.UtcNow, null)],
            DriveStatuses = [],
            Errors = new Dictionary<string, string>(),
            WatchExcludedRoots = []
        };
        var reconciled = RepositoryDiscoveryCoordinator.ReconcilePaths(
            [@"C:\repos\old1", @"C:\repos\old2"], inventory);
        Assert.That(reconciled, Is.Empty);
    }

    [Test]
    public void CompleteNonemptyReplacesPreviousList()
    {
        var inventory = new DiscoveryInventory
        {
            Outcome = DiscoveryOutcome.CompleteNonempty,
            Route = DiscoveryRoute.Indexed,
            Paths = [@"C:\repos\new1", @"C:\repos\new2"],
            RequestedNtfsDrives = ["C"],
            Observations = [new RootObservation(@"C:\repos", false, true, DiscoverySource.Scan, DateTime.UtcNow, null)],
            DriveStatuses = [],
            Errors = new Dictionary<string, string>(),
            WatchExcludedRoots = []
        };
        var reconciled = RepositoryDiscoveryCoordinator.ReconcilePaths(
            [@"C:\repos\old1"], inventory);
        Assert.That(reconciled, Is.EquivalentTo([@"C:\repos\new1", @"C:\repos\new2"]));
    }

    [Test]
    public void UnusablePreservesPreviousListExactly()
    {
        string[] previous = [@"C:\repos\r1", @"D:\repos\r2"];
        var inventory = new DiscoveryInventory
        {
            Outcome = DiscoveryOutcome.Unusable,
            Route = DiscoveryRoute.Indexed,
            Paths = [],
            RequestedNtfsDrives = ["C", "D"],
            Observations = [new RootObservation(@"C:\repos", false, false, DiscoverySource.Scan, null, "broker error"),
             new RootObservation(@"D:\repos", false, false, DiscoverySource.Scan, null, "offline")],
            DriveStatuses = [],
            Errors = new Dictionary<string, string> { ["C"] = "broker error", ["D"] = "offline" },
            WatchExcludedRoots = []
        };
        var reconciled = RepositoryDiscoveryCoordinator.ReconcilePaths(previous, inventory);
        Assert.That(reconciled, Is.EquivalentTo(previous));
    }

    [Test]
    public void OverlappingRoots_FailedChildOverridesSuccessfulAncestor()
    {
        var inventory = new DiscoveryInventory
        {
            Outcome = DiscoveryOutcome.Partial,
            Route = DiscoveryRoute.Indexed,
            Paths = [@"C:\repos\survived"],
            RequestedNtfsDrives = ["C"],
            Observations = [new RootObservation(@"C:\repos", false, true, DiscoverySource.Scan, DateTime.UtcNow, null),
             new RootObservation(@"C:\repos\sub", false, false, DiscoverySource.Scan, null, "access denied")],
            DriveStatuses = [],
            Errors = new Dictionary<string, string> { [@"C:\repos\sub"] = "access denied" },
            WatchExcludedRoots = []
        };
        var reconciled = RepositoryDiscoveryCoordinator.ReconcilePaths(
            [@"C:\repos\gone", @"C:\repos\sub\child_keep", @"C:\repos\survived"], inventory);
        Assert.That(reconciled, Is.EquivalentTo([@"C:\repos\sub\child_keep", @"C:\repos\survived"]));
    }

    [Test]
    public void StaleSnapshotDoesNotPruneMissingEntries()
    {
        var inventory = new DiscoveryInventory
        {
            Outcome = DiscoveryOutcome.CompleteNonempty,
            Route = DiscoveryRoute.Indexed,
            Paths = [@"C:\repos\found"],
            RequestedNtfsDrives = ["C"],
            Observations = [new RootObservation(@"C:\repos", false, true, DiscoverySource.Snapshot, DateTime.UtcNow.AddHours(-2), null)],
            DriveStatuses = [],
            Errors = new Dictionary<string, string>(),
            WatchExcludedRoots = []
        };
        var reconciled = RepositoryDiscoveryCoordinator.ReconcilePaths(
            [@"C:\repos\old", @"C:\repos\found"], inventory);
        Assert.That(reconciled, Is.EquivalentTo([@"C:\repos\old", @"C:\repos\found"]));
    }

    [Test]
    public void ExactRetainedRootOutsideSearchScope_RetainedWhenIncomplete()
    {
        var inventory = new DiscoveryInventory
        {
            Outcome = DiscoveryOutcome.Partial,
            Route = DiscoveryRoute.Indexed,
            Paths = [],
            RequestedNtfsDrives = ["C"],
            Observations = [new RootObservation(@"C:\repos", false, true, DiscoverySource.Scan, DateTime.UtcNow, null),
             new RootObservation(@"E:\retained", true, false, DiscoverySource.Scan, null, "offline")],
            DriveStatuses = [],
            Errors = new Dictionary<string, string> { ["E"] = "offline" },
            WatchExcludedRoots = []
        };
        var reconciled = RepositoryDiscoveryCoordinator.ReconcilePaths(
            [@"C:\repos\gone", @"E:\retained"], inventory);
        Assert.That(reconciled, Is.EquivalentTo([@"E:\retained"]));
    }

    [Test]
    public void PosixCaseDistinctions_PreservedInReconciliation()
    {
        var inventory = new DiscoveryInventory
        {
            Outcome = DiscoveryOutcome.CompleteNonempty,
            Route = DiscoveryRoute.RecursiveOnly,
            Paths = ["/repos/MyRepo"],
            RequestedNtfsDrives = [],
            Observations = [new RootObservation("/repos", false, true, DiscoverySource.Recursive, DateTime.UtcNow, null)],
            DriveStatuses = [],
            Errors = new Dictionary<string, string>(),
            WatchExcludedRoots = []
        };
        var reconciled = RepositoryDiscoveryCoordinator.ReconcilePaths(
            ["/repos/myrepo"], inventory);
        Assert.That(reconciled, Is.EquivalentTo(["/repos/MyRepo"]));
    }

    [Test]
    public async Task ResultDisposal_ClosesSession_WhenNotDetached()
    {
        await using var fixture = new TestSupport.IndexOwnerFixture();
        await fixture.StartAsync();
        var inventory = new DiscoveryInventory
        {
            Outcome = DiscoveryOutcome.CompleteNonempty,
            Route = DiscoveryRoute.Indexed,
            Paths = ["C:\\repo"],
            RequestedNtfsDrives = ["C"],
            Observations = [],
            DriveStatuses = [],
            Errors = new Dictionary<string, string>(),
            WatchExcludedRoots = []
        };
        var result = new RepositoryDiscoveryResult(inventory, fixture.Owner);
        await result.DisposeAsync();
        Assert.That(fixture.ConnectionDisposals, Is.EqualTo(1));
    }

    [Test]
    public async Task ResultDetach_SurvivesResultDisposal()
    {
        await using var fixture = new TestSupport.IndexOwnerFixture();
        await fixture.StartAsync();
        var inventory = new DiscoveryInventory
        {
            Outcome = DiscoveryOutcome.CompleteNonempty,
            Route = DiscoveryRoute.Indexed,
            Paths = ["C:\\repo"],
            RequestedNtfsDrives = ["C"],
            Observations = [],
            DriveStatuses = [],
            Errors = new Dictionary<string, string>(),
            WatchExcludedRoots = []
        };
        var result = new RepositoryDiscoveryResult(inventory, fixture.Owner);
        var detached = result.DetachIndexSession();
        Assert.That(detached, Is.SameAs(fixture.Owner));
        await result.DisposeAsync();
        Assert.That(fixture.ConnectionDisposals, Is.Zero);
    }
}

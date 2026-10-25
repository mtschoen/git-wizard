using GitWizard;
using GitWizardTests.TestSupport;
using MFTLib.Index;
using MFTLibTestExtensions;

namespace GitWizardTests;

[TestFixture]
public class MftIndexCacheMaintenanceTests
{
    [Test]
    public async Task ClearPreservesOwnerDeletesLegacyDirectoriesAndRefreshesInventory()
    {
        using var files = new IndexCacheFixture();
        var older = Path.Combine(MftIndexCachePolicy.GetCacheRoot(files.Home), "v1-" + new string('A', 64));
        Directory.CreateDirectory(older);
        var olderBlock = SyntheticBlock.CachedPath(older, 'Y', 1);
        await File.WriteAllTextAsync(olderBlock, "legacy hashed-directory block");
        var unknown = Path.Combine(MftIndexCachePolicy.GetCacheRoot(files.Home), "unrecognized");
        Directory.CreateDirectory(unknown);
        var unknownBlock = SyntheticBlock.CachedPath(unknown, 'Z', 1);
        await File.WriteAllTextAsync(unknownBlock, "keep unknown layout");
        await using (var owner = await files.OpenOwnerAsync())
        {
            var result = MftIndexCacheMaintenance.Clear(files.Home);
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Errors, Is.Empty);
            Assert.That(result.Deletions.Single(row => row.File.Path == files.OwnedPath).Outcome,
                Is.EqualTo(CachedBlockDeletionOutcome.InUse));
            Assert.That(result.Deletions.Where(row => row.File.Drive == 'Y')
                .All(row => row.Outcome == CachedBlockDeletionOutcome.Deleted), Is.True);
            Assert.That(result.Deletions, Has.Count.EqualTo(3));
            Assert.That(File.Exists(files.OwnedPath), Is.True);
            Assert.That(File.Exists(files.FreePath), Is.False);
            Assert.That(File.Exists(olderBlock), Is.False);
            Assert.That(File.Exists(unknownBlock), Is.True);
            Assert.That(await File.ReadAllTextAsync(Path.Combine(files.Cache, "unrelated.txt")), Is.EqualTo("keep"));
            foreach (var path in new[] { files.OwnedPath, files.FreePath, olderBlock })
            {
                Assert.That(File.Exists(path + ".lock"), Is.True);
            }

            Assert.That(result.Inventory.Select(row => row.File.Path), Is.EqualTo(new[] { files.OwnedPath }));
            Assert.That(result.Inventory.Single().Availability, Is.EqualTo(CachedBlockAvailability.InUse));
            Assert.That(owner.Drives.Single().State, Is.EqualTo(DriveState.Ready));
            Assert.That(owner.Root('X').IsDirectory, Is.True);
        }
        var retry = MftIndexCacheMaintenance.Clear(files.Home);
        Assert.That(retry.Succeeded, Is.True);
        Assert.That(retry.Inventory, Is.Empty);
        Assert.That(File.Exists(files.OwnedPath + ".lock"), Is.True);
        Assert.That(MftIndexCacheMaintenance.Clear(files.Home).Deletions, Is.Empty);
    }

    [Test]
    public void AbsentCacheDoesNotCreateAnIndexDirectory()
    {
        using var files = new PackedIndexFixture();
        var result = MftIndexCacheMaintenance.Clear(files.DirectoryPath);
        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.Inventory, Is.Empty);
        Assert.That(result.Deletions, Is.Empty);
        Assert.That(Directory.Exists(MftIndexCachePolicy.GetCacheRoot(files.DirectoryPath)), Is.False);
        Assert.That(result.ToClearText(), Does.Contain("No canonical cached blocks remain"));
    }

    [TestCase(CachedBlockDeletionOutcome.Deleted, null, true)]
    [TestCase(CachedBlockDeletionOutcome.InUse, null, false)]
    [TestCase(CachedBlockDeletionOutcome.Failed, "permission denied", false)]
    public void DisplayPreservesPerDriveOutcome(CachedBlockDeletionOutcome outcome,
        string? reason, bool succeeded)
    {
        using var files = new IndexCacheFixture();
        var file = CacheDirectory.InspectCached(files.Cache).Single().File;
        var result = new MftIndexCacheMaintenanceResult(
            [SyntheticNotifications.CreateCachedBlockDeletionResult(file, outcome, reason)], [], []);
        Assert.That(result.Succeeded, Is.EqualTo(succeeded));
        Assert.That(result.ToClearText(), Does.Contain("Y: (00000001)"));
        Assert.That(result.ToClearText(), Does.Contain(outcome.ToString()));
        Assert.That(result.ToClearText(), Does.Contain(file.Path));
        if (reason is not null)
        {
            Assert.That(result.ToClearText(), Does.Contain(reason));
        }
    }

    [Test]
    public void InspectionErrorDoesNotClaimAnEmptyCache()
    {
        var result = new MftIndexCacheMaintenanceResult([], [], ["inspection denied"]);
        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.ToClearText(), Does.Contain("inspection denied"));
        Assert.That(result.ToClearText(), Does.Not.Contain("No canonical cached blocks remain"));
    }

    [Test, Platform("Win")]
    public void ReadOnlyBlockReportsFailureAndRetainsIt()
    {
        using var files = new IndexCacheFixture();
        File.SetAttributes(files.FreePath, FileAttributes.ReadOnly);
        try
        {
            var result = MftIndexCacheMaintenance.Clear(files.Home);
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Deletions.Single().Outcome, Is.EqualTo(CachedBlockDeletionOutcome.Failed));
            Assert.That(result.Deletions.Single().FailureReason, Is.Not.Null.And.Not.Empty);
            Assert.That(result.Inventory.Single().File.Path, Is.EqualTo(files.FreePath));
        }
        finally
        {
            File.SetAttributes(files.FreePath, FileAttributes.Normal);
        }
    }
}

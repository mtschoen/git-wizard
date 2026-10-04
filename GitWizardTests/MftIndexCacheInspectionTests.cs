using GitWizard;
using GitWizardTests.TestSupport;
using MFTLib.Index;

namespace GitWizardTests;

public class MftIndexCacheInspectionTests
{
    string? _home;

    [SetUp]
    public void SetUp()
    {
        _home = TestUtilities.RedirectLocalFilesToTemp();
    }

    [TearDown]
    public void TearDown()
    {
        GitWizardLog.CloseCurrentLogFile();
        TestUtilities.ClearLocalFilesRedirect(_home);
    }

    static MftIndexCacheInspection Create() => new(new FrozenCacheClock(), true,
        MftIndexCacheInspection.InspectDirectory);

    static Task<FileIndex> Open(PackedIndexFixture files, string cache, bool cacheOnly = false) =>
        FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = [new IndexedDrive('X', files.TreePath, 1)],
            CacheDirectory = cache,
            ProducerPolicy = ProducerPolicy.Enumeration,
            InitialOpenCacheOnly = cacheOnly
        }, CancellationToken.None);

    [Test]
    public void MissingInventoryDoesNotCreateCache()
    {
        using var files = new PackedIndexFixture();
        var snapshot = Create().Capture(files.DirectoryPath);
        Assert.That(snapshot.Inventory, Is.Empty);
        Assert.That(snapshot.Errors, Is.Empty);
        Assert.That(Directory.Exists(snapshot.CacheDirectory), Is.False);
    }

    [Test]
    public void NonWindowsDoesNotInvokeInspection()
    {
        using var files = new PackedIndexFixture();
        var service = new MftIndexCacheInspection(new FrozenCacheClock(), false,
            _ => throw new AssertionException("Non-Windows inspected a Windows cache"));
        var snapshot = service.Capture(files.DirectoryPath);
        Assert.That(snapshot.IsApplicable, Is.False);
        Assert.That(snapshot.Inventory, Is.Empty);
        Assert.That(Directory.Exists(snapshot.CacheDirectory), Is.False);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void InspectionExceptionsAreVisible(bool denied)
    {
        using var files = new PackedIndexFixture();
        var service = new MftIndexCacheInspection(new FrozenCacheClock(), true, _ =>
        {
            if (denied)
                throw new UnauthorizedAccessException("denied cache");
            throw new IOException("unreadable cache");
        });
        var snapshot = service.Capture(files.DirectoryPath);
        Assert.That(snapshot.Succeeded, Is.False);
        Assert.That(snapshot.Errors.Single(), Does.Contain(denied ? "denied cache" : "unreadable cache"));
        Assert.That(MftIndexCacheStatusFormatter.Format(snapshot), Does.Contain("Error:"));
    }

    [Test]
    public void FileAtPolicyPathIsAnErrorNotEmptyInventory()
    {
        using var files = new PackedIndexFixture();
        var cache = MftIndexCachePolicy.GetCacheDirectory(files.DirectoryPath);
        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        File.WriteAllText(cache, "not a directory");
        Assert.That(Create().Capture(files.DirectoryPath).Succeeded, Is.False);
    }

    [Test]
    public async Task AvailableInvalidAndInUseAreDifferentAndContentIsUnchanged()
    {
        using var files = new PackedIndexFixture();
        var cache = MftIndexCachePolicy.GetCacheDirectory(files.DirectoryPath);
        await using (var seed = await Open(files, cache))
            Assert.That(seed.Drives.Single().CacheSlot, Is.EqualTo(CacheSlotState.OwnedCanonical));
        var file = CacheDirectory.EnumerateCached(cache).Single();
        var before = await File.ReadAllBytesAsync(file.Path);
        var available = Create().Capture(files.DirectoryPath);
        Assert.That(available.Inventory.Single().Availability, Is.EqualTo(CachedBlockAvailability.Available));
        Assert.That(await File.ReadAllBytesAsync(file.Path), Is.EqualTo(before));
        Assert.That(File.Exists(file.Path + ".lock"), Is.True);

        await using (var owner = await Open(files, cache))
        {
            var inUse = Create().Capture(files.DirectoryPath);
            Assert.That(inUse.Inventory.Single().Availability, Is.EqualTo(CachedBlockAvailability.InUse));
            await using var refused = await Open(files, cache, cacheOnly: true);
            Assert.That(refused.Drives.Single().FailureKind, Is.EqualTo(DriveFailureKind.InUse));
            Assert.That(owner.Root('X').IsDirectory, Is.True);
        }
        Assert.That(await File.ReadAllBytesAsync(file.Path), Is.EqualTo(before));
        await File.WriteAllTextAsync(file.Path, "invalid block fixture");
        var invalid = Create().Capture(files.DirectoryPath).Inventory.Single();
        Assert.That(invalid.Availability, Is.EqualTo(CachedBlockAvailability.Invalid));
        Assert.That(invalid.Validation, Is.Not.Null);
    }

    [Test]
    public async Task ConcurrentNormalOpenIsPrivateWhileCacheOnlyRefuses()
    {
        using var files = new PackedIndexFixture();
        var cache = MftIndexCachePolicy.GetCacheDirectory(files.DirectoryPath);
        var index = await Open(files, cache);
        var ui = new MftIndexSession(index, null, ["X"],
            [new IndexedDrive('X', files.TreePath, 1)], new Dictionary<string, string>(), cache);
        await using var cli = await Open(files, cache);
        Assert.That(cli.Drives.Single().CacheSlot, Is.EqualTo(CacheSlotState.PrivateFallback));
        Assert.That(cli.Drives.Single().BlockSource, Is.EqualTo(BlockSource.ProducedByScan));
        var snapshot = Create().Capture(files.DirectoryPath, [ui.CaptureCacheStatus()!]);
        Assert.That(snapshot.Inventory.Single().Availability, Is.EqualTo(CachedBlockAvailability.InUse));
        Assert.That(MftIndexCacheStatusFormatter.Format(snapshot), Does.Contain("this session owns the cache"));
        await using var refused = await Open(files, cache, cacheOnly: true);
        Assert.That(refused.Drives.Single().State, Is.EqualTo(DriveState.Failed));
        Assert.That(refused.Drives.Single().FailureKind, Is.EqualTo(DriveFailureKind.InUse));
        Assert.That(cli.Root('X').IsDirectory, Is.True);
        Assert.That(index.Root('X').IsDirectory, Is.True);
        await ui.DisposeAsync();
        Assert.That(cli.Drives.Single().CacheSlot, Is.EqualTo(CacheSlotState.PrivateFallback));
        await cli.RescanAsync('X', CancellationToken.None);
        Assert.That(cli.Drives.Single().CacheSlot, Is.EqualTo(CacheSlotState.OwnedCanonical));
    }
}

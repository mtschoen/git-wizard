using GitWizard;
using GitWizardTests.TestSupport;
using MFTLib.Index;
using MFTLibTestExtensions;

namespace GitWizardTests;

public class MftIndexCacheInspectionTests
{
    LocalFilesSandbox? _sandbox;

    [SetUp]
    public void SetUp()
    {
        _sandbox = new LocalFilesSandbox();
    }

    [TearDown]
    public void TearDown()
    {
        GitWizardLog.CloseCurrentLogFile();
        _sandbox?.Dispose();
        _sandbox = null;
    }

    static MftIndexCacheInspection Create() => new(new FrozenCacheClock(), true,
        MftIndexCacheInspection.InspectDirectory);

    static Task<FileIndex> Open(PackedIndexFixture files, string cache, bool cacheOnly = false) =>
        FileIndex.OpenAsync(MFTLibTestExtensions.SyntheticVolumeFacts.Apply(new FileIndexOptions
        {
            Root = (RootKey)new VolumeKey('X'),
            CacheDirectory = cache,
            CacheMode = cacheOnly ? CacheMode.ExistingOnly : CacheMode.Persistent
        } with
        { Producer = ProducerSelection.WalkOnly }, 1, volume: new VolumeKey('X'), volumeRootDirectory: files.TreePath), CancellationToken.None);

    [Test]
    public void MissingInventoryDoesNotCreateCache()
    {
        using var files = new PackedIndexFixture();
        var snapshot = Create().Capture(files.DirectoryPath);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshot.Inventory, Is.Empty);
            Assert.That(snapshot.Errors, Is.Empty);
            Assert.That(Directory.Exists(snapshot.CacheDirectory), Is.False);
        }
    }

    [Test]
    public void NonWindowsDoesNotInvokeInspection()
    {
        using var files = new PackedIndexFixture();
        var service = new MftIndexCacheInspection(new FrozenCacheClock(), false,
            _ => throw new AssertionException("Non-Windows inspected a Windows cache"));
        var snapshot = service.Capture(files.DirectoryPath);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshot.IsApplicable, Is.False);
            Assert.That(snapshot.Inventory, Is.Empty);
            Assert.That(Directory.Exists(snapshot.CacheDirectory), Is.False);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void InspectionExceptionsAreVisible(bool denied)
    {
        using var files = new PackedIndexFixture();
        var service = new MftIndexCacheInspection(new FrozenCacheClock(), true, _ =>
        {
            if (denied)
            {
                throw new UnauthorizedAccessException("denied cache");
            }

            throw new IOException("unreadable cache");
        });
        var snapshot = service.Capture(files.DirectoryPath);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshot.Succeeded, Is.False);
            Assert.That(snapshot.Errors.Single(), Does.Contain(denied ? "denied cache" : "unreadable cache"));
            Assert.That(MftIndexCacheStatusFormatter.Format(snapshot), Does.Contain("Error:"));
        }
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
        {
            Assert.That(seed.Status.Block?.CacheLocation, Is.EqualTo(CacheLocation.OwnedCanonical));
        }

        var file = CacheDirectory.InspectCached(cache).Single().File;
        var before = await File.ReadAllBytesAsync(file.Path);
        var available = Create().Capture(files.DirectoryPath);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(available.Inventory.Single().Availability, Is.EqualTo(CachedBlockAvailability.Available));
            Assert.That(await File.ReadAllBytesAsync(file.Path), Is.EqualTo(before));
            Assert.That(File.Exists(file.Path + ".lock"), Is.True);
        }

        await using (var owner = await Open(files, cache))
        {
            var inUse = Create().Capture(files.DirectoryPath);
            Assert.That(inUse.Inventory.Single().Availability, Is.EqualTo(CachedBlockAvailability.InUse));
            await using var refused = await Open(files, cache, cacheOnly: true);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(refused.Status.Failure?.Kind, Is.EqualTo(RootFailureKind.InUse));
                Assert.That(owner.Root().IsDirectory, Is.True);
            }
        }
        Assert.That(await File.ReadAllBytesAsync(file.Path), Is.EqualTo(before));
        await File.WriteAllTextAsync(file.Path, "invalid block fixture");
        var invalid = Create().Capture(files.DirectoryPath).Inventory.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(invalid.Availability, Is.EqualTo(CachedBlockAvailability.Invalid));
            Assert.That(invalid.Validation, Is.Not.Null);
        }
    }

    [Test]
    public async Task InventoryReportsTheStoredCacheTagNextToTheRequestedOne()
    {
        using var files = new PackedIndexFixture();
        var cache = MftIndexCachePolicy.GetCacheDirectory(files.DirectoryPath);
        var stored = new CacheTag("OLDT", 7);
        SyntheticBlock.WriteCached(cache, new VolumeKey('X'), 1, new SyntheticBlockOptions { CacheTag = stored },
            [new SyntheticRow(5, "root", 5) { IsDirectory = true }]);
        await File.WriteAllTextAsync(SyntheticBlock.CachedPath(cache, new VolumeKey('Y'), 1), "invalid block fixture");

        var snapshot = Create().Capture(files.DirectoryPath);

        var available = snapshot.Inventory.Single(row => row.File.Root == new VolumeKey('X'));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(available.Availability, Is.EqualTo(CachedBlockAvailability.Available));
            Assert.That(available.CacheTag, Is.EqualTo(stored));
            Assert.That(snapshot.RequestedCacheTag, Is.EqualTo(MftIndexCachePolicy.CacheTag));
        }
        var lines = MftIndexCacheStatusFormatter.Format(snapshot).Split(Environment.NewLine)
            .Where(line => line.StartsWith("  Cache tag:", StringComparison.Ordinal)).ToArray();
        Assert.That(lines, Is.EqualTo([
            $"  Cache tag: stored {stored}; requested {MftIndexCachePolicy.CacheTag}",
            $"  Cache tag: stored unavailable; requested {MftIndexCachePolicy.CacheTag}"
        ]));
    }

    [Test]
    public async Task ConcurrentNormalOpenIsPrivateWhileCacheOnlyRefuses()
    {
        using var files = new PackedIndexFixture();
        var cache = MftIndexCachePolicy.GetCacheDirectory(files.DirectoryPath);
        var index = await Open(files, cache);
        var ui = new MftIndexSession([index], null, ["X"], new Dictionary<string, string>(), cache);
        await using var cli = await Open(files, cache);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cli.Status.Block?.CacheLocation, Is.EqualTo(CacheLocation.PrivateFallback));
            Assert.That(cli.Status.Block?.Origin, Is.EqualTo(BlockOrigin.ProducedByScan));
        }
        var snapshot = Create().Capture(files.DirectoryPath, [ui.CaptureCacheStatus()!]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshot.Inventory.Single().Availability, Is.EqualTo(CachedBlockAvailability.InUse));
            Assert.That(MftIndexCacheStatusFormatter.Format(snapshot), Does.Contain("this session owns the cache"));
        }
        await using var refused = await Open(files, cache, cacheOnly: true);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(refused.Status.State, Is.EqualTo(RootState.Failed));
            Assert.That(refused.Status.Failure?.Kind, Is.EqualTo(RootFailureKind.InUse));
            Assert.That(cli.Root().IsDirectory, Is.True);
            Assert.That(index.Root().IsDirectory, Is.True);
        }
        await ui.DisposeAsync();
        Assert.That(cli.Status.Block?.CacheLocation, Is.EqualTo(CacheLocation.PrivateFallback));
        await cli.RescanAsync(CancellationToken.None);
        Assert.That(cli.Status.Block?.CacheLocation, Is.EqualTo(CacheLocation.OwnedCanonical));
    }
}

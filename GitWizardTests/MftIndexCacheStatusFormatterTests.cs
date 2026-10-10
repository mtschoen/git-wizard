using GitWizard;
using GitWizardTests.TestSupport;
using MFTLib.Index;

namespace GitWizardTests;

public class MftIndexCacheStatusFormatterTests
{
    [TestCase(30, "30s")]
    [TestCase(120, "2m")]
    [TestCase(7200, "2h")]
    [TestCase(172800, "2d")]
    [TestCase(-60, "in the future (clock difference)")]
    public void AgeIsFrozen(int seconds, string expected) => Assert.That(
        MftIndexCacheStatusFormatter.Age(FrozenCacheClock.Now.UtcDateTime.AddSeconds(-seconds),
            FrozenCacheClock.Now), Is.EqualTo(expected));

    [Test]
    public void UnknownTimestampIsNotZeroAge() => Assert.That(
        MftIndexCacheStatusFormatter.Age(default, FrozenCacheClock.Now), Is.EqualTo("unavailable"));

    [TestCase(null, "all eligible records")]
    [TestCase(new string[] { }, "directories only")]
    [TestCase(new[] { ".git", "HEAD" }, "directories plus .git, HEAD")]
    public void RetentionDescribesFullDirectoryOnlyAndNamedScans(string[]? names, string expected)
    {
        var snapshot = new MftIndexCacheSnapshot { IsApplicable = true, DirectoryScanFileNames = names };
        Assert.That(MftIndexCacheStatusFormatter.Format(snapshot), Does.Contain($"Retention: {expected}"));
    }

    [TestCase(CacheSlotState.OwnedCanonical, "this session owns the cache")]
    [TestCase(CacheSlotState.PrivateFallback, "running on a private block; another process holds the cache")]
    [TestCase(CacheSlotState.NotApplicable, "")]
    public void BackingUsesLibraryField(CacheSlotState state, string expected) => Assert.That(
        MftIndexCacheStatusFormatter.Backing(state), Is.EqualTo(expected));

    [TestCase(1u, true, true)]
    [TestCase(2u, true, false)]
    [TestCase(1u, false, false)]
    public async Task OwnershipRequiresDirectoryAndSerial(uint serial, bool sameDirectory, bool owned)
    {
        using var files = new PackedIndexFixture();
        var cache = MftIndexCachePolicy.GetCacheDirectory(files.DirectoryPath);
        await using var index = await FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = [new IndexedDrive('X', files.TreePath, 1)],
            CacheDirectory = cache,
            ProducerPolicy = ProducerPolicy.Enumeration
        }, CancellationToken.None);
        var active = new MftActiveCacheSession(sameDirectory ? cache : files.BlockDirectory,
            [new(new MftDriveStatus("X", index.Drives.Single(), null), serial)]);
        var service = new MftIndexCacheInspection(new FrozenCacheClock(), true,
            MftIndexCacheInspection.InspectDirectory);
        var text = MftIndexCacheStatusFormatter.Format(service.Capture(files.DirectoryPath, [active]));
        var inventoryLine = text.Split(Environment.NewLine).Single(line => line.StartsWith("Drive X:", StringComparison.Ordinal));
        Assert.That(inventoryLine.Contains("this session owns the cache", StringComparison.Ordinal), Is.EqualTo(owned));
        Assert.That(text, Does.Contain("Directory-entry last-write UTC:"));
        Assert.That(text, Does.Contain("Size:"));
        Assert.That(text, Does.Contain("serial 00000001"));
        Assert.That(text, Does.Contain("Retention: directories plus .git, HEAD"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task WatchFailureIsSeparateFromOpenFailure(bool refused)
    {
        using var files = new PackedIndexFixture();
        await using var index = await files.OpenIndexAsync();
        var drive = index.Drives.Single();
        var status = drive with
        {
            State = refused ? DriveState.Failed : DriveState.Ready,
            FailureKind = refused ? DriveFailureKind.InUse : DriveFailureKind.None,
            Block = drive.Block with { CacheSlot = CacheSlotState.NotApplicable, ScanTimestamp = default },
            Watch = drive.Watch with { FailureMessage = refused ? null : "watch stopped" }
        };
        var directory = MftIndexCachePolicy.GetCacheDirectory(files.DirectoryPath);
        var active = new MftActiveCacheSession(directory,
            [new(new MftDriveStatus("X", status, null), 1)]);
        var snapshot = new MftIndexCacheInspection(new FrozenCacheClock(), true,
            _ => Array.Empty<CachedBlockStatus>()).Capture(files.DirectoryPath, [active]);
        var text = MftIndexCacheStatusFormatter.Format(snapshot);
        Assert.That(text, Does.Contain(refused ? "Open/rescan failure: InUse" : "State: Ready"));
        if (!refused)
        {
            Assert.That(text, Does.Contain("Open/rescan failure: None"));
            Assert.That(text, Does.Contain("Watch failure: watch stopped"));
        }
        Assert.That(text, Does.Contain("Scan timestamp UTC: unavailable"));
        Assert.That(text, Does.Not.Contain("  Backing:"));
    }
}

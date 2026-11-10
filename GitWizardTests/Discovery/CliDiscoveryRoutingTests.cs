using System.Runtime.ExceptionServices;
using GitWizard.Watch;
using GitWizardTests.TestSupport;
using MFTLib.Index;

namespace GitWizardTests.Discovery;

[TestFixture]
[NonParallelizable]
public class CliDiscoveryRoutingTests
{
    static DriveInfo[] EnumerateWorkerDrives(
        ref int enumerations,
        DriveInfo[] initialSnapshot,
        ManualResetEventSlim ownEntered,
        ManualResetEventSlim peerEntered)
    {
        if (Interlocked.Increment(ref enumerations) != 1)
        {
            return DriveInfo.GetDrives();
        }

        ownEntered.Set();
        if (!peerEntered.Wait(TestTimeouts.HangGuard))
        {
            throw new TimeoutException("Timed out waiting for peer drive enumeration.");
        }

        return initialSnapshot;
    }

    static Thread StartMappingWorker(
        string backingPath,
        DriveInfo[] initialSnapshot,
        ManualResetEventSlim ownEntered,
        ManualResetEventSlim peerEntered,
        out Func<(string? Drive, Exception? Failure)> getResult)
    {
        string? mappedDrive = null;
        Exception? failure = null;
        var enumerations = 0;
        var thread = new Thread(() =>
        {
            try
            {
                mappedDrive = CliDiscoveryFixture.MapSubstDrive(
                    backingPath,
                    () => EnumerateWorkerDrives(ref enumerations, initialSnapshot, ownEntered, peerEntered));
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                ownEntered.Set();
            }
        })
        {
            IsBackground = true
        };
        thread.Start();
        getResult = () => (mappedDrive, failure);
        return thread;
    }

    [SetUp]
    public void SetUp()
    {
        VolumeChangeSourceFactory._isElevatedOverride = () => true;
    }

    [TearDown]
    public void TearDown() => VolumeChangeSourceFactory.ResetToDefaults();

    [Test]
    public void MappingFailure_RestoresSandbox()
    {
        var previousHome = Environment.GetEnvironmentVariable("GITWIZARD_HOME");
        string? failedHome = null;
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
            {
                _ = new CliDiscoveryFixture(
                    "report",
                    false,
                    () =>
                    {
                        failedHome = Environment.GetEnvironmentVariable("GITWIZARD_HOME");
                        throw new InvalidOperationException("Synthetic mapping failure");
                    });
            });
            using (Assert.EnterMultipleScope())
            {
                Assert.That(failedHome, Is.Not.Null);
                Assert.That(Environment.GetEnvironmentVariable("GITWIZARD_HOME"), Is.EqualTo(previousHome));
                Assert.That(Directory.Exists(failedHome), Is.False);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITWIZARD_HOME", previousHome);
            TestUtilities.ResetStaticCaches();
            if (failedHome is not null && failedHome != previousHome)
            {
                TempDirectory.DeleteTree(failedHome);
            }
        }
    }

    [Test]
    public void NoFreeDriveLetters_ThrowsWithoutMapping()
    {
        var enumerations = 0;
        var allOccupied = Enumerable.Range('A', 26)
            .Select(letter => new DriveInfo($"{(char)letter}:\\"))
            .ToArray();
        using var directory = new TempDirectory("gw-subst-nofree-");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            CliDiscoveryFixture.MapSubstDrive(
                directory.Path,
                () =>
                {
                    enumerations++;
                    return allOccupied;
                }));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception!.Message, Does.Contain("could not find a free drive letter from Z: through A:"));
            Assert.That(enumerations, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task ConcurrentMappings_SameInitialDrive_BothRemainUsable()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Ignore("Windows subst drive mapping is Windows-only.");
        }

        var initialSnapshot = DriveInfo.GetDrives();
        var occupiedCount = initialSnapshot
            .Select(drive => char.ToUpperInvariant(drive.Name[0]))
            .Where(letter => letter is >= 'A' and <= 'Z')
            .Distinct()
            .Count();
        if (26 - occupiedCount < 2)
        {
            Assert.Ignore("Requires at least two free Windows drive letters for concurrent subst mappings.");
        }

        using var firstDirectory = new TempDirectory("gw-subst-concurrent-a-");
        using var secondDirectory = new TempDirectory("gw-subst-concurrent-b-");
        using var replacementDirectory = new TempDirectory("gw-subst-replacement-");
        Directory.CreateDirectory(Path.Combine(firstDirectory.Path, "shared"));
        Directory.CreateDirectory(Path.Combine(secondDirectory.Path, "shared"));
        await File.WriteAllTextAsync(Path.Combine(firstDirectory.Path, "shared", "marker.txt"), "alpha");
        await File.WriteAllTextAsync(Path.Combine(secondDirectory.Path, "shared", "marker.txt"), "bravo");
        await File.WriteAllTextAsync(Path.Combine(replacementDirectory.Path, "marker.txt"), "replacement");

        using var firstEntered = new ManualResetEventSlim();
        using var secondEntered = new ManualResetEventSlim();
        Func<(string? Drive, Exception? Failure)> getFirstResult = static () => (null, null);
        Func<(string? Drive, Exception? Failure)> getSecondResult = static () => (null, null);
        string? firstDrive = null;
        string? secondDrive = null;
        string? replacementDrive = null;
        try
        {
            var firstThread = StartMappingWorker(
                firstDirectory.Path, initialSnapshot, firstEntered, secondEntered, out getFirstResult);
            var secondThread = StartMappingWorker(
                secondDirectory.Path, initialSnapshot, secondEntered, firstEntered, out getSecondResult);

            var firstJoined = firstThread.Join(TestTimeouts.HangGuard);
            secondEntered.Set();
            var secondJoined = secondThread.Join(TestTimeouts.HangGuard);
            firstEntered.Set();

            var (firstMapped, firstFailure) = getFirstResult();
            var (secondMapped, secondFailure) = getSecondResult();
            firstDrive = firstMapped;
            secondDrive = secondMapped;

            Assert.That(firstJoined && secondJoined, Is.True, "Concurrent mapping workers timed out.");
            var failures = new[] { firstFailure, secondFailure }.OfType<Exception>().ToArray();
            if (failures.Length == 1)
            {
                ExceptionDispatchInfo.Capture(failures[0]).Throw();
            }

            if (failures.Length > 1)
            {
                throw new AggregateException(failures);
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(firstDrive, Is.Not.Null.And.Not.Empty);
                Assert.That(secondDrive, Is.Not.Null.And.Not.Empty);
                Assert.That(secondDrive, Is.Not.EqualTo(firstDrive).IgnoreCase);
                Assert.That(await File.ReadAllTextAsync(Path.Combine($@"{firstDrive}\shared", "marker.txt")), Is.EqualTo("alpha"));
                Assert.That(await File.ReadAllTextAsync(Path.Combine($@"{secondDrive}\shared", "marker.txt")), Is.EqualTo("bravo"));
            }

            await CliDiscoveryFixture.UnmapSubstDriveAsync(firstDrive!);
            var releasedDrive = firstDrive;
            firstDrive = null;
            getFirstResult = static () => (null, null);
            var otherLetters = Enumerable.Range('A', 26)
                .Where(letter => letter != releasedDrive![0])
                .Select(letter => new DriveInfo($"{(char)letter}:\\"))
                .ToArray();
            replacementDrive = CliDiscoveryFixture.MapSubstDrive(replacementDirectory.Path, () => otherLetters);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(replacementDrive, Is.EqualTo(releasedDrive));
                Assert.That(await File.ReadAllTextAsync($@"{replacementDrive}\marker.txt"), Is.EqualTo("replacement"));
                Assert.That(await File.ReadAllTextAsync(Path.Combine($@"{secondDrive}\shared", "marker.txt")), Is.EqualTo("bravo"));
            }
        }
        finally
        {
            firstEntered.Set();
            secondEntered.Set();
            try
            {
                firstDrive ??= getFirstResult().Drive;
                secondDrive ??= getSecondResult().Drive;
                if (firstDrive is not null)
                {
                    await CliDiscoveryFixture.UnmapSubstDriveAsync(firstDrive);
                }

                if (secondDrive is not null)
                {
                    await CliDiscoveryFixture.UnmapSubstDriveAsync(secondDrive);
                }
            }
            finally
            {
                if (replacementDrive is not null)
                {
                    try
                    {
                        Assert.That(await File.ReadAllTextAsync($@"{replacementDrive}\marker.txt"), Is.EqualTo("replacement"));
                    }
                    finally
                    {
                        await CliDiscoveryFixture.UnmapSubstDriveAsync(replacementDrive);
                    }
                }
            }
        }
    }

    [TestCase("report")]
    [TestCase("scan")]
    [TestCase("sweep")]
    [TestCase("tags")]
    [TestCase("branches")]
    [TestCase("watch")]
    public async Task WindowsEntryPointDoesNotCrawlNtfs(string command)
    {
        await using var fixture = new CliDiscoveryFixture(command);
        fixture.Dependencies.CrawlGuard = root => throw new AssertionException($"Crawler invoked unexpectedly for {root}");
        await fixture.RunAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(fixture.Dependencies.CrawledRoots, Is.Empty);
            Assert.That(fixture.OpenedDriveSets, Is.Not.Empty);
        }
        foreach (var drives in fixture.OpenedDriveSets)
        {
            Assert.That(drives, Is.EquivalentTo(fixture.Configuration.SearchPaths).IgnoreCase);
        }
    }

    [TestCase("report")]
    [TestCase("scan")]
    [TestCase("sweep")]
    [TestCase("tags")]
    [TestCase("branches")]
    [TestCase("watch")]
    public async Task WindowsNtfsOnly_DoesNotInvokeCrawler(string command)
    {
        await using var fixture = new CliDiscoveryFixture(command);
        fixture.Configuration.SearchPaths.Clear();
        fixture.Configuration.SearchPaths.Add(fixture.Dependencies.IndexFixture.TreePath);
        fixture.Dependencies.CrawlGuard = root => throw new AssertionException($"Crawler invoked unexpectedly for {root}");

        await fixture.RunAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fixture.Dependencies.CrawledRoots, Is.Empty);
            Assert.That(fixture.OpenedDriveSets, Is.Not.Empty);
        }
        foreach (var drives in fixture.OpenedDriveSets)
        {
            Assert.That(drives, Is.EquivalentTo(fixture.Configuration.SearchPaths).IgnoreCase);
        }
    }

    [TestCase("report")]
    [TestCase("scan")]
    [TestCase("sweep")]
    [TestCase("tags")]
    [TestCase("branches")]
    [TestCase("watch")]
    public async Task NoMft_OpensUncachedDirectoryIndexes(string command)
    {
        await using var fixture = new CliDiscoveryFixture(command, noMft: true);
        fixture.Configuration.SearchPaths.Clear();
        var treePath = fixture.Dependencies.IndexFixture.TreePath;
        fixture.Configuration.SearchPaths.Add(treePath);

        await fixture.RunAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fixture.Dependencies.CrawledRoots, Is.Empty);
            Assert.That(fixture.OpenedDriveSets, Is.Not.Empty);
            Assert.That(fixture.Dependencies.OpenedCacheModes, Is.All.EqualTo(CacheMode.Disabled));
        }
    }

    [TestCase("sweep")]
    [TestCase("tags")]
    [TestCase("branches")]
    public async Task IncompleteDiscovery_RefusesPublication_Exits3(string command)
    {
        await using var fixture = new CliDiscoveryFixture(command);
        fixture.Configuration.SearchPaths.Clear();
        fixture.Configuration.SearchPaths.Add(fixture.Dependencies.IndexFixture.TreePath);

        var indexFixture = fixture.Dependencies.IndexFixture;
        fixture.Dependencies.CustomOpenIndexAsync = StatusSessions.Opener(indexFixture, ('C', SyntheticDriveBehavior.ProducerFails));

        var exitCode = await fixture.RunAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exitCode, Is.EqualTo(3));
            Assert.That(fixture.Stderr.ToString(), Does.Contain("Drive C failure"));
        }
    }

    [Test]
    public async Task IncompleteDiscovery_Report_KeepsDiagnosticsOffStderr_Exits3()
    {
        await using var fixture = new CliDiscoveryFixture("report");
        fixture.Configuration.SearchPaths.Clear();
        fixture.Configuration.SearchPaths.Add(fixture.Dependencies.IndexFixture.TreePath);

        var indexFixture = fixture.Dependencies.IndexFixture;
        fixture.Dependencies.CustomOpenIndexAsync = StatusSessions.Opener(indexFixture, ('C', SyntheticDriveBehavior.ProducerFails));

        var exitCode = await fixture.RunAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exitCode, Is.EqualTo(3));
            Assert.That(fixture.Stdout.ToString(), Does.Contain("Drive C failure"));
            Assert.That(fixture.Stderr.ToString(), Does.Not.Contain("Drive C failure"));
        }
    }

    [Test]
    public async Task ScanOnly_IncompleteDiscovery_Exits1()
    {
        await using var fixture = new CliDiscoveryFixture("scan");
        fixture.Configuration.SearchPaths.Clear();
        fixture.Configuration.SearchPaths.Add(fixture.Dependencies.IndexFixture.TreePath);

        var indexFixture = fixture.Dependencies.IndexFixture;
        fixture.Dependencies.CustomOpenIndexAsync = StatusSessions.Opener(indexFixture, ('C', SyntheticDriveBehavior.ProducerFails));

        var exitCode = await fixture.RunAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exitCode, Is.EqualTo(1));
            Assert.That(fixture.Stderr.ToString(), Does.Contain("Drive C failure"));
        }
    }
}

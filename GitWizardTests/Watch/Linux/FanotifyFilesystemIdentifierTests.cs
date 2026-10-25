using System.Runtime.Versioning;
using GitWizard.Watch;
using GitWizard.Watch.Linux;
using GitWizardTests.TestSupport;
using static GitWizardTests.Watch.Linux.FanotifyEventBuffers;

namespace GitWizardTests.Watch.Linux;

[TestFixture]
[SupportedOSPlatform("linux")]
public class FanotifyFilesystemIdentifierTests
{
    const long FirstFilesystem = 1;
    const long SecondFilesystem = 2;
    const long UnderlyingFilesystem = 99;

    string _temporaryRoot = null!;
    string _firstRoot = null!;
    string _secondRoot = null!;
    string _siblingRoot = null!;
    FakeFanotifyNative _native = null!;

    [SetUp]
    public void CreateRootsOnTwoFilesystems()
    {
        _temporaryRoot = Directory.CreateTempSubdirectory("git-wizard-fsid").FullName;
        _firstRoot = Directory.CreateDirectory(Path.Combine(_temporaryRoot, "first")).FullName;
        _secondRoot = Directory.CreateDirectory(Path.Combine(_temporaryRoot, "second")).FullName;
        _siblingRoot = Directory.CreateDirectory(Path.Combine(_temporaryRoot, "sibling")).FullName;
        _native = new FakeFanotifyNative { InitializeResult = new FanotifyNativeCallResult(7, 0) };
        _native.FilesystemIdByMountPath[_firstRoot] = FirstFilesystem;
        _native.FilesystemIdByMountPath[_secondRoot] = SecondFilesystem;
        _native.FilesystemIdByMountPath[_siblingRoot] = FirstFilesystem;
    }

    [TearDown]
    public void DeleteTemporaryRoot() => Directory.Delete(_temporaryRoot, true);

    static byte[] CreateEvents(params (long FilesystemId, byte[] Handle, string Name)[] events) =>
        CombineBuffers(events
            .Select(item => CreateSyntheticEventBuffer(
                FanotifyConstants.Create, item.Handle, item.Name, filesystemId: item.FilesystemId))
            .ToArray());

    static async Task<List<VolumeChangeEntry>> ReadEntriesAsync(
        FanotifyVolumeChangeSource source, int expectedCount, int timeoutSeconds = 5)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        var entries = new List<VolumeChangeEntry>();
        try
        {
            await foreach (var batch in source.WatchAsync(cancellation.Token))
            {
                entries.AddRange(batch.Entries);
                if (entries.Count >= expectedCount)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Fewer entries than expected arrived before the deadline; the caller asserts on what was delivered.
        }

        return entries;
    }

    [Test]
    public async Task SamePayloadOnDifferentFilesystems_ResolvesThroughTheMatchingRoots()
    {
        var handle = new byte[] { 1, 2, 3, 4 };
        _native.MapHandle(FirstFilesystem, GetFullHandleBytes(handle), 100, "/first/dir");
        _native.MapHandle(SecondFilesystem, GetFullHandleBytes(handle), 200, "/second/dir");
        _native.ReadQueue.Enqueue(CreateEvents(
            (FirstFilesystem, handle, "a.txt"), (SecondFilesystem, handle, "a.txt")));
        var source = new FanotifyVolumeChangeSource(_native);
        await source.ArmAndCatchUpAsync(new[] { _firstRoot, _secondRoot }, CancellationToken.None);

        var entries = await ReadEntriesAsync(source, 2);

        Assert.Multiple(() =>
        {
            Assert.That(entries.Select(entry => entry.FullPath), Is.EqualTo(new[]
            {
                Path.Combine("/first/dir", "a.txt"), Path.Combine("/second/dir", "a.txt")
            }));
            Assert.That(_native.OpenByHandleRoots, Is.EqualTo(new[] { _firstRoot, _secondRoot }));
            Assert.That(_native.OpenByHandleCalls.Select(call => call.MountDescriptor),
                Does.Not.Contain(FanotifyConstants.CurrentWorkingDirectory));
        });
    }

    [Test]
    public async Task SamePayloadOnDifferentFilesystems_CachesOneEntryPerFilesystem()
    {
        var handle = new byte[] { 1, 2, 3, 4 };
        _native.MapHandle(FirstFilesystem, GetFullHandleBytes(handle), 100, "/first/dir");
        _native.MapHandle(SecondFilesystem, GetFullHandleBytes(handle), 200, "/second/dir");
        _native.ReadQueue.Enqueue(CreateEvents((FirstFilesystem, handle, "a.txt")));
        _native.ReadQueue.Enqueue(CreateEvents((SecondFilesystem, handle, "a.txt")));
        _native.ReadQueue.Enqueue(CreateEvents((SecondFilesystem, handle, "b.txt"), (FirstFilesystem, handle, "b.txt")));
        var source = new FanotifyVolumeChangeSource(_native);
        await source.ArmAndCatchUpAsync(new[] { _firstRoot, _secondRoot }, CancellationToken.None);

        var entries = await ReadEntriesAsync(source, 4);

        Assert.Multiple(() =>
        {
            Assert.That(entries.Select(entry => entry.FullPath), Is.EqualTo(new[]
            {
                Path.Combine("/first/dir", "a.txt"), Path.Combine("/second/dir", "a.txt"),
                Path.Combine("/second/dir", "b.txt"), Path.Combine("/first/dir", "b.txt")
            }));
            Assert.That(_native.OpenByHandleCalls, Has.Count.EqualTo(2), "The third read is served from the cache.");
        });
    }

    [Test]
    public async Task EventFromUnmarkedFilesystem_IsSkippedWithoutResolving()
    {
        var handle = new byte[] { 5, 6, 7, 8 };
        _native.MapHandle(FirstFilesystem, GetFullHandleBytes(handle), 100, "/first/dir");
        _native.ReadQueue.Enqueue(CreateEvents((99, handle, "stray.txt")));
        _native.ReadQueue.Enqueue(CreateEvents((FirstFilesystem, handle, "kept.txt")));
        var source = new FanotifyVolumeChangeSource(_native);
        await source.ArmAndCatchUpAsync(new[] { _firstRoot }, CancellationToken.None);

        var entries = await ReadEntriesAsync(source, 1, timeoutSeconds: 1);

        Assert.Multiple(() =>
        {
            Assert.That(entries.Select(entry => entry.FullPath), Is.EqualTo(new[] { Path.Combine("/first/dir", "kept.txt") }));
            Assert.That(_native.OpenByHandleCalls, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task ArmedWatch_HoldsNoMountDescriptorBetweenBatches()
    {
        var handle = new byte[] { 1, 2, 3, 4 };
        _native.MapHandle(FirstFilesystem, GetFullHandleBytes(handle), 100, "/first/dir");
        _native.MapHandle(SecondFilesystem, GetFullHandleBytes(handle), 200, "/second/dir");
        _native.ReadQueue.Enqueue(CreateEvents((FirstFilesystem, handle, "a.txt"), (SecondFilesystem, handle, "a.txt")));
        var source = new FanotifyVolumeChangeSource(_native);

        var result = await source.ArmAndCatchUpAsync(new[] { _firstRoot, _secondRoot }, CancellationToken.None);
        var openAfterArming = _native.OpenMountDescriptors.ToList();
        var entries = await ReadEntriesAsync(source, 2);

        Assert.Multiple(() =>
        {
            Assert.That(result.Errors, Is.Empty);
            Assert.That(openAfterArming, Is.Empty, "arming validates each root and closes its descriptor");
            Assert.That(entries, Has.Count.EqualTo(2));
            Assert.That(_native.OpenedDirectories.Count, Is.GreaterThan(2), "the batch opened its own descriptors");
            Assert.That(_native.OpenMountDescriptors, Is.Empty, "the batch closed every descriptor it opened");
        });
    }

    [Test]
    public async Task MountThatCannotBeOpened_RecordsAnErrorForThatRootOnly()
    {
        _native.UnopenableMountPaths.Add(_firstRoot);
        var source = new FanotifyVolumeChangeSource(_native);

        var result = await source.ArmAndCatchUpAsync(new[] { _firstRoot, _secondRoot }, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Errors.Keys, Is.EquivalentTo(new[] { _firstRoot }));
            Assert.That(result.Errors[_firstRoot], Does.Contain("Could not open a descriptor"));
        });
    }

    [Test]
    public async Task ArmingAgain_ReopensEachRootAndHoldsNothing()
    {
        var source = new FanotifyVolumeChangeSource(_native);
        await source.ArmAndCatchUpAsync(new[] { _firstRoot }, CancellationToken.None);

        await source.ArmAndCatchUpAsync(new[] { _firstRoot }, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(_native.OpenedDirectories, Has.Count.EqualTo(2));
            Assert.That(_native.OpenMountDescriptors, Is.Empty);
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task EmptyOrFailedRearm_ReleasesPreviousOwnership(bool initializeFails)
    {
        await using var source = new FanotifyVolumeChangeSource(_native);
        await source.ArmAndCatchUpAsync(new[] { _firstRoot }, CancellationToken.None);
        if (initializeFails)
        {
            _native.InitializeResult = new FanotifyNativeCallResult(-1, FanotifyError.NotPermitted);
        }

        var result = await source.ArmAndCatchUpAsync(
            initializeFails ? new[] { _secondRoot } : Array.Empty<string>(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(_native.ClosedDescriptors, Does.Contain(7));
            Assert.That(_native.OpenMountDescriptors, Is.Empty);
            Assert.That(result.Errors.Count, Is.EqualTo(initializeFails ? 1 : 0));
            Assert.That(source.Readiness.Volumes.Select(volume => volume.Volume), Does.Not.Contain(_firstRoot));
        });
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var batch in source.WatchAsync(CancellationToken.None))
            {
                Assert.Fail($"Unexpected batch: {batch}");
            }
        });
    }

    [Test]
    public async Task DetachedMount_ReresolvesThroughASurvivingMountAndNeverEmitsTheDetachedPrefix()
    {
        var directory = new byte[] { 4, 4, 4, 4 };
        _native.MapHandleUnderRoot(FirstFilesystem, GetFullHandleBytes(directory), 100, "dir");
        _native.ReadQueue.Enqueue(CreateEvents((FirstFilesystem, directory, "before.txt")));
        var source = new FanotifyVolumeChangeSource(_native);
        await source.ArmAndCatchUpAsync(new[] { _firstRoot, _siblingRoot }, CancellationToken.None);
        var delivered = (await ReadEntriesAsync(source, 1)).Select(entry => entry.FullPath).ToList();

        _native.GoneMountPaths.Add(_firstRoot);
        _native.ReadQueue.Enqueue(CreateEvents((FirstFilesystem, directory, "after.txt")));
        delivered.AddRange((await ReadEntriesAsync(source, 1)).Select(entry => entry.FullPath));

        Assert.Multiple(() =>
        {
            Assert.That(delivered, Is.EqualTo(new[]
            {
                Path.Combine(_firstRoot, "dir", "before.txt"), Path.Combine(_siblingRoot, "dir", "after.txt")
            }), "a handle seen before the detach resolves again through the surviving mount, not from the cache");
            Assert.That(_native.OpenByHandleRoots, Is.EqualTo(new[] { _firstRoot, _siblingRoot }));
            Assert.That(_native.OpenMountDescriptors, Is.Empty);
        });
    }

    [Test]
    public async Task DetachedMountWithNoSurvivor_DropsEventsAndForgetsCachedHandles()
    {
        var directory = new byte[] { 4, 4, 4, 4 };
        _native.MapHandleUnderRoot(FirstFilesystem, GetFullHandleBytes(directory), 100, "dir");
        _native.ReadQueue.Enqueue(CreateEvents((FirstFilesystem, directory, "before.txt")));
        var source = new FanotifyVolumeChangeSource(_native);
        await source.ArmAndCatchUpAsync(new[] { _firstRoot }, CancellationToken.None);
        var delivered = (await ReadEntriesAsync(source, 1)).Select(entry => entry.FullPath).ToList();

        _native.GoneMountPaths.Add(_firstRoot);
        _native.ReadQueue.Enqueue(CreateEvents((FirstFilesystem, directory, "dropped.txt")));
        delivered.AddRange((await ReadEntriesAsync(source, 1, timeoutSeconds: 1)).Select(entry => entry.FullPath));
        _native.GoneMountPaths.Remove(_firstRoot);
        _native.ReadQueue.Enqueue(CreateEvents((FirstFilesystem, directory, "after.txt")));
        delivered.AddRange((await ReadEntriesAsync(source, 1)).Select(entry => entry.FullPath));

        Assert.Multiple(() =>
        {
            Assert.That(delivered, Is.EqualTo(new[]
            {
                Path.Combine(_firstRoot, "dir", "before.txt"), Path.Combine(_firstRoot, "dir", "after.txt")
            }));
            Assert.That(_native.OpenByHandleRoots, Has.Count.EqualTo(2), "the remounted filesystem resolves afresh");
            Assert.That(_native.OpenMountDescriptors, Is.Empty);
        });
    }

    [Test]
    public async Task UnmountedFilesystem_StopsResolvingItsEventsAndForgetsItsCachedHandles()
    {
        var firstHandle = new byte[] { 1, 1, 1, 1 };
        var secondHandle = new byte[] { 2, 2, 2, 2 };
        _native.MapHandle(FirstFilesystem, GetFullHandleBytes(firstHandle), 100, "/first/dir");
        _native.MapHandle(SecondFilesystem, GetFullHandleBytes(secondHandle), 200, "/second/dir");
        _native.ReadQueue.Enqueue(CreateEvents(
            (FirstFilesystem, firstHandle, "a.txt"), (SecondFilesystem, secondHandle, "a.txt")));
        var source = new FanotifyVolumeChangeSource(_native);
        await source.ArmAndCatchUpAsync(new[] { _firstRoot, _secondRoot }, CancellationToken.None);
        var delivered = (await ReadEntriesAsync(source, 2)).Select(entry => entry.FullPath).ToList();
        _native.GoneMountPaths.Add(_firstRoot);
        _native.ReadQueue.Enqueue(CreateEvents(
            (FirstFilesystem, firstHandle, "b.txt"), (SecondFilesystem, secondHandle, "b.txt")));

        delivered.AddRange((await ReadEntriesAsync(source, 1)).Select(entry => entry.FullPath));

        Assert.Multiple(() =>
        {
            Assert.That(delivered, Is.EqualTo(new[]
            {
                Path.Combine("/first/dir", "a.txt"), Path.Combine("/second/dir", "a.txt"),
                Path.Combine("/second/dir", "b.txt")
            }));
            Assert.That(_native.OpenMountDescriptors, Is.Empty);
        });
    }

    public enum StaleRootScenario
    {
        Unmounted = 0,
        FirstRootRenamed = 1,
        FirstRootRemoved = 2,
        FirstRootOpenFails = 3,
        FirstRootFilesystemIdUnreadable = 4,
        FirstRootOvermounted = 5,
        EveryRootOvermounted = 6,
        OvermountedWithSiblingRemoved = 7,
        OvermountedWithSiblingOpenFailing = 8,
        OvermountedWithSiblingFilesystemIdUnreadable = 9,
        OvermountedThenUncovered = 10,
        OwnedMountDetachedWithSiblingSurviving = 11
    }

    /// <summary>
    /// Each batch opens a descriptor on a root that currently reaches the filesystem. A root that was renamed,
    /// removed, unopenable, overmounted or unmounted is skipped; a sibling root still resolves the event, and
    /// with no reachable root the event is dropped until a root reaches the filesystem again.
    /// </summary>
    [TestCase(StaleRootScenario.Unmounted)]
    [TestCase(StaleRootScenario.FirstRootRenamed)]
    [TestCase(StaleRootScenario.FirstRootRemoved)]
    [TestCase(StaleRootScenario.FirstRootOpenFails)]
    [TestCase(StaleRootScenario.FirstRootFilesystemIdUnreadable)]
    [TestCase(StaleRootScenario.FirstRootOvermounted)]
    [TestCase(StaleRootScenario.EveryRootOvermounted)]
    [TestCase(StaleRootScenario.OvermountedWithSiblingRemoved)]
    [TestCase(StaleRootScenario.OvermountedWithSiblingOpenFailing)]
    [TestCase(StaleRootScenario.OvermountedWithSiblingFilesystemIdUnreadable)]
    [TestCase(StaleRootScenario.OvermountedThenUncovered)]
    [TestCase(StaleRootScenario.OwnedMountDetachedWithSiblingSurviving)]
    public async Task UnreachableRoot_ResolvesThroughAReachableRootOrDropsTheEvent(StaleRootScenario scenario)
    {
        var siblingStillReachable = scenario is StaleRootScenario.FirstRootRenamed
            or StaleRootScenario.FirstRootRemoved
            or StaleRootScenario.FirstRootOpenFails
            or StaleRootScenario.FirstRootFilesystemIdUnreadable
            or StaleRootScenario.FirstRootOvermounted
            or StaleRootScenario.OwnedMountDetachedWithSiblingSurviving;
        var firstHandle = new byte[] { 1, 1, 1, 1 };
        var siblingHandle = new byte[] { 5, 5, 5, 5 };
        var laterHandle = new byte[] { 6, 6, 6, 6 };
        var unmappedHandle = new byte[] { 3, 3, 3, 3 };
        _native.MapHandle(FirstFilesystem, GetFullHandleBytes(firstHandle), 100, "/first/dir");
        _native.MapHandle(FirstFilesystem, GetFullHandleBytes(siblingHandle), 300, "/sibling/dir");
        _native.MapHandle(FirstFilesystem, GetFullHandleBytes(laterHandle), 400, "/later/dir");
        _native.ReadQueue.Enqueue(CreateEvents((FirstFilesystem, firstHandle, "a.txt")));
        var source = new FanotifyVolumeChangeSource(_native);
        await source.ArmAndCatchUpAsync(new[] { _firstRoot, _siblingRoot, _secondRoot }, CancellationToken.None);
        var delivered = (await ReadEntriesAsync(source, 1)).Select(entry => entry.FullPath).ToList();

        ApplyScenario(scenario);
        _native.ReadQueue.Enqueue(CreateEvents((FirstFilesystem, unmappedHandle, "gone.txt")));
        _native.ReadQueue.Enqueue(CreateEvents((FirstFilesystem, siblingHandle, "b.txt")));
        delivered.AddRange((await ReadEntriesAsync(source, 1, siblingStillReachable ? 5 : 1)).Select(entry => entry.FullPath));
        if (scenario == StaleRootScenario.OvermountedThenUncovered)
        {
            _native.FilesystemIdByMountPath[_firstRoot] = FirstFilesystem;
            _native.ReadQueue.Enqueue(CreateEvents((FirstFilesystem, laterHandle, "c.txt")));
            delivered.AddRange((await ReadEntriesAsync(source, 1)).Select(entry => entry.FullPath));
        }

        var expected = new List<string> { Path.Combine("/first/dir", "a.txt") };
        if (siblingStillReachable)
        {
            expected.Add(Path.Combine("/sibling/dir", "b.txt"));
        }

        if (scenario == StaleRootScenario.OvermountedThenUncovered)
        {
            expected.Add(Path.Combine("/later/dir", "c.txt"));
        }

        Assert.Multiple(() =>
        {
            Assert.That(delivered, Is.EqualTo(expected));
            Assert.That(_native.OpenMountDescriptors, Is.Empty);
        });
    }

    void ApplyScenario(StaleRootScenario scenario)
    {
        switch (scenario)
        {
            case StaleRootScenario.Unmounted:
                _native.GoneMountPaths.Add(_firstRoot);
                _native.GoneMountPaths.Add(_siblingRoot);
                break;
            case StaleRootScenario.OwnedMountDetachedWithSiblingSurviving:
                _native.GoneMountPaths.Add(_firstRoot);
                break;
            case StaleRootScenario.FirstRootRenamed:
                Directory.Move(_firstRoot, _firstRoot + "-renamed");
                break;
            case StaleRootScenario.FirstRootRemoved:
                Directory.Delete(_firstRoot);
                break;
            case StaleRootScenario.FirstRootOpenFails:
                _native.UnopenableMountPaths.Add(_firstRoot);
                break;
            case StaleRootScenario.FirstRootFilesystemIdUnreadable:
                _native.UnreadableIdentifierMountPaths.Add(_firstRoot);
                break;
            case StaleRootScenario.EveryRootOvermounted:
                _native.FilesystemIdByMountPath[_firstRoot] = UnderlyingFilesystem;
                _native.FilesystemIdByMountPath[_siblingRoot] = UnderlyingFilesystem;
                break;
            case StaleRootScenario.OvermountedWithSiblingRemoved:
                _native.FilesystemIdByMountPath[_firstRoot] = UnderlyingFilesystem;
                Directory.Delete(_siblingRoot);
                break;
            case StaleRootScenario.OvermountedWithSiblingOpenFailing:
                _native.FilesystemIdByMountPath[_firstRoot] = UnderlyingFilesystem;
                _native.UnopenableMountPaths.Add(_siblingRoot);
                break;
            case StaleRootScenario.OvermountedWithSiblingFilesystemIdUnreadable:
                _native.FilesystemIdByMountPath[_firstRoot] = UnderlyingFilesystem;
                _native.UnreadableIdentifierMountPaths.Add(_siblingRoot);
                break;
            case StaleRootScenario.FirstRootOvermounted:
                _native.FilesystemIdByMountPath[_firstRoot] = UnderlyingFilesystem;
                break;
            case StaleRootScenario.OvermountedThenUncovered:
                // No sibling root is left to vouch for the filesystem while the first root is covered.
                _native.FilesystemIdByMountPath[_firstRoot] = UnderlyingFilesystem;
                Directory.Delete(_siblingRoot);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }
    }

    [Test]
    public async Task UnopenableOnlyRoot_DropsTheEventAndHoldsNothing()
    {
        var unmappedHandle = new byte[] { 3, 3, 3, 3 };
        _native.ReadQueue.Enqueue(CreateEvents((FirstFilesystem, unmappedHandle, "gone.txt")));
        var source = new FanotifyVolumeChangeSource(_native);
        await source.ArmAndCatchUpAsync(new[] { _firstRoot }, CancellationToken.None);
        _native.UnopenableMountPaths.Add(_firstRoot);

        var entries = await ReadEntriesAsync(source, 1, timeoutSeconds: 1);

        Assert.Multiple(() =>
        {
            Assert.That(entries, Is.Empty);
            Assert.That(_native.OpenMountDescriptors, Is.Empty);
        });
    }
}

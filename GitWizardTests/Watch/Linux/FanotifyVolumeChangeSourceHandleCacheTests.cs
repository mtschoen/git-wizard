using System.Runtime.Versioning;
using GitWizard.Watch;
using GitWizard.Watch.Linux;
using static GitWizardTests.Watch.Linux.FanotifyEventBuffers;

namespace GitWizardTests.Watch.Linux;

// Fanotify source tests are split by concern because the combined fixture exceeds 500 lines.
[TestFixture]
[SupportedOSPlatform("linux")]
public class FanotifyVolumeChangeSourceHandleCacheTests
{
    static readonly string ExistingDirectoryPath = Path.GetTempPath();

    [Test]
    public async Task WatchAsync_CachesDirectoryHandles_AndReusesWhenOpenFailsLater()
    {
        var handle = new byte[] { 51, 52, 53, 54 };
        var firstBuffer = CreateSyntheticEventBuffer(FanotifyConstants.Create, handle, "first.txt");
        var secondBuffer = CreateSyntheticEventBuffer(FanotifyConstants.Delete, handle, "second.txt");

        var native = new FakeFanotifyVolumeNative
        {
            InitializeResult = new FanotifyNativeCallResult(7, 0)
        };
        native.HandleToFdMap[new FileHandleKey(0L, GetFullHandleBytes(handle))] = 203;
        native.FdToPathMap[203] = "/repo/dirCache";
        native.ReadQueue.Enqueue(firstBuffer);
        native.ReadQueue.Enqueue(secondBuffer);

        var source = new FanotifyVolumeChangeSource(native);
        await source.ArmAndCatchUpAsync(new[] { ExistingDirectoryPath }, CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var batches = new List<VolumeChangeBatch>();
        await foreach (var batch in source.WatchAsync(cts.Token))
        {
            batches.Add(batch);
            // Simulate directory deletion by clearing OpenByHandle mapping after first batch
            native.HandleToFdMap.Clear();
            if (batches.Count >= 2)
                break;
        }

        Assert.Multiple(() =>
        {
            var entries = batches.SelectMany(b => b.Entries).ToList();
            Assert.That(entries, Has.Count.EqualTo(2));
            Assert.That(entries[0], Is.EqualTo(new VolumeChangeEntry("/repo/dirCache/first.txt", VolumeEntryKind.Created)));
            Assert.That(entries[1], Is.EqualTo(new VolumeChangeEntry("/repo/dirCache/second.txt", VolumeEntryKind.Deleted)));
        });
    }

    [Test]
    public async Task WatchAsync_DirectoryRenamed_InvalidatesHandleCacheAndResolvesNewPath()
    {
        var rootHandle = new byte[] { 1, 1, 1, 1 };
        var dirHandle = new byte[] { 2, 2, 2, 2 };

        var initialFileBuffer = CreateSyntheticEventBuffer(FanotifyConstants.Create, dirHandle, "file1.txt");
        var movedFromBuffer = CreateSyntheticEventBuffer(FanotifyConstants.MovedFrom | FanotifyConstants.OnDirectory, rootHandle, "olddir");
        var movedToBuffer = CreateSyntheticEventBuffer(FanotifyConstants.MovedTo | FanotifyConstants.OnDirectory, rootHandle, "newdir");
        var newFileBuffer = CreateSyntheticEventBuffer(FanotifyConstants.Create, dirHandle, "file2.txt");

        var native = new FakeFanotifyVolumeNative
        {
            InitializeResult = new FanotifyNativeCallResult(7, 0)
        };
        native.HandleToFdMap[new FileHandleKey(0L, GetFullHandleBytes(rootHandle))] = 300;
        native.FdToPathMap[300] = "/repo";

        native.HandleToFdMap[new FileHandleKey(0L, GetFullHandleBytes(dirHandle))] = 301;
        native.FdToPathMap[301] = "/repo/olddir";

        native.ReadQueue.Enqueue(initialFileBuffer);
        native.ReadQueue.Enqueue(movedFromBuffer);
        native.ReadQueue.Enqueue(movedToBuffer);

        var source = new FanotifyVolumeChangeSource(native);
        await source.ArmAndCatchUpAsync(new[] { ExistingDirectoryPath }, CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var batches = new List<VolumeChangeBatch>();
        await foreach (var batch in source.WatchAsync(cts.Token))
        {
            batches.Add(batch);
            if (batches.Count == 3)
            {
                native.FdToPathMap[301] = "/repo/newdir";
                native.ReadQueue.Enqueue(newFileBuffer);
            }
            else if (batches.Count >= 4)
            {
                break;
            }
        }

        Assert.Multiple(() =>
        {
            var entries = batches.SelectMany(b => b.Entries).ToList();
            Assert.That(entries, Has.Count.EqualTo(4));
            Assert.That(entries[0], Is.EqualTo(new VolumeChangeEntry("/repo/olddir/file1.txt", VolumeEntryKind.Created)));
            Assert.That(entries[1], Is.EqualTo(new VolumeChangeEntry("/repo/olddir", VolumeEntryKind.Deleted)));
            Assert.That(entries[2], Is.EqualTo(new VolumeChangeEntry("/repo/newdir", VolumeEntryKind.MovedIn)));
            Assert.That(entries[3], Is.EqualTo(new VolumeChangeEntry("/repo/newdir/file2.txt", VolumeEntryKind.Created)));
        });
    }

    [Test]
    public async Task WatchAsync_OverlappingMarks_DeliverEachEventOnce()
    {
        var tempBase = Directory.CreateTempSubdirectory("git-wizard-overlap-test").FullName;
        var tempSub = Directory.CreateDirectory(Path.Combine(tempBase, "inner")).FullName;

        try
        {
            var handle = new byte[] { 41, 42, 43, 44 };
            var native = new FakeFanotifyVolumeNative { InitializeResult = new FanotifyNativeCallResult(7, 0) };
            native.MapHandle(0L, GetFullHandleBytes(handle), 320, tempSub);
            native.ReadQueue.Enqueue(CreateSyntheticEventBuffer(FanotifyConstants.Create, handle, "once.txt"));

            var source = new FanotifyVolumeChangeSource(native);
            var armResult = await source.ArmAndCatchUpAsync(new[] { tempBase, tempSub }, CancellationToken.None);
            Assert.That(armResult.Errors, Is.Empty);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var batches = new List<VolumeChangeBatch>();
            try
            {
                await foreach (var batch in source.WatchAsync(cts.Token))
                {
                    batches.Add(batch);
                    cts.CancelAfter(TimeSpan.FromMilliseconds(300));
                }
            }
            catch (OperationCanceledException)
            {
                // Expected: the watch is cancelled after the queued event had time to repeat.
            }

            Assert.Multiple(() =>
            {
                Assert.That(batches, Has.Count.EqualTo(1));
                Assert.That(batches[0].Volume, Is.EqualTo("/"));
                Assert.That(batches[0].Entries, Is.EqualTo(new[]
                {
                    new VolumeChangeEntry(Path.Combine(tempSub, "once.txt"), VolumeEntryKind.Created)
                }));
            });
        }
        finally
        {
            if (Directory.Exists(tempBase))
                Directory.Delete(tempBase, true);
        }
    }

    [Test]
    public async Task WatchAsync_FileDeleteWithoutOnDirectory_DoesNotInvalidateParentDirectoryCache()
    {
        var dirHandle = new byte[] { 42, 42, 42, 42 };
        var createdFileBuffer = CreateSyntheticEventBuffer(FanotifyConstants.Create, dirHandle, "file1.txt");
        var deletedFileBuffer = CreateSyntheticEventBuffer(FanotifyConstants.Delete, dirHandle, "file1.txt");
        var modifiedFileBuffer = CreateSyntheticEventBuffer(FanotifyConstants.Modify, dirHandle, "file2.txt");

        var native = new FakeFanotifyVolumeNative
        {
            InitializeResult = new FanotifyNativeCallResult(7, 0)
        };
        native.HandleToFdMap[new FileHandleKey(0L, GetFullHandleBytes(dirHandle))] = 200;
        native.FdToPathMap[200] = "/repo/dir";

        native.ReadQueue.Enqueue(createdFileBuffer);
        native.ReadQueue.Enqueue(deletedFileBuffer);
        native.ReadQueue.Enqueue(modifiedFileBuffer);

        var source = new FanotifyVolumeChangeSource(native);
        await source.ArmAndCatchUpAsync(new[] { ExistingDirectoryPath }, CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var batches = new List<VolumeChangeBatch>();
        await foreach (var batch in source.WatchAsync(cts.Token))
        {
            batches.Add(batch);
            if (batches.Count >= 3)
                break;
        }

        Assert.Multiple(() =>
        {
            var entries = batches.SelectMany(b => b.Entries).ToList();
            Assert.That(entries, Has.Count.EqualTo(3));
            Assert.That(entries[0], Is.EqualTo(new VolumeChangeEntry("/repo/dir/file1.txt", VolumeEntryKind.Created)));
            Assert.That(entries[1], Is.EqualTo(new VolumeChangeEntry("/repo/dir/file1.txt", VolumeEntryKind.Deleted)));
            Assert.That(entries[2], Is.EqualTo(new VolumeChangeEntry("/repo/dir/file2.txt", VolumeEntryKind.Modified)));
        });
    }

    [Test]
    public async Task WatchAsync_EventsUnderNestedMarkedRoots_AllRouteUnderSlash()
    {
        var tempBase = Directory.CreateTempSubdirectory("git-wizard-vol-test").FullName;
        var tempSub = Directory.CreateDirectory(Path.Combine(tempBase, "user")).FullName;

        try
        {
            var handleUser = new byte[] { 91, 92, 93, 94 };
            var handleOther = new byte[] { 95, 96, 97, 98 };

            var bufferUser = CreateSyntheticEventBuffer(FanotifyConstants.Create, handleUser, "profile.txt");
            var bufferOther = CreateSyntheticEventBuffer(FanotifyConstants.Create, handleOther, "data.txt");

            var native = new FakeFanotifyVolumeNative
            {
                InitializeResult = new FanotifyNativeCallResult(7, 0)
            };
            native.HandleToFdMap[new FileHandleKey(0L, GetFullHandleBytes(handleUser))] = 401;
            native.HandleToFdMap[new FileHandleKey(0L, GetFullHandleBytes(handleOther))] = 402;
            native.FdToPathMap[401] = tempSub;
            native.FdToPathMap[402] = tempBase + "less";
            native.ReadQueue.Enqueue(bufferUser);
            native.ReadQueue.Enqueue(bufferOther);

            var source = new FanotifyVolumeChangeSource(native);
            var armResult = await source.ArmAndCatchUpAsync(new[] { tempBase, tempSub, "/" }, CancellationToken.None);
            Assert.That(armResult.Errors, Is.Empty);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var batches = new List<VolumeChangeBatch>();
            await foreach (var batch in source.WatchAsync(cts.Token))
            {
                batches.Add(batch);
                if (batches.Count >= 2)
                    break;
            }

            Assert.Multiple(() =>
            {
                Assert.That(batches, Has.Count.EqualTo(2));
                Assert.That(batches[0].Volume, Is.EqualTo("/"));
                Assert.That(batches[0].Entries[0].FullPath, Is.EqualTo(Path.Combine(tempSub, "profile.txt")));
                Assert.That(batches[1].Volume, Is.EqualTo("/"));
                Assert.That(batches[1].Entries[0].FullPath, Is.EqualTo(tempBase + "less/data.txt"));
            });
        }
        finally
        {
            if (Directory.Exists(tempBase))
                Directory.Delete(tempBase, true);
        }
    }

}

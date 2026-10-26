using System.Runtime.Versioning;
using GitWizard.Watch;
using GitWizard.Watch.Linux;
using GitWizardTests.TestSupport;
using static GitWizardTests.Watch.Linux.FanotifyEventBuffers;

namespace GitWizardTests.Watch.Linux;

// Fanotify source tests are split by concern because the combined fixture exceeds 500 lines.
[TestFixture]
[SupportedOSPlatform("linux")]
public class FanotifyVolumeChangeSourceEventTests
{
    static readonly string ExistingDirectoryPath = TempDirectory.SystemTempPath;

    [Test]
    public async Task WatchAsync_DecodesEvents_WithAllVolumeEntryKinds()
    {
        var handleA = new byte[] { 1, 2, 3, 4 };
        var handleB = new byte[] { 5, 6, 7, 8 };
        var handleC = new byte[] { 9, 10, 11, 12 };

        var bufferA = CreateSyntheticEventBuffer(FanotifyConstants.Create, handleA, "created.txt");
        var bufferB = CreateSyntheticEventBuffer(FanotifyConstants.Delete, handleA, "deleted.txt");
        var bufferC = CreateSyntheticEventBuffer(FanotifyConstants.MovedFrom, handleB, "moved_from.txt");
        var bufferD = CreateSyntheticEventBuffer(FanotifyConstants.MovedTo, handleB, "moved_to.txt");
        var bufferE = CreateSyntheticEventBuffer(FanotifyConstants.Modify, handleC, "modified.txt");


        var combinedBuffer = CombineBuffers(bufferA, bufferB, bufferC, bufferD, bufferE);

        var native = new FakeFanotifyNative
        {
            InitializeResult = new FanotifyNativeCallResult(7, 0),
            HandleToFdMap =
            {
                [new FileHandleKey(0L, GetFullHandleBytes(handleA))] = 100,
                [new FileHandleKey(0L, GetFullHandleBytes(handleB))] = 101,
                [new FileHandleKey(0L, GetFullHandleBytes(handleC))] = 102
            },
            FdToPathMap =
            {
                [100] = "/repo/dirA",
                [101] = "/repo/dirB",
                [102] = "/repo/dirC"
            }
        };
        native.ReadQueue.Enqueue(combinedBuffer);

        var source = new FanotifyVolumeChangeSource(native);
        await source.ArmAndCatchUpAsync([ExistingDirectoryPath], CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var batches = new List<VolumeChangeBatch>();
        await foreach (var batch in source.WatchAsync(cts.Token))
        {
            batches.Add(batch);
            if (batches.Count >= 1 && batches.Sum(b => b.Entries.Count) >= 5)
            {
                break;
            }
        }

        using (Assert.EnterMultipleScope())
        {
            var entries = batches.SelectMany(b => b.Entries).ToList();
            Assert.That(entries, Has.Count.EqualTo(5));
            Assert.That(entries[0], Is.EqualTo(new VolumeChangeEntry("/repo/dirA/created.txt", VolumeEntryKind.Created)));
            Assert.That(entries[1], Is.EqualTo(new VolumeChangeEntry("/repo/dirA/deleted.txt", VolumeEntryKind.Deleted)));
            Assert.That(entries[2], Is.EqualTo(new VolumeChangeEntry("/repo/dirB/moved_from.txt", VolumeEntryKind.Deleted)));
            Assert.That(entries[3], Is.EqualTo(new VolumeChangeEntry("/repo/dirB/moved_to.txt", VolumeEntryKind.Created)));
            Assert.That(entries[4], Is.EqualTo(new VolumeChangeEntry("/repo/dirC/modified.txt", VolumeEntryKind.Modified)));
        }
    }

    [Test]
    public async Task WatchAsync_DirectoryEventWithoutFileName_ResolvesDirectoryPath()
    {
        var handle = new byte[] { 21, 22, 23, 24 };
        var buffer = CreateSyntheticEventBuffer(FanotifyConstants.Modify | FanotifyConstants.OnDirectory, handle, "");

        var native = new FakeFanotifyNative
        {
            InitializeResult = new FanotifyNativeCallResult(7, 0),
            HandleToFdMap = { [new FileHandleKey(0L, GetFullHandleBytes(handle))] = 200 },
            FdToPathMap = { [200] = "/repo/dirA" }
        };
        native.ReadQueue.Enqueue(buffer);

        var source = new FanotifyVolumeChangeSource(native);
        await source.ArmAndCatchUpAsync([ExistingDirectoryPath], CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var batches = new List<VolumeChangeBatch>();
        await foreach (var batch in source.WatchAsync(cts.Token))
        {
            batches.Add(batch);
            break;
        }

        using (Assert.EnterMultipleScope())
        {
            var entries = batches.SelectMany(b => b.Entries).ToList();
            Assert.That(entries, Has.Count.EqualTo(1));
            Assert.That(entries[0], Is.EqualTo(new VolumeChangeEntry("/repo/dirA", VolumeEntryKind.Modified)));
        }
    }

    [Test]
    public async Task WatchAsync_DotFileName_ResolvesDirectoryPath()
    {
        var handle = new byte[] { 31, 32, 33, 34 };
        var buffer = CreateSyntheticEventBuffer(FanotifyConstants.Modify, handle, ".");

        var native = new FakeFanotifyNative
        {
            InitializeResult = new FanotifyNativeCallResult(7, 0),
            HandleToFdMap = { [new FileHandleKey(0L, GetFullHandleBytes(handle))] = 201 },
            FdToPathMap = { [201] = "/" }
        };
        native.ReadQueue.Enqueue(buffer);

        var source = new FanotifyVolumeChangeSource(native);
        await source.ArmAndCatchUpAsync([ExistingDirectoryPath], CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var batches = new List<VolumeChangeBatch>();
        await foreach (var batch in source.WatchAsync(cts.Token))
        {
            batches.Add(batch);
            break;
        }

        using (Assert.EnterMultipleScope())
        {
            var entries = batches.SelectMany(b => b.Entries).ToList();
            Assert.That(entries, Has.Count.EqualTo(1));
            Assert.That(entries[0], Is.EqualTo(new VolumeChangeEntry("/", VolumeEntryKind.Modified)));
        }
    }

    [Test]
    public async Task WatchAsync_RootDirectoryParentWithFileName_FormatsPathCorrectly()
    {
        var handle = ")*+,"u8.ToArray();
        var buffer = CreateSyntheticEventBuffer(FanotifyConstants.Create, handle, "test.txt");

        var native = new FakeFanotifyNative
        {
            InitializeResult = new FanotifyNativeCallResult(7, 0),
            HandleToFdMap = { [new FileHandleKey(0L, GetFullHandleBytes(handle))] = 202 },
            FdToPathMap = { [202] = "/" }
        };
        native.ReadQueue.Enqueue(buffer);

        var source = new FanotifyVolumeChangeSource(native);
        await source.ArmAndCatchUpAsync([ExistingDirectoryPath], CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var batches = new List<VolumeChangeBatch>();
        await foreach (var batch in source.WatchAsync(cts.Token))
        {
            batches.Add(batch);
            break;
        }

        using (Assert.EnterMultipleScope())
        {
            var entries = batches.SelectMany(b => b.Entries).ToList();
            Assert.That(entries, Has.Count.EqualTo(1));
            Assert.That(entries[0], Is.EqualTo(new VolumeChangeEntry("/test.txt", VolumeEntryKind.Created)));
        }
    }

    [Test]
    public async Task WatchAsync_EventUnderMarkedRoot_IsRoutedUnderSlash()
    {
        var handle = "=>?@"u8.ToArray();
        var buffer = CreateSyntheticEventBuffer(FanotifyConstants.Create, handle, "repo.git");

        var native = new FakeFanotifyNative
        {
            InitializeResult = new FanotifyNativeCallResult(7, 0),
            HandleToFdMap = { [new FileHandleKey(0L, GetFullHandleBytes(handle))] = 204 },
            FdToPathMap = { [204] = ExistingDirectoryPath }
        };
        native.ReadQueue.Enqueue(buffer);

        var source = new FanotifyVolumeChangeSource(native);
        await source.ArmAndCatchUpAsync([ExistingDirectoryPath], CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var batches = new List<VolumeChangeBatch>();
        await foreach (var batch in source.WatchAsync(cts.Token))
        {
            batches.Add(batch);
            break;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(batches, Has.Count.EqualTo(1));
            Assert.That(batches[0].Volume, Is.EqualTo("/"));
        }
    }

    [Test]
    public async Task WatchAsync_MalformedEvents_AreSkippedCleanly()
    {
        // 1. Info record where open_by_handle_at fails and readlink returns null
        var handleUnresolved = "cc"u8.ToArray();
        var unresolvedBuffer = CreateSyntheticEventBuffer(FanotifyConstants.Create, handleUnresolved, "unknown.txt");

        // 2. Event version != 3
        var wrongVersion = new byte[32];
        BitConverter.TryWriteBytes(wrongVersion.AsSpan(0, 4), (uint)32);
        wrongVersion[4] = 2; // version 2

        // 3. Info record with length too short (< 4)
        var shortInfo = new byte[28];
        BitConverter.TryWriteBytes(shortInfo.AsSpan(0, 4), (uint)28);
        shortInfo[4] = 3; // version 3
        BitConverter.TryWriteBytes(shortInfo.AsSpan(6, 2), (ushort)24); // metadata_len
        shortInfo[24] = FanotifyConstants.EventInfoTypeDfidName;
        BitConverter.TryWriteBytes(shortInfo.AsSpan(26, 2), (ushort)2); // info_len < 4

        // 4. Info record with infoLen < 20
        var incompleteInfo = new byte[36];
        BitConverter.TryWriteBytes(incompleteInfo.AsSpan(0, 4), (uint)36);
        incompleteInfo[4] = 3;
        BitConverter.TryWriteBytes(incompleteInfo.AsSpan(6, 2), (ushort)24);
        incompleteInfo[24] = FanotifyConstants.EventInfoTypeDfidName;
        BitConverter.TryWriteBytes(incompleteInfo.AsSpan(26, 2), (ushort)12); // infoLen < 20

        // 5. Valid event that follows
        var handleValid = "GHIJ"u8.ToArray();
        var validBuffer = CreateSyntheticEventBuffer(FanotifyConstants.Create, handleValid, "valid.txt");


        var combined = CombineBuffers(wrongVersion, shortInfo, incompleteInfo, unresolvedBuffer, validBuffer);

        var native = new FakeFanotifyNative
        {
            InitializeResult = new FanotifyNativeCallResult(7, 0),
            HandleToFdMap = { [new FileHandleKey(0L, GetFullHandleBytes(handleValid))] = 205 },
            FdToPathMap = { [205] = "/repo/valid" }
        };
        native.ReadQueue.Enqueue(combined);

        var source = new FanotifyVolumeChangeSource(native);
        await source.ArmAndCatchUpAsync([ExistingDirectoryPath], CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var batches = new List<VolumeChangeBatch>();
        await foreach (var batch in source.WatchAsync(cts.Token))
        {
            batches.Add(batch);
            break;
        }

        using (Assert.EnterMultipleScope())
        {
            var entries = batches.SelectMany(b => b.Entries).ToList();
            Assert.That(entries, Has.Count.EqualTo(1));
            Assert.That(entries[0].FullPath, Is.EqualTo("/repo/valid/valid.txt"));
        }
    }

    [Test]
    public async Task WatchAsync_ShortEventLength_BreaksBufferProcessingWithoutCrashing()
    {
        var shortEvent = new byte[16];
        BitConverter.TryWriteBytes(shortEvent.AsSpan(0, 4), (uint)16);

        var handleValid = "QRST"u8.ToArray();
        var validBuffer = CreateSyntheticEventBuffer(FanotifyConstants.Create, handleValid, "second.txt");

        var native = new FakeFanotifyNative
        {
            InitializeResult = new FanotifyNativeCallResult(7, 0),
            HandleToFdMap = { [new FileHandleKey(0L, GetFullHandleBytes(handleValid))] = 206 },
            FdToPathMap = { [206] = "/repo/second" }
        };
        native.ReadQueue.Enqueue(shortEvent);
        native.ReadQueue.Enqueue(validBuffer);

        var source = new FanotifyVolumeChangeSource(native);
        await source.ArmAndCatchUpAsync([ExistingDirectoryPath], CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var batches = new List<VolumeChangeBatch>();
        await foreach (var batch in source.WatchAsync(cts.Token))
        {
            batches.Add(batch);
            break;
        }

        using (Assert.EnterMultipleScope())
        {
            var entries = batches.SelectMany(b => b.Entries).ToList();
            Assert.That(entries, Has.Count.EqualTo(1));
            Assert.That(entries[0].FullPath, Is.EqualTo("/repo/second/second.txt"));
        }
    }

    [Test]
    public async Task WatchAsync_HandleBytesExceedsInfoLength_SkipsRecord()
    {
        // Info record with handle_bytes = 100 but infoLen = 30 (< 20 + 100)
        var invalidHandleInfo = new byte[54];
        BitConverter.TryWriteBytes(invalidHandleInfo.AsSpan(0, 4), (uint)54);
        invalidHandleInfo[4] = 3;
        BitConverter.TryWriteBytes(invalidHandleInfo.AsSpan(6, 2), (ushort)24);
        invalidHandleInfo[24] = FanotifyConstants.EventInfoTypeDfidName;
        BitConverter.TryWriteBytes(invalidHandleInfo.AsSpan(26, 2), (ushort)30);
        BitConverter.TryWriteBytes(invalidHandleInfo.AsSpan(36, 4), (uint)100); // handle_bytes = 100

        var validHandle = "=>?@"u8.ToArray();
        var validBuffer = CreateSyntheticEventBuffer(FanotifyConstants.Create, validHandle, "ok.txt");


        var combined = CombineBuffers(invalidHandleInfo, validBuffer);

        var native = new FakeFanotifyNative
        {
            InitializeResult = new FanotifyNativeCallResult(7, 0),
            HandleToFdMap = { [new FileHandleKey(0L, GetFullHandleBytes(validHandle))] = 207 },
            FdToPathMap = { [207] = "/repo/ok" }
        };
        native.ReadQueue.Enqueue(combined);

        var source = new FanotifyVolumeChangeSource(native);
        await source.ArmAndCatchUpAsync([ExistingDirectoryPath], CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var batches = new List<VolumeChangeBatch>();
        await foreach (var batch in source.WatchAsync(cts.Token))
        {
            batches.Add(batch);
            break;
        }

        using (Assert.EnterMultipleScope())
        {
            var entries = batches.SelectMany(b => b.Entries).ToList();
            Assert.That(entries, Has.Count.EqualTo(1));
            Assert.That(entries[0].FullPath, Is.EqualTo("/repo/ok/ok.txt"));
        }
    }

    [Test]
    public async Task WatchAsync_MovedToDirectory_IsEmittedAsMovedIn_AndMovedToFileAsCreated()
    {
        var parentHandle = new byte[] { 31, 32, 33, 34 };
        var directoryBuffer = CreateSyntheticEventBuffer(
            FanotifyConstants.MovedTo | FanotifyConstants.OnDirectory, parentHandle, "incoming");
        var fileBuffer = CreateSyntheticEventBuffer(FanotifyConstants.MovedTo, parentHandle, "note.txt");
        var native = new FakeFanotifyNative { InitializeResult = new FanotifyNativeCallResult(7, 0) };
        native.MapHandle(0L, GetFullHandleBytes(parentHandle), 310, "/repos");
        native.ReadQueue.Enqueue(CombineBuffers(directoryBuffer, fileBuffer));


        var source = new FanotifyVolumeChangeSource(native);
        await source.ArmAndCatchUpAsync([ExistingDirectoryPath], CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var batches = new List<VolumeChangeBatch>();
        await foreach (var batch in source.WatchAsync(cts.Token))
        {
            batches.Add(batch);
            break;
        }

        Assert.That(batches.SelectMany(batch => batch.Entries), Is.EqualTo([
            new VolumeChangeEntry(Path.Combine("/repos", "incoming"), VolumeEntryKind.MovedIn),
            new VolumeChangeEntry(Path.Combine("/repos", "note.txt"), VolumeEntryKind.Created)
        ]));
    }

}

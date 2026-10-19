using System.Runtime.Versioning;
using GitWizard.Watch;
using GitWizard.Watch.Linux;
using static GitWizardTests.Watch.Linux.FanotifyEventBuffers;

namespace GitWizardTests.Watch.Linux;

[SupportedOSPlatform("linux")]
public class FanotifyVolumeChangeSourceTests
{
    static readonly string ExistingDirectoryPath = Path.GetTempPath();

    [Test]
    public async Task Constructor_Default_CreatesInstance()
    {
        await using var source = new FanotifyVolumeChangeSource();
        Assert.That(source, Is.Not.Null);
    }

    [Test]
    public void ArmAndCatchUpAsync_NullVolumes_ThrowsArgumentNullException()
    {
        var native = new FakeFanotifyVolumeNative();
        var source = new FanotifyVolumeChangeSource(native);

        Assert.ThrowsAsync<ArgumentNullException>(() =>
            source.ArmAndCatchUpAsync(null!, CancellationToken.None));
    }

    [Test]
    public void ArmAndCatchUpAsync_CancellationRequested_ThrowsOperationCanceledException()
    {
        var native = new FakeFanotifyVolumeNative();
        var source = new FanotifyVolumeChangeSource(native);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(() =>
            source.ArmAndCatchUpAsync(new[] { ExistingDirectoryPath }, cts.Token));
    }

    [Test]
    public async Task ArmAndCatchUpAsync_EmptyVolumes_ReturnsEmptyErrors()
    {
        var native = new FakeFanotifyVolumeNative();
        var source = new FanotifyVolumeChangeSource(native);

        var result = await source.ArmAndCatchUpAsync(Array.Empty<string>(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Errors, Is.Empty);
            Assert.That(native.InitializeCallCount, Is.Zero);
        });
    }

    [Test]
    public async Task ArmAndCatchUpAsync_InitializeFails_PopulatesErrorsForVolumes()
    {
        var native = new FakeFanotifyVolumeNative
        {
            InitializeResult = new FanotifyNativeCallResult(-1, FanotifyError.NotPermitted)
        };
        var source = new FanotifyVolumeChangeSource(native);

        var result = await source.ArmAndCatchUpAsync(new[] { "/", "/mnt/data" }, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Errors, Has.Count.EqualTo(2));
            Assert.That(result.Errors["/"], Does.Contain("fanotify_init failed"));
            Assert.That(result.Errors["/mnt/data"], Does.Contain("fanotify_init failed"));
            Assert.That(native.MarkCalls, Is.Empty);
        });
    }

    [Test]
    public async Task ArmAndCatchUpAsync_WhitespaceAndMissingDirectory_RecordsErrors()
    {
        var native = new FakeFanotifyVolumeNative
        {
            InitializeResult = new FanotifyNativeCallResult(5, 0)
        };
        var source = new FanotifyVolumeChangeSource(native);
        var missingPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        var result = await source.ArmAndCatchUpAsync(new[] { "  ", missingPath }, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Errors, Has.Count.EqualTo(2));
            Assert.That(result.Errors["  "], Does.Contain("whitespace"));
            Assert.That(result.Errors[missingPath], Does.Contain("does not exist"));
            Assert.That(native.MarkCalls, Is.Empty);
        });
    }

    [Test]
    public async Task ArmAndCatchUpAsync_MarkFails_RecordsErrorForFailedVolume()
    {
        var native = new FakeFanotifyVolumeNative
        {
            InitializeResult = new FanotifyNativeCallResult(5, 0)
        };
        native.MarkResults.Enqueue(new FanotifyNativeCallResult(-1, FanotifyError.OperationNotSupported));
        var source = new FanotifyVolumeChangeSource(native);

        var result = await source.ArmAndCatchUpAsync(new[] { ExistingDirectoryPath }, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Errors, Has.Count.EqualTo(1));
            Assert.That(result.Errors[ExistingDirectoryPath], Does.Contain("fanotify_mark failed"));
            Assert.That(native.MarkCalls, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task ArmAndCatchUpAsync_Success_MarksFilesystem()
    {
        var native = new FakeFanotifyVolumeNative
        {
            InitializeResult = new FanotifyNativeCallResult(12, 0)
        };
        var source = new FanotifyVolumeChangeSource(native);

        var result = await source.ArmAndCatchUpAsync(new[] { ExistingDirectoryPath }, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Errors, Is.Empty);
            Assert.That(native.InitializeFlags, Is.EqualTo(
                FanotifyConstants.CloseOnExecute | FanotifyConstants.ReportDirectoryFileIdAndName));
            Assert.That(native.EventFileFlags, Is.EqualTo(FanotifyConstants.ReadOnly));
            Assert.That(native.MarkCalls, Has.Count.EqualTo(1));
            Assert.That(native.MarkCalls[0].Descriptor, Is.EqualTo(12));
            Assert.That(native.MarkCalls[0].Path, Is.EqualTo(ExistingDirectoryPath));
            Assert.That(native.MarkCalls[0].Flags, Is.EqualTo(
                FanotifyConstants.MarkAdd | FanotifyConstants.MarkFilesystem));
        });
    }

    [Test]
    public void WatchAsync_BeforeArm_ThrowsInvalidOperationException()
    {
        var native = new FakeFanotifyVolumeNative();
        var source = new FanotifyVolumeChangeSource(native);

        var enumerator = source.WatchAsync(CancellationToken.None).GetAsyncEnumerator();

        Assert.ThrowsAsync<InvalidOperationException>(async () => await enumerator.MoveNextAsync());
    }

    [Test]
    public async Task WatchAsync_AllVolumesFailedToArm_CompletesImmediately()
    {
        var native = new FakeFanotifyVolumeNative
        {
            InitializeResult = new FanotifyNativeCallResult(10, 0)
        };
        native.MarkResults.Enqueue(new FanotifyNativeCallResult(-1, FanotifyError.OperationNotSupported));
        var source = new FanotifyVolumeChangeSource(native);

        await source.ArmAndCatchUpAsync(new[] { ExistingDirectoryPath }, CancellationToken.None);

        var batches = new List<VolumeChangeBatch>();
        await foreach (var batch in source.WatchAsync(CancellationToken.None))
            batches.Add(batch);

        Assert.That(batches, Is.Empty);
        Assert.That(source.PumpStarted.IsCompleted, Is.False,
            "No usable volumes means PumpEventsAsync never ran; a consumer waiting on PumpStarted must not be told it did.");
    }

    [Test]
    public async Task WatchAsync_SignalsPumpStartedOnceTheReadLoopIsActuallyRunning()
    {
        var native = new FakeFanotifyVolumeNative
        {
            InitializeResult = new FanotifyNativeCallResult(9, 0)
        };
        var source = new FanotifyVolumeChangeSource(native);
        await source.ArmAndCatchUpAsync(new[] { ExistingDirectoryPath }, CancellationToken.None);

        Assert.That(source.PumpStarted.IsCompleted, Is.False,
            "Arming alone must not signal the pump; WatchAsync has not been called yet.");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var enumerator = source.WatchAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        var moveNext = enumerator.MoveNextAsync().AsTask();

        // MoveNextAsync only queues PumpEventsAsync onto a pool thread (Task.Run); that queueing
        // alone is not the same as the pump having actually begun reading. PumpStarted must still
        // complete promptly once the queued work actually runs, with no batch required to prove it.
        await source.PumpStarted.WaitAsync(TimeSpan.FromSeconds(5));

        await cts.CancelAsync();
        try
        {
            await moveNext;
        }
        catch (OperationCanceledException)
        {
            // Expected: cancellation unwinds the still-pending read.
        }
    }

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

        var native = new FakeFanotifyVolumeNative
        {
            InitializeResult = new FanotifyNativeCallResult(7, 0)
        };
        native.HandleToFdMap[new FileHandleKey(0L, GetFullHandleBytes(handleA))] = 100;
        native.HandleToFdMap[new FileHandleKey(0L, GetFullHandleBytes(handleB))] = 101;
        native.HandleToFdMap[new FileHandleKey(0L, GetFullHandleBytes(handleC))] = 102;
        native.FdToPathMap[100] = "/repo/dirA";
        native.FdToPathMap[101] = "/repo/dirB";
        native.FdToPathMap[102] = "/repo/dirC";
        native.ReadQueue.Enqueue(combinedBuffer);

        var source = new FanotifyVolumeChangeSource(native);
        await source.ArmAndCatchUpAsync(new[] { ExistingDirectoryPath }, CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var batches = new List<VolumeChangeBatch>();
        await foreach (var batch in source.WatchAsync(cts.Token))
        {
            batches.Add(batch);
            if (batches.Count >= 1 && batches.Sum(b => b.Entries.Count) >= 5)
                break;
        }

        Assert.Multiple(() =>
        {
            var entries = batches.SelectMany(b => b.Entries).ToList();
            Assert.That(entries, Has.Count.EqualTo(5));
            Assert.That(entries[0], Is.EqualTo(new VolumeChangeEntry("/repo/dirA/created.txt", VolumeEntryKind.Created)));
            Assert.That(entries[1], Is.EqualTo(new VolumeChangeEntry("/repo/dirA/deleted.txt", VolumeEntryKind.Deleted)));
            Assert.That(entries[2], Is.EqualTo(new VolumeChangeEntry("/repo/dirB/moved_from.txt", VolumeEntryKind.Deleted)));
            Assert.That(entries[3], Is.EqualTo(new VolumeChangeEntry("/repo/dirB/moved_to.txt", VolumeEntryKind.Created)));
            Assert.That(entries[4], Is.EqualTo(new VolumeChangeEntry("/repo/dirC/modified.txt", VolumeEntryKind.Modified)));
        });
    }

    [Test]
    public async Task WatchAsync_DirectoryEventWithoutFileName_ResolvesDirectoryPath()
    {
        var handle = new byte[] { 21, 22, 23, 24 };
        var buffer = CreateSyntheticEventBuffer(FanotifyConstants.Modify | FanotifyConstants.OnDirectory, handle, "");

        var native = new FakeFanotifyVolumeNative
        {
            InitializeResult = new FanotifyNativeCallResult(7, 0)
        };
        native.HandleToFdMap[new FileHandleKey(0L, GetFullHandleBytes(handle))] = 200;
        native.FdToPathMap[200] = "/repo/dirA";
        native.ReadQueue.Enqueue(buffer);

        var source = new FanotifyVolumeChangeSource(native);
        await source.ArmAndCatchUpAsync(new[] { ExistingDirectoryPath }, CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var batches = new List<VolumeChangeBatch>();
        await foreach (var batch in source.WatchAsync(cts.Token))
        {
            batches.Add(batch);
            break;
        }

        Assert.Multiple(() =>
        {
            var entries = batches.SelectMany(b => b.Entries).ToList();
            Assert.That(entries, Has.Count.EqualTo(1));
            Assert.That(entries[0], Is.EqualTo(new VolumeChangeEntry("/repo/dirA", VolumeEntryKind.Modified)));
        });
    }

    [Test]
    public async Task WatchAsync_DotFileName_ResolvesDirectoryPath()
    {
        var handle = new byte[] { 31, 32, 33, 34 };
        var buffer = CreateSyntheticEventBuffer(FanotifyConstants.Modify, handle, ".");

        var native = new FakeFanotifyVolumeNative
        {
            InitializeResult = new FanotifyNativeCallResult(7, 0)
        };
        native.HandleToFdMap[new FileHandleKey(0L, GetFullHandleBytes(handle))] = 201;
        native.FdToPathMap[201] = "/";
        native.ReadQueue.Enqueue(buffer);

        var source = new FanotifyVolumeChangeSource(native);
        await source.ArmAndCatchUpAsync(new[] { ExistingDirectoryPath }, CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var batches = new List<VolumeChangeBatch>();
        await foreach (var batch in source.WatchAsync(cts.Token))
        {
            batches.Add(batch);
            break;
        }

        Assert.Multiple(() =>
        {
            var entries = batches.SelectMany(b => b.Entries).ToList();
            Assert.That(entries, Has.Count.EqualTo(1));
            Assert.That(entries[0], Is.EqualTo(new VolumeChangeEntry("/", VolumeEntryKind.Modified)));
        });
    }

    [Test]
    public async Task WatchAsync_RootDirectoryParentWithFileName_FormatsPathCorrectly()
    {
        var handle = new byte[] { 41, 42, 43, 44 };
        var buffer = CreateSyntheticEventBuffer(FanotifyConstants.Create, handle, "test.txt");

        var native = new FakeFanotifyVolumeNative
        {
            InitializeResult = new FanotifyNativeCallResult(7, 0)
        };
        native.HandleToFdMap[new FileHandleKey(0L, GetFullHandleBytes(handle))] = 202;
        native.FdToPathMap[202] = "/";
        native.ReadQueue.Enqueue(buffer);

        var source = new FanotifyVolumeChangeSource(native);
        await source.ArmAndCatchUpAsync(new[] { ExistingDirectoryPath }, CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var batches = new List<VolumeChangeBatch>();
        await foreach (var batch in source.WatchAsync(cts.Token))
        {
            batches.Add(batch);
            break;
        }

        Assert.Multiple(() =>
        {
            var entries = batches.SelectMany(b => b.Entries).ToList();
            Assert.That(entries, Has.Count.EqualTo(1));
            Assert.That(entries[0], Is.EqualTo(new VolumeChangeEntry("/test.txt", VolumeEntryKind.Created)));
        });
    }

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
    public async Task WatchAsync_EventUnderMarkedRoot_IsRoutedUnderSlash()
    {
        var handle = new byte[] { 61, 62, 63, 64 };
        var buffer = CreateSyntheticEventBuffer(FanotifyConstants.Create, handle, "repo.git");

        var native = new FakeFanotifyVolumeNative
        {
            InitializeResult = new FanotifyNativeCallResult(7, 0)
        };
        native.HandleToFdMap[new FileHandleKey(0L, GetFullHandleBytes(handle))] = 204;
        native.FdToPathMap[204] = ExistingDirectoryPath;
        native.ReadQueue.Enqueue(buffer);

        var source = new FanotifyVolumeChangeSource(native);
        await source.ArmAndCatchUpAsync(new[] { ExistingDirectoryPath }, CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var batches = new List<VolumeChangeBatch>();
        await foreach (var batch in source.WatchAsync(cts.Token))
        {
            batches.Add(batch);
            break;
        }

        Assert.Multiple(() =>
        {
            Assert.That(batches, Has.Count.EqualTo(1));
            Assert.That(batches[0].Volume, Is.EqualTo("/"));
        });
    }

    [Test]
    public async Task WatchAsync_MalformedEvents_AreSkippedCleanly()
    {
        // 1. Info record where open_by_handle_at fails and readlink returns null
        var handleUnresolved = new byte[] { 99, 99 };
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
        var handleValid = new byte[] { 71, 72, 73, 74 };
        var validBuffer = CreateSyntheticEventBuffer(FanotifyConstants.Create, handleValid, "valid.txt");

        var combined = CombineBuffers(wrongVersion, shortInfo, incompleteInfo, unresolvedBuffer, validBuffer);

        var native = new FakeFanotifyVolumeNative
        {
            InitializeResult = new FanotifyNativeCallResult(7, 0)
        };
        native.HandleToFdMap[new FileHandleKey(0L, GetFullHandleBytes(handleValid))] = 205;
        native.FdToPathMap[205] = "/repo/valid";
        native.ReadQueue.Enqueue(combined);

        var source = new FanotifyVolumeChangeSource(native);
        await source.ArmAndCatchUpAsync(new[] { ExistingDirectoryPath }, CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var batches = new List<VolumeChangeBatch>();
        await foreach (var batch in source.WatchAsync(cts.Token))
        {
            batches.Add(batch);
            break;
        }

        Assert.Multiple(() =>
        {
            var entries = batches.SelectMany(b => b.Entries).ToList();
            Assert.That(entries, Has.Count.EqualTo(1));
            Assert.That(entries[0].FullPath, Is.EqualTo("/repo/valid/valid.txt"));
        });
    }

    [Test]
    public async Task WatchAsync_ShortEventLength_BreaksBufferProcessingWithoutCrashing()
    {
        var shortEvent = new byte[16];
        BitConverter.TryWriteBytes(shortEvent.AsSpan(0, 4), (uint)16);

        var handleValid = new byte[] { 81, 82, 83, 84 };
        var validBuffer = CreateSyntheticEventBuffer(FanotifyConstants.Create, handleValid, "second.txt");

        var native = new FakeFanotifyVolumeNative
        {
            InitializeResult = new FanotifyNativeCallResult(7, 0)
        };
        native.HandleToFdMap[new FileHandleKey(0L, GetFullHandleBytes(handleValid))] = 206;
        native.FdToPathMap[206] = "/repo/second";
        native.ReadQueue.Enqueue(shortEvent);
        native.ReadQueue.Enqueue(validBuffer);

        var source = new FanotifyVolumeChangeSource(native);
        await source.ArmAndCatchUpAsync(new[] { ExistingDirectoryPath }, CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var batches = new List<VolumeChangeBatch>();
        await foreach (var batch in source.WatchAsync(cts.Token))
        {
            batches.Add(batch);
            break;
        }

        Assert.Multiple(() =>
        {
            var entries = batches.SelectMany(b => b.Entries).ToList();
            Assert.That(entries, Has.Count.EqualTo(1));
            Assert.That(entries[0].FullPath, Is.EqualTo("/repo/second/second.txt"));
        });
    }

    [Test]
    public async Task WatchAsync_PollFails_RaisesSourceDied()
    {
        var native = new FakeFanotifyVolumeNative
        {
            InitializeResult = new FanotifyNativeCallResult(7, 0),
            PollResult = -1
        };

        var source = new FanotifyVolumeChangeSource(native);
        string? diedReason = null;
        source.SourceDied += reason => diedReason = reason;

        await source.ArmAndCatchUpAsync(new[] { ExistingDirectoryPath }, CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var batches = new List<VolumeChangeBatch>();
        await foreach (var batch in source.WatchAsync(cts.Token))
            batches.Add(batch);

        Assert.Multiple(() =>
        {
            Assert.That(batches, Is.Empty);
            Assert.That(diedReason, Does.Contain("poll failed"));
        });
    }

    [Test]
    public async Task WatchAsync_ReadReturnsZero_RaisesSourceDied()
    {
        var native = new FakeFanotifyVolumeNative
        {
            InitializeResult = new FanotifyNativeCallResult(7, 0),
            ReadReturnsZero = true
        };

        var source = new FanotifyVolumeChangeSource(native);
        string? diedReason = null;
        source.SourceDied += reason => diedReason = reason;

        await source.ArmAndCatchUpAsync(new[] { ExistingDirectoryPath }, CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var batches = new List<VolumeChangeBatch>();
        await foreach (var batch in source.WatchAsync(cts.Token))
            batches.Add(batch);

        Assert.Multiple(() =>
        {
            Assert.That(batches, Is.Empty);
            Assert.That(diedReason, Does.Contain("no bytes"));
        });
    }

    [Test]
    public async Task WatchAsync_ReadThrowsException_RaisesSourceDied()
    {
        var native = new FakeFanotifyVolumeNative
        {
            InitializeResult = new FanotifyNativeCallResult(7, 0),
            ReadThrowsException = true
        };

        var source = new FanotifyVolumeChangeSource(native);
        string? diedReason = null;
        source.SourceDied += reason => diedReason = reason;

        await source.ArmAndCatchUpAsync(new[] { ExistingDirectoryPath }, CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var batches = new List<VolumeChangeBatch>();
        await foreach (var batch in source.WatchAsync(cts.Token))
            batches.Add(batch);

        Assert.Multiple(() =>
        {
            Assert.That(batches, Is.Empty);
            Assert.That(diedReason, Does.Contain("pump faulted"));
        });
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

        var validHandle = new byte[] { 61, 62, 63, 64 };
        var validBuffer = CreateSyntheticEventBuffer(FanotifyConstants.Create, validHandle, "ok.txt");

        var combined = CombineBuffers(invalidHandleInfo, validBuffer);

        var native = new FakeFanotifyVolumeNative
        {
            InitializeResult = new FanotifyNativeCallResult(7, 0)
        };
        native.HandleToFdMap[new FileHandleKey(0L, GetFullHandleBytes(validHandle))] = 207;
        native.FdToPathMap[207] = "/repo/ok";
        native.ReadQueue.Enqueue(combined);

        var source = new FanotifyVolumeChangeSource(native);
        await source.ArmAndCatchUpAsync(new[] { ExistingDirectoryPath }, CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var batches = new List<VolumeChangeBatch>();
        await foreach (var batch in source.WatchAsync(cts.Token))
        {
            batches.Add(batch);
            break;
        }

        Assert.Multiple(() =>
        {
            var entries = batches.SelectMany(b => b.Entries).ToList();
            Assert.That(entries, Has.Count.EqualTo(1));
            Assert.That(entries[0].FullPath, Is.EqualTo("/repo/ok/ok.txt"));
        });
    }

    [Test]
    public async Task ArmAndCatchUpAsync_CalledTwice_ReArmsAndDisposesPreviousDescriptor()
    {
        var native = new FakeFanotifyVolumeNative
        {
            InitializeResult = new FanotifyNativeCallResult(101, 0)
        };

        var source = new FanotifyVolumeChangeSource(native);
        await source.ArmAndCatchUpAsync(new[] { ExistingDirectoryPath }, CancellationToken.None);

        native.InitializeResult = new FanotifyNativeCallResult(102, 0);
        await source.ArmAndCatchUpAsync(new[] { ExistingDirectoryPath }, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(native.ClosedDescriptors, Does.Contain(101));
            Assert.That(native.MarkCalls, Has.Count.EqualTo(2));
        });
    }

    [Test]
    public async Task DisposeAsync_DisposesDescriptorAndAllowsCleanExit()
    {
        var native = new FakeFanotifyVolumeNative
        {
            InitializeResult = new FanotifyNativeCallResult(77, 0)
        };

        var source = new FanotifyVolumeChangeSource(native);
        await source.ArmAndCatchUpAsync(new[] { ExistingDirectoryPath }, CancellationToken.None);

        await source.DisposeAsync();

        Assert.That(native.ClosedDescriptors, Does.Contain(77));
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
    public async Task WatchAsync_MovedToDirectory_IsEmittedAsMovedIn_AndMovedToFileAsCreated()
    {
        var parentHandle = new byte[] { 31, 32, 33, 34 };
        var directoryBuffer = CreateSyntheticEventBuffer(
            FanotifyConstants.MovedTo | FanotifyConstants.OnDirectory, parentHandle, "incoming");
        var fileBuffer = CreateSyntheticEventBuffer(FanotifyConstants.MovedTo, parentHandle, "note.txt");
        var native = new FakeFanotifyVolumeNative { InitializeResult = new FanotifyNativeCallResult(7, 0) };
        native.MapHandle(0L, GetFullHandleBytes(parentHandle), 310, "/repos");
        native.ReadQueue.Enqueue(CombineBuffers(directoryBuffer, fileBuffer));

        var source = new FanotifyVolumeChangeSource(native);
        await source.ArmAndCatchUpAsync(new[] { ExistingDirectoryPath }, CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var batches = new List<VolumeChangeBatch>();
        await foreach (var batch in source.WatchAsync(cts.Token))
        {
            batches.Add(batch);
            break;
        }

        Assert.That(batches.SelectMany(batch => batch.Entries), Is.EqualTo(new[]
        {
            new VolumeChangeEntry(Path.Combine("/repos", "incoming"), VolumeEntryKind.MovedIn),
            new VolumeChangeEntry(Path.Combine("/repos", "note.txt"), VolumeEntryKind.Created)
        }));
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
    public async Task WatchAsync_QueueOverflowEvent_RaisesSourceDiedAndTerminates()
    {
        var overflowBuffer = CreateSyntheticOverflowBuffer();
        var native = new FakeFanotifyVolumeNative
        {
            InitializeResult = new FanotifyNativeCallResult(7, 0)
        };
        native.ReadQueue.Enqueue(overflowBuffer);

        var source = new FanotifyVolumeChangeSource(native);
        string? diedReason = null;
        source.SourceDied += reason => diedReason = reason;

        await source.ArmAndCatchUpAsync(new[] { ExistingDirectoryPath }, CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var batches = new List<VolumeChangeBatch>();
        await foreach (var batch in source.WatchAsync(cts.Token))
            batches.Add(batch);

        Assert.Multiple(() =>
        {
            Assert.That(batches, Is.Empty);
            Assert.That(diedReason, Does.Contain("overflow"));
        });
    }

    [Test]
    public async Task WatchAsync_CancelledDuringWatch_TerminatesPumpCleanlyWithoutFaulting()
    {
        var native = new FakeFanotifyVolumeNative
        {
            InitializeResult = new FanotifyNativeCallResult(7, 0)
        };

        var source = new FanotifyVolumeChangeSource(native);
        await source.ArmAndCatchUpAsync(new[] { ExistingDirectoryPath }, CancellationToken.None);

        using var cts = new CancellationTokenSource();
        var enumerator = source.WatchAsync(cts.Token).GetAsyncEnumerator(cts.Token);

        await cts.CancelAsync();
        Assert.CatchAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync());

        await source.DisposeAsync();
        Assert.That(native.ClosedDescriptors, Does.Contain(7));
    }

    [Test]
    public async Task WatchAsync_DisposeAsyncConcurrentWithEnumeration_DoesNotThrowObjectDisposedException()
    {
        var native = new FakeFanotifyVolumeNative
        {
            InitializeResult = new FanotifyNativeCallResult(7, 0)
        };

        var source = new FanotifyVolumeChangeSource(native);
        await source.ArmAndCatchUpAsync(new[] { ExistingDirectoryPath }, CancellationToken.None);

        using var cts = new CancellationTokenSource();
        var enumerator = source.WatchAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        var moveNextTask = enumerator.MoveNextAsync().AsTask();

        // Simulate LiveWatchController.StopAsync: cancel and immediately force-dispose the source concurrently.
        await cts.CancelAsync();
        await source.DisposeAsync();

        // MoveNextAsync should either complete with false or throw OperationCanceledException,
        // but must never throw ObjectDisposedException.
        try
        {
            await moveNextTask;
        }
        catch (OperationCanceledException)
        {
            // Expected on cancellation
        }

        await enumerator.DisposeAsync();
        Assert.That(native.ClosedDescriptors, Does.Contain(7));
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

    [Test]
    [Category("RequiresRoot")]
    [Platform("Linux")]
    [SupportedOSPlatform("linux")]
    public async Task RealFanotify_DetectsFileCreationAndDeletion()
    {
        var probe = FanotifyInterop.Probe(Path.GetTempPath());
        if (probe.RequiresElevation)
        {
            Assert.Inconclusive("Requires root or CAP_SYS_ADMIN");
            return;
        }

        var tempDir = Directory.CreateTempSubdirectory("git-wizard-watch-test").FullName;
        try
        {
            using var tmpfs = TemporaryTmpfsMount.TryMount(Path.Combine(tempDir, "tmpfs"), out var mountFailure);
            if (tmpfs is null)
            {
                Assert.Inconclusive($"Mounting a tmpfs for the second filesystem needs root: {mountFailure}");
                return;
            }

            var outerFilesystemId = ReadFilesystemIdentifier(tempDir);
            var tmpfsFilesystemId = ReadFilesystemIdentifier(tmpfs.Path);
            Assert.That(tmpfsFilesystemId, Is.Not.EqualTo(outerFilesystemId),
                "the tmpfs and its parent must be distinct filesystems for fsid matching to be exercised");

            await using (var source = new FanotifyVolumeChangeSource())
            {
                using var armCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var armResult = await source.ArmAndCatchUpAsync(new[] { tempDir, tmpfs.Path }, armCts.Token);
                Assert.That(armResult.Errors, Is.Empty);

                using var watchCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var watchToken = watchCts.Token;
                var outerFile = Path.Combine(tempDir, "test.txt");
                var tmpfsFile = Path.Combine(tmpfs.Path, "test.txt");
                var expected = new HashSet<(string Path, VolumeEntryKind Kind)>
                {
                    (outerFile, VolumeEntryKind.Created), (outerFile, VolumeEntryKind.Deleted),
                    (tmpfsFile, VolumeEntryKind.Created), (tmpfsFile, VolumeEntryKind.Deleted)
                };

                // fanotify merges a create and a delete of one name into a single event, so each file is
                // deleted only after its creation was observed.
                foreach (var file in new[] { outerFile, tmpfsFile })
                    await File.WriteAllTextAsync(file, "hello", watchToken);

                var observed = new HashSet<(string Path, VolumeEntryKind Kind)>();
                var deleted = false;
                try
                {
                    await foreach (var batch in source.WatchAsync(watchToken))
                    {
                        foreach (var entry in batch.Entries)
                            observed.Add((entry.FullPath, entry.Kind));

                        if (!deleted && observed.Contains((outerFile, VolumeEntryKind.Created))
                            && observed.Contains((tmpfsFile, VolumeEntryKind.Created)))
                        {
                            deleted = true;
                            File.Delete(outerFile);
                            File.Delete(tmpfsFile);
                        }

                        if (expected.IsSubsetOf(observed))
                            break;
                    }
                }
                catch (OperationCanceledException)
                {
                    // The deadline passed; the assertion below names what was and was not observed.
                }

                Assert.That(expected.Except(observed), Is.Empty,
                    "creations and deletions on both real filesystems");
            }
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [SupportedOSPlatform("linux")]
    static long ReadFilesystemIdentifier(string directory)
    {
        var opened = FanotifyNative.Instance.OpenDirectory(directory);
        Assert.That(opened.Value, Is.GreaterThanOrEqualTo(0), $"open {directory} (errno {opened.ErrorNumber})");
        try
        {
            Assert.That(FanotifyNative.Instance.TryReadFilesystemIdentifier(opened.Value, out var filesystemId), Is.True);
            return filesystemId;
        }
        finally
        {
            FanotifyNative.Instance.Close(opened.Value);
        }
    }

    [Test]
    public async Task ArmPublishesMarkReadinessPerRoot()
    {
        var native = new FakeFanotifyVolumeNative
        {
            InitializeResult = new FanotifyNativeCallResult(5, 0)
        };
        native.MarkResults.Enqueue(new FanotifyNativeCallResult(0, 0));
        native.MarkResults.Enqueue(new FanotifyNativeCallResult(-1, 13));
        var tempRoot = Path.Combine(Path.GetTempPath(), "gw-fanotify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var okPath = Path.Combine(tempRoot, "ok");
        Directory.CreateDirectory(okPath);
        var brokenPath = Path.Combine(tempRoot, "broken");
        Directory.CreateDirectory(brokenPath);
        try
        {
            var source = new FanotifyVolumeChangeSource(native);
            var snapshots = new List<VolumeReadinessSnapshot>();
            source.ReadinessChanged += snapshots.Add;
            Assert.That(source.Mode, Is.EqualTo(WatchReadinessMode.FilesystemMarks));

            var result = await source.ArmAndCatchUpAsync(new[] { okPath, brokenPath }, CancellationToken.None);
            Assert.That(source.Readiness.IsSettled, Is.True);
            Assert.That(source.Readiness.CaughtUpVolumes, Is.EqualTo(new[] { okPath }));
            var excluded = source.Readiness.ExcludedVolumes.Single();
            Assert.That(excluded.Volume, Is.EqualTo(brokenPath));
            Assert.That(excluded.ExclusionReason, Is.EqualTo(result.Errors[brokenPath]));
            Assert.That(snapshots, Has.Count.EqualTo(2));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, true);
        }
    }
}

internal sealed class FakeFanotifyVolumeNative : IFanotifyNative
{
    public FanotifyNativeCallResult InitializeResult { get; set; } = new(1, 0);
    public int InitializeCallCount { get; private set; }
    public uint InitializeFlags { get; private set; }
    public uint EventFileFlags { get; private set; }

    public Queue<FanotifyNativeCallResult> MarkResults { get; } = new();
    public List<FanotifyMarkCall> MarkCalls { get; } = new();
    public List<int> ClosedDescriptors { get; } = new();

    public int PollResult { get; set; } = 1;
    public bool ReadReturnsZero { get; set; }
    public bool ReadThrowsException { get; set; }
    public Queue<byte[]> ReadQueue { get; } = new();

    public Dictionary<FileHandleKey, int> HandleToFdMap { get; } = new();
    public Dictionary<int, string> FdToPathMap { get; } = new();

    public FanotifyNativeCallResult Initialize(uint flags, uint eventFileFlags)
    {
        InitializeCallCount++;
        InitializeFlags = flags;
        EventFileFlags = eventFileFlags;
        return InitializeResult;
    }

    public FanotifyNativeCallResult Mark(
        int descriptor,
        uint flags,
        ulong mask,
        int directoryDescriptor,
        string path)
    {
        MarkCalls.Add(new FanotifyMarkCall(descriptor, flags, mask, directoryDescriptor, path));
        return MarkResults.Count > 0 ? MarkResults.Dequeue() : new FanotifyNativeCallResult(0, 0);
    }

    public int Close(int descriptor)
    {
        ClosedDescriptors.Add(descriptor);
        OpenMountDescriptors.Remove(descriptor);
        return 0;
    }

    public int Poll(int descriptor, int timeoutMilliseconds)
    {
        if (PollResult < 0)
            return PollResult;

        if (ReadThrowsException || ReadReturnsZero)
            return 1;

        if (ReadQueue.Count > 0)
            return 1;

        return 0;
    }

    public int Read(int descriptor, byte[] buffer, int count)
    {
        if (ReadThrowsException)
            throw new InvalidOperationException("Simulated read exception");

        if (ReadReturnsZero)
            return 0;

        if (ReadQueue.Count == 0)
            return 0;

        var source = ReadQueue.Dequeue();
        var bytesToCopy = Math.Min(source.Length, count);
        Buffer.BlockCopy(source, 0, buffer, 0, bytesToCopy);
        return bytesToCopy;
    }

    public Dictionary<string, long> FilesystemIdByMountPath { get; } = new(StringComparer.Ordinal);
    public HashSet<string> UnopenableMountPaths { get; } = new(StringComparer.Ordinal);
    public HashSet<string> UnreadableIdentifierMountPaths { get; } = new(StringComparer.Ordinal);
    /// <summary>Roots whose mount was detached: opening the path now reaches the underlying filesystem.</summary>
    public HashSet<string> GoneMountPaths { get; } = new(StringComparer.Ordinal);
    public List<(string Path, int Descriptor)> OpenedDirectories { get; } = new();
    public List<(int MountDescriptor, byte[] Handle)> OpenByHandleCalls { get; } = new();
    /// <summary>Descriptors opened through <see cref="OpenDirectory"/> and not yet closed.</summary>
    public HashSet<int> OpenMountDescriptors { get; } = new();
    /// <summary>Paths of the roots each <see cref="OpenByHandle"/> call resolved through, in call order.</summary>
    public List<string> OpenByHandleRoots { get; } = new();
    readonly Dictionary<int, string> _rootByDescriptor = new();
    readonly Dictionary<int, string> _relativePathByDirectoryDescriptor = new();
    readonly Dictionary<int, string> _rootByDirectoryDescriptor = new();
    readonly Dictionary<int, long> _filesystemIdByDescriptor = new();
    int _nextMountDescriptor = 1000;
    const long UnderlyingFilesystemId = 99;

    public void MapHandle(long filesystemId, byte[] handlePayload, int descriptor, string path)
    {
        HandleToFdMap[new FileHandleKey(filesystemId, handlePayload)] = descriptor;
        FdToPathMap[descriptor] = path;
    }

    /// <summary>Maps a handle to a directory whose resolved path is the resolving root plus <paramref name="relativePath"/>.</summary>
    public void MapHandleUnderRoot(long filesystemId, byte[] handlePayload, int descriptor, string relativePath)
    {
        HandleToFdMap[new FileHandleKey(filesystemId, handlePayload)] = descriptor;
        _relativePathByDirectoryDescriptor[descriptor] = relativePath;
    }

    public FanotifyNativeCallResult OpenDirectory(string path)
    {
        if (UnopenableMountPaths.Contains(path) || !Directory.Exists(path))
            return new FanotifyNativeCallResult(-1, 2);

        var descriptor = _nextMountDescriptor++;
        if (!UnreadableIdentifierMountPaths.Contains(path))
        {
            _filesystemIdByDescriptor[descriptor] = GoneMountPaths.Contains(path)
                ? UnderlyingFilesystemId
                : FilesystemIdByMountPath.GetValueOrDefault(path);
        }

        OpenedDirectories.Add((path, descriptor));
        OpenMountDescriptors.Add(descriptor);
        _rootByDescriptor[descriptor] = path;
        return new FanotifyNativeCallResult(descriptor, 0);
    }

    public bool TryReadFilesystemIdentifier(int descriptor, out long filesystemId) =>
        _filesystemIdByDescriptor.TryGetValue(descriptor, out filesystemId);

    public int OpenByHandle(int mountDirectoryDescriptor, byte[] handle, int flags)
    {
        OpenByHandleCalls.Add((mountDirectoryDescriptor, handle));
        OpenByHandleRoots.Add(_rootByDescriptor[mountDirectoryDescriptor]);
        if (!_filesystemIdByDescriptor.TryGetValue(mountDirectoryDescriptor, out var filesystemId))
            return -1;

        var key = new FileHandleKey(filesystemId, handle);
        if (!HandleToFdMap.TryGetValue(key, out var fd))
            return -1;

        _rootByDirectoryDescriptor[fd] = _rootByDescriptor[mountDirectoryDescriptor];
        return fd;
    }

    public string? ReadLink(string path)
    {
        if (path.StartsWith("/proc/self/fd/", StringComparison.Ordinal)
            && int.TryParse(path["/proc/self/fd/".Length..], out var fd))
        {
            if (_relativePathByDirectoryDescriptor.TryGetValue(fd, out var relativePath))
                return Path.Combine(_rootByDirectoryDescriptor[fd], relativePath);

            return FdToPathMap.TryGetValue(fd, out var resolved) ? resolved : null;
        }

        return null;
    }
}

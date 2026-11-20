using System.IO.Pipelines;
using System.Runtime.Versioning;
using GitWizard.Watch;
using GitWizard.Watch.Linux;
using GitWizardTests.TestSupport;

namespace GitWizardTests.Watch.Linux;

[TestFixture]
[NonParallelizable]
public class LinuxWatchHelperHostTests
{
    sealed class FailingWriteStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new IOException("Broken pipe");
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("Broken pipe");
    }

    /// <summary>Accepts writes until <see cref="BreakPipe"/>, then fails them like a closed stdout pipe.</summary>
    sealed class BreakableWriteStream : Stream
    {
        volatile bool _broken;
        int _writes;
        readonly TaskCompletionSource _firstWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes when the first frame (hello) has been written.</summary>
        public Task FirstWrite => _firstWrite.Task;

        public void BreakPipe() => _broken = true;

        /// <summary>Lets the hello frame through, then fails every later write.</summary>
        public bool FailWritesAfterTheFirst { get; init; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => ThrowWhenBroken();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (FailWritesAfterTheFirst && Interlocked.Increment(ref _writes) > 1)
            {
                throw new IOException("Broken pipe");
            }

            _firstWrite.TrySetResult();

            ThrowWhenBroken();
        }

        void ThrowWhenBroken()
        {
            if (_broken)
            {
                throw new IOException("Broken pipe");
            }
        }
    }

    static Task<int> RunHost(HelperPipes pipes, IVolumeChangeSource source, TextWriter? diagnostics = null) =>
        Task.Run(() => LinuxWatchHelperHost.RunAsync(pipes.HelperInput, pipes.HelperOutput, diagnostics ?? TextWriter.Null,
            () => true, () => source));

    static async Task<int> FinishAsync(Task<int> host)
    {
        var exitCode = await host.WaitAsync(TestTimeouts.ShortHangGuard);
        return exitCode;
    }

    [Test]
    public async Task NotRoot_RefusesBeforeCreatingTheSource()
    {
        await using var pipes = new HelperPipes();
        await using var diagnostics = new StringWriter();
        var created = 0;

        var exitCode = await LinuxWatchHelperHost.RunAsync(pipes.HelperInput, pipes.HelperOutput, diagnostics,
            () => false, () =>
            {
                created++;
                return new ReadinessVolumeChangeSource();
            });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exitCode, Is.EqualTo(LinuxWatchHelperHost.ExitCodeNotRoot));
            Assert.That(created, Is.Zero);
            Assert.That(diagnostics.ToString(), Does.Contain("must run as root"));
        }
    }

    [Test]
    public void TryRun_WithoutTheFlag_LeavesNormalStartupAlone()
    {
        Assert.That(LinuxWatchHelperHost.TryRun(["--broker", "-v"]), Is.Null);
        if (!OperatingSystem.IsLinux())
        {
            Assert.That(LinuxWatchHelperHost.TryRun([LinuxWatchHelperHost.Flag]), Is.EqualTo(LinuxWatchHelperHost.ExitCodeNotRoot));
        }
    }

    [Test]
    public async Task Arm_ReportsHelloReadinessAndArmResult()
    {
        await using var pipes = new HelperPipes();
        var source = new ReadinessVolumeChangeSource
        {
            Mode = WatchReadinessMode.FilesystemMarks,
            ArmErrors = new Dictionary<string, string> { ["/missing"] = "Directory does not exist: /missing" }
        };
        var host = RunHost(pipes, source);

        Assert.That(await pipes.ReadFrameAsync(), Is.EqualTo(new HelloFrame(LinuxWatchHelperProtocol.Version)));
        await pipes.SendRequestAsync(new ArmRequestFrame(["/repos", "/missing"]));
        var (armResult, _) = await pipes.ReadFrameAsync<ArmResultFrame>();
        source.SetVolume(VolumeWatchStatus.CaughtUp("/repos", VolumeBlockOrigin.Unknown));
        var (readiness, _) = await pipes.ReadFrameAsync<ReadinessFrame>();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(source.ArmedVolumes, Is.EqualTo(["/repos", "/missing"]));
            Assert.That(armResult.Errors, Is.EqualTo(source.ArmErrors));
            Assert.That(readiness.Volumes, Is.EqualTo([VolumeWatchStatus.CaughtUp("/repos", VolumeBlockOrigin.Unknown)]));
        }

        await pipes.CloseClientInputAsync();
        Assert.That(await FinishAsync(host), Is.EqualTo(LinuxWatchHelperHost.ExitCodeEndOfInput));
    }

    [Test]
    [Platform("Linux")]
    [SupportedOSPlatform("linux")]
    public async Task Arm_WithRootsThatDifferOnlyInCaseAndBothMarksFailing_ReportsEachExactRoot()
    {
        using var parent = new TempDirectory("gw-helper-host-case-");
        var upper = Path.Combine(parent.Path, "Repos");
        var lower = Path.Combine(parent.Path, "repos");
        Directory.CreateDirectory(upper);
        Directory.CreateDirectory(lower);
        await using var pipes = new HelperPipes();
        var native = new FakeFanotifyNative(1, new FanotifyNativeCallResult(-1, FanotifyError.NotPermitted),
            new FanotifyNativeCallResult(-1, FanotifyError.NotPermitted));
        await using var source = new FanotifyVolumeChangeSource(native);
        var host = RunHost(pipes, source);

        Assert.That(await pipes.ReadFrameAsync(), Is.EqualTo(new HelloFrame(LinuxWatchHelperProtocol.Version)));
        await pipes.SendRequestAsync(new ArmRequestFrame([upper, lower]));
        var (armResult, _) = await pipes.ReadFrameAsync<ArmResultFrame>();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(armResult.Errors.Keys, Is.EquivalentTo([upper, lower]));
            Assert.That(armResult.Errors[upper], Does.Contain(upper));
            Assert.That(armResult.Errors[lower], Does.Contain(lower));
        }

        await pipes.CloseClientInputAsync();
        await FinishAsync(host);
    }

    [Test]
    [Platform("Linux")]
    [SupportedOSPlatform("linux")]
    public async Task Arm_WithRootsThatDifferOnlyInCase_KeepsTheHealthyRootsReadinessBesideTheFailedOne()
    {
        using var parent = new TempDirectory("gw-helper-host-case-");
        var upper = Path.Combine(parent.Path, "Repos");
        var lower = Path.Combine(parent.Path, "repos");
        Directory.CreateDirectory(upper);
        Directory.CreateDirectory(lower);
        await using var pipes = new HelperPipes();
        var native = new FakeFanotifyNative(1, new FanotifyNativeCallResult(0, 0),
            new FanotifyNativeCallResult(-1, FanotifyError.NotPermitted));
        await using var source = new FanotifyVolumeChangeSource(native);
        var host = RunHost(pipes, source);

        Assert.That(await pipes.ReadFrameAsync(), Is.EqualTo(new HelloFrame(LinuxWatchHelperProtocol.Version)));
        await pipes.SendRequestAsync(new ArmRequestFrame([upper, lower]));
        var (armResult, before) = await pipes.ReadFrameAsync<ArmResultFrame>();
        var finalReadiness = before.OfType<ReadinessFrame>().Last();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(armResult.Errors.Keys, Is.EqualTo([lower]));
            Assert.That(finalReadiness.Volumes.Select(volume => (volume.Volume, volume.State)), Is.EquivalentTo(
            [
                (upper, VolumeWatchState.CaughtUp),
                (lower, VolumeWatchState.Excluded)
            ]));
        }

        await pipes.CloseClientInputAsync();
        await FinishAsync(host);
    }

    [Test]
    public async Task Watch_StreamsPumpStartAndBatchesInOrder_ThenDriveFailureAndDeath()
    {
        await using var pipes = new HelperPipes();
        var source = new ReadinessVolumeChangeSource(autoStartPump: false) { Mode = WatchReadinessMode.FilesystemMarks };
        var host = RunHost(pipes, source);
        await pipes.SendRequestAsync(new ArmRequestFrame(["/repos"]));
        await pipes.ReadFrameAsync<ArmResultFrame>();

        await pipes.SendRequestAsync(new WatchRequestFrame());
        await source.Started.WaitAsync(TestTimeouts.ShortHangGuard);
        source.SignalPumpStarted();
        await pipes.ReadFrameAsync<PumpStartedFrame>();
        var first = new VolumeChangeBatch("/", [new VolumeChangeEntry("/repos/a/file", VolumeEntryKind.Created)]);
        var second = new VolumeChangeBatch("/", [new VolumeChangeEntry("/repos/a/file", VolumeEntryKind.Deleted)]);
        source.Publish(first);
        source.Publish(second);
        var firstFrame = await pipes.ReadFrameAsync();
        var secondFrame = await pipes.ReadFrameAsync();
        source.FailDrive("/repos", "watch failed");
        var failure = await pipes.ReadFrameAsync();
        source.KillSource("fanotify event queue overflowed.");
        var died = await pipes.ReadFrameAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(((BatchFrame)firstFrame!).Batch.Entries, Is.EqualTo(first.Entries));
            Assert.That(((BatchFrame)secondFrame!).Batch.Entries, Is.EqualTo(second.Entries));
            Assert.That(failure, Is.EqualTo(new DriveFailedFrame("/repos", "watch failed")));
            Assert.That(died, Is.EqualTo(new DiedFrame("fanotify event queue overflowed.")));
            Assert.That(await FinishAsync(host), Is.EqualTo(LinuxWatchHelperHost.ExitCodeSourceEnded));
            Assert.That(source.DisposeCount, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task Watch_ForwardsOnlyEntriesAtUnderOrBesideAnArmedRoot()
    {
        await using var pipes = new HelperPipes();
        var source = new ReadinessVolumeChangeSource { Mode = WatchReadinessMode.FilesystemMarks };
        var host = RunHost(pipes, source);
        await pipes.SendRequestAsync(new ArmRequestFrame(["/srv/data/repos"]));
        await pipes.ReadFrameAsync<ArmResultFrame>();
        await pipes.SendRequestAsync(new WatchRequestFrame());
        await pipes.ReadFrameAsync<PumpStartedFrame>();

        source.Publish(new VolumeChangeBatch("/", [new VolumeChangeEntry("/var/log/journal/system.journal", VolumeEntryKind.Modified)]));
        source.Publish(new VolumeChangeBatch("/",
        [
            new VolumeChangeEntry("/root/.bash_history", VolumeEntryKind.Modified),
            new VolumeChangeEntry("/srv/data/repos/a/.git/index", VolumeEntryKind.Modified),
            new VolumeChangeEntry("/srv/data/repos", VolumeEntryKind.Deleted),
            new VolumeChangeEntry("/srv/data/repos-moved", VolumeEntryKind.MovedIn),
            new VolumeChangeEntry("/srv/data/reposition/file", VolumeEntryKind.Created),
            new VolumeChangeEntry("/srv/data/.cache/x", VolumeEntryKind.Created)
        ]));
        var (batch, before) = await pipes.ReadFrameAsync<BatchFrame>();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(before, Is.Empty, "A batch with no relevant entry is not sent.");
            Assert.That(batch.Batch.Entries, Is.EqualTo(
            [
                new VolumeChangeEntry("/srv/data/repos/a/.git/index", VolumeEntryKind.Modified),
                new VolumeChangeEntry("/srv/data/repos", VolumeEntryKind.Deleted),
                new VolumeChangeEntry("/srv/data/repos-moved", VolumeEntryKind.MovedIn)
            ]));
        }

        await pipes.CloseClientInputAsync();
        await FinishAsync(host);
    }

    [Test]
    public async Task EndOfInput_ExitsAndDisposesTheSource()
    {
        await using var pipes = new HelperPipes();
        var source = new ReadinessVolumeChangeSource { Mode = WatchReadinessMode.FilesystemMarks };
        var host = RunHost(pipes, source);
        await pipes.SendRequestAsync(new ArmRequestFrame(["/repos"]));
        await pipes.ReadFrameAsync<ArmResultFrame>();
        await pipes.SendRequestAsync(new WatchRequestFrame());
        await source.Started.WaitAsync(TestTimeouts.ShortHangGuard);

        await pipes.CloseClientInputAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await FinishAsync(host), Is.EqualTo(LinuxWatchHelperHost.ExitCodeEndOfInput));
            Assert.That(source.DisposeCount, Is.EqualTo(1));
        }
    }

    [TestCase(false, TestName = "EndOfInput_ExitsWhileNativeMarkIsStillHeld")]
    [TestCase(true, TestName = "EndOfInput_ExitsWhileNativePumpIsStillHeld")]
    [SupportedOSPlatform("linux")]
    public async Task EndOfInput_ExitsWhileNativeOperationIsStillHeld(bool holdPump)
    {
        using var root = new TempDirectory();
        await using var pipes = new HelperPipes();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deadlineStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expire = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Hold()
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        }

        const int descriptor = 9;
        var native = new FakeFanotifyNative
        {
            InitializeResult = new FanotifyNativeCallResult(descriptor, 0),
            OnMark = holdPump ? null : Hold,
            OnPoll = holdPump ? Hold : null,
            OnClose = value =>
            {
                if (value == descriptor)
                {
                    closed.SetResult();
                }
            }
        };
        var source = new FanotifyVolumeChangeSource(native);
        var host = LinuxWatchHelperHost.RunAsync(pipes.HelperInput, pipes.HelperOutput, TextWriter.Null,
            () => true, () => source, () =>
            {
                deadlineStarted.SetResult();
                return expire.Task;
            });
        try
        {
            await pipes.ReadFrameAsync<HelloFrame>();
            await pipes.SendRequestAsync(new ArmRequestFrame([root.Path]));
            if (holdPump)
            {
                var (arm, _) = await pipes.ReadFrameAsync<ArmResultFrame>();
                Assert.That(arm.Errors, Is.Empty);
                await pipes.SendRequestAsync(new WatchRequestFrame());
                await pipes.ReadFrameAsync<PumpStartedFrame>();
            }

            await entered.Task.WaitAsync(TestTimeouts.ShortHangGuard);
            Assert.That(deadlineStarted.Task.IsCompleted, Is.False);
            await pipes.CloseClientInputAsync();
            await deadlineStarted.Task.WaitAsync(TestTimeouts.ShortHangGuard);
            Assert.That(host.IsCompleted, Is.False);
            expire.SetResult();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(await FinishAsync(host), Is.EqualTo(LinuxWatchHelperHost.ExitCodeShutdownTimedOut));
                Assert.That(closed.Task.IsCompleted, Is.False, "The native call still owns its descriptor.");
            }
        }
        finally
        {
            release.TrySetResult();
            expire.TrySetResult();
            if (entered.Task.IsCompleted)
            {
                await closed.Task.WaitAsync(TestTimeouts.ShortHangGuard);
            }
        }
    }

    [Test]
    public async Task EndOfInput_WithAnOpenStalledOutput_StillDisposesAndExits()
    {
        await using var pipes = new HelperPipes();
        var output = new Pipe(new PipeOptions(pauseWriterThreshold: 1, resumeWriterThreshold: 1));
        await using var writer = output.Writer.AsStream();
        var source = new ReadinessVolumeChangeSource();
        var host = LinuxWatchHelperHost.RunAsync(pipes.HelperInput, writer, TextWriter.Null, () => true, () => source);
        try
        {
            // Observe hello without consuming it: the writer cannot finish flushing this frame.
            await output.Reader.ReadAsync().AsTask().WaitAsync(TestTimeouts.ShortHangGuard);
            await pipes.CloseClientInputAsync();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(await FinishAsync(host), Is.EqualTo(LinuxWatchHelperHost.ExitCodeEndOfInput));
                Assert.That(source.DisposeCount, Is.EqualTo(1));
            }
        }
        finally
        {
            await output.Reader.CompleteAsync();
        }
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task TerminalFrames_AfterInputCloses_DrainUntilTheShutdownDeadline(bool drain)
    {
        await using var pipes = new HelperPipes();
        var output = new Pipe(new PipeOptions(pauseWriterThreshold: 1, resumeWriterThreshold: 1));
        await using var writer = output.Writer.AsStream();
        await using var reader = output.Reader.AsStream();
        using var lines = new StreamReader(reader);
        var deadlineStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expire = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new ReadinessVolumeChangeSource();
        var host = LinuxWatchHelperHost.RunAsync(pipes.HelperInput, writer, TextWriter.Null, () => true, () => source,
            () =>
            {
                deadlineStarted.SetResult();
                return expire.Task;
            });
        await lines.ReadLineAsync().WaitAsync(TestTimeouts.ShortHangGuard);
        source.FailDrive("/repos", "drive failed");
        source.KillSource("source ended");
        await pipes.CloseClientInputAsync();
        await deadlineStarted.Task.WaitAsync(TestTimeouts.ShortHangGuard);

        if (drain)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(await lines.ReadLineAsync().WaitAsync(TestTimeouts.ShortHangGuard),
                    Is.EqualTo(LinuxWatchHelperProtocol.Serialize(new DriveFailedFrame("/repos", "drive failed"))));
                Assert.That(await lines.ReadLineAsync().WaitAsync(TestTimeouts.ShortHangGuard),
                    Is.EqualTo(LinuxWatchHelperProtocol.Serialize(new DiedFrame("source ended"))));
                Assert.That(await FinishAsync(host), Is.EqualTo(LinuxWatchHelperHost.ExitCodeSourceEnded));
            }
        }
        else
        {
            expire.SetResult();
            Assert.That(await FinishAsync(host), Is.EqualTo(LinuxWatchHelperHost.ExitCodeShutdownTimedOut));
        }
    }

    [Test]
    public async Task OutputWriteFailure_Exits()
    {
        await using var pipes = new HelperPipes();
        var source = new ReadinessVolumeChangeSource();

        var exitCode = await LinuxWatchHelperHost.RunAsync(pipes.HelperInput, new FailingWriteStream(), TextWriter.Null,
            () => true, () => source).WaitAsync(TestTimeouts.ShortHangGuard);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exitCode, Is.EqualTo(LinuxWatchHelperHost.ExitCodeOutputFailed));
            Assert.That(source.DisposeCount, Is.EqualTo(1));
        }
    }

    [Test]
    [SupportedOSPlatform("linux")]
    public async Task OutputWriteFailure_WithInputStillOpen_ExitsWhileNativeMarkIsStillHeld()
    {
        using var root = new TempDirectory();
        await using var pipes = new HelperPipes();
        var output = new BreakableWriteStream();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deadlineStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expire = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        const int descriptor = 9;
        var native = new FakeFanotifyNative
        {
            InitializeResult = new FanotifyNativeCallResult(descriptor, 0),
            OnMark = () =>
            {
                entered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
            },
            OnClose = value =>
            {
                if (value == descriptor)
                {
                    closed.TrySetResult();
                }
            }
        };
        var host = LinuxWatchHelperHost.RunAsync(pipes.HelperInput, output, TextWriter.Null,
            () => true, () => new FanotifyVolumeChangeSource(native), () =>
            {
                deadlineStarted.TrySetResult();
                return expire.Task;
            });
        try
        {
            await pipes.SendRequestAsync(new ArmRequestFrame([root.Path]));
            await entered.Task.WaitAsync(TestTimeouts.ShortHangGuard);
            Assert.That(deadlineStarted.Task.IsCompleted, Is.False);

            // Stdin stays open; a rejected request makes the writer try the broken pipe.
            output.BreakPipe();
            await pipes.SendRequestLineAsync("not json");
            await deadlineStarted.Task.WaitAsync(TestTimeouts.ShortHangGuard);
            Assert.That(host.IsCompleted, Is.False);
            expire.SetResult();
            Assert.That(await FinishAsync(host), Is.EqualTo(LinuxWatchHelperHost.ExitCodeShutdownTimedOut));
        }
        finally
        {
            release.TrySetResult();
            expire.TrySetResult();
            if (entered.Task.IsCompleted)
            {
                // Join the abandoned cleanup so nothing touches the scratch root after it is deleted.
                await closed.Task.WaitAsync(TestTimeouts.ShortHangGuard);
            }
        }
    }

    [Test]
    public async Task SourceDeath_WithInputStillOpen_ExitsWhileCleanupIsStillHeld()
    {
        await using var pipes = new HelperPipes();
        var cleanupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deadlineStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expire = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new ReadinessVolumeChangeSource
        {
            DisposeGate = () =>
            {
                cleanupEntered.TrySetResult();
                return new ValueTask(release.Task);
            }
        };
        var host = LinuxWatchHelperHost.RunAsync(pipes.HelperInput, pipes.HelperOutput, TextWriter.Null,
            () => true, () => source, () =>
            {
                deadlineStarted.TrySetResult();
                return expire.Task;
            });
        try
        {
            await pipes.ReadFrameAsync<HelloFrame>();
            Assert.That(deadlineStarted.Task.IsCompleted, Is.False);

            source.KillSource("source ended");
            await cleanupEntered.Task.WaitAsync(TestTimeouts.ShortHangGuard);
            await deadlineStarted.Task.WaitAsync(TestTimeouts.ShortHangGuard);
            Assert.That(host.IsCompleted, Is.False);
            expire.SetResult();
            Assert.That(await FinishAsync(host), Is.EqualTo(LinuxWatchHelperHost.ExitCodeShutdownTimedOut));
        }
        finally
        {
            release.TrySetResult();
            expire.TrySetResult();
            if (cleanupEntered.Task.IsCompleted)
            {
                // Join the abandoned cleanup so no work outlives the test.
                await source.DisposeCompleted.WaitAsync(TestTimeouts.ShortHangGuard);
            }
        }
    }

    [Test]
    public async Task SourceDeath_WhenTheDiedFrameCannotBeDelivered_StillExitsWithTheSourceReason()
    {
        await using var pipes = new HelperPipes();
        var source = new ReadinessVolumeChangeSource();
        var output = new BreakableWriteStream { FailWritesAfterTheFirst = true };
        var host = LinuxWatchHelperHost.RunAsync(pipes.HelperInput, output, TextWriter.Null, () => true, () => source);
        await output.FirstWrite.WaitAsync(TestTimeouts.ShortHangGuard);

        source.KillSource("source ended");

        Assert.That(await FinishAsync(host), Is.EqualTo(LinuxWatchHelperHost.ExitCodeSourceEnded));
    }

    [Test]
    public async Task UnexpectedInputFailure_ExitsAndDisposesTheSource()
    {
        var input = new Pipe();
        await using var reader = input.Reader.AsStream();
        await using var diagnostics = new StringWriter();
        var source = new ReadinessVolumeChangeSource();
        var host = LinuxWatchHelperHost.RunAsync(reader, Stream.Null, diagnostics, () => true, () => source);

        await input.Writer.CompleteAsync(new InvalidOperationException("Synthetic reader failure"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await FinishAsync(host), Is.EqualTo(LinuxWatchHelperHost.ExitCodeEndOfInput));
            Assert.That(source.DisposeCount, Is.EqualTo(1));
            Assert.That(diagnostics.ToString(), Does.Contain("Synthetic reader failure"));
        }
    }

    [TestCase("not json")]
    [TestCase(LinuxWatchHelperProtocolTests.UndecodableFrame)]
    [TestCase("{\"type\":\"arm\",\"roots\":[\"relative/path\"]}")]
    [TestCase("{\"type\":\"arm\",\"roots\":[]}")]
    [TestCase("{\"type\":\"watch\"}")]
    [TestCase("{\"type\":\"hello\",\"protocol\":1}")]
    public async Task MalformedOrOutOfOrderRequest_IsRejectedAndTheHelperKeepsServing(string request)
    {
        await using var pipes = new HelperPipes();
        var source = new ReadinessVolumeChangeSource();
        var host = RunHost(pipes, source);
        await pipes.ReadFrameAsync<HelloFrame>();

        await pipes.SendRequestLineAsync(request);
        var rejection = await pipes.ReadFrameAsync();
        await pipes.SendRequestAsync(new ArmRequestFrame(["/repos"]));
        var (armResult, _) = await pipes.ReadFrameAsync<ArmResultFrame>();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rejection, Is.TypeOf<ErrorFrame>());
            Assert.That(((ErrorFrame)rejection!).Message, Does.StartWith("Rejected request:"));
            Assert.That(armResult.Errors, Is.Empty);
            Assert.That(source.ArmedVolumes, Is.EqualTo(["/repos"]));
        }

        await pipes.CloseClientInputAsync();
        Assert.That(await FinishAsync(host), Is.EqualTo(LinuxWatchHelperHost.ExitCodeEndOfInput));
    }

    [Test]
    public async Task SecondArm_IsRejected()
    {
        await using var pipes = new HelperPipes();
        var host = RunHost(pipes, new ReadinessVolumeChangeSource());
        await pipes.SendRequestAsync(new ArmRequestFrame(["/repos"]));
        await pipes.ReadFrameAsync<ArmResultFrame>();

        await pipes.SendRequestAsync(new ArmRequestFrame(["/other"]));
        var (rejection, _) = await pipes.ReadFrameAsync<ErrorFrame>();

        Assert.That(rejection.Message, Does.Contain("already armed"));
        await pipes.CloseClientInputAsync();
        await FinishAsync(host);
    }
}

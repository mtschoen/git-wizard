using GitWizard.Watch;
using GitWizard.Watch.Linux;
using GitWizardTests.TestSupport;

namespace GitWizardTests.Watch.Linux;

[TestFixture]
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
    public void TryRun_WithoutTheFlag_LeavesNormalStartupAlone() =>
        Assert.That(LinuxWatchHelperHost.TryRun(["--broker", "-v"]), Is.Null);

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
            Assert.That(source.ArmedVolumes, Is.EqualTo(new[] { "/repos", "/missing" }));
            Assert.That(armResult.Errors, Is.EqualTo(source.ArmErrors));
            Assert.That(readiness.Volumes, Is.EqualTo(new[] { VolumeWatchStatus.CaughtUp("/repos", VolumeBlockOrigin.Unknown) }));
        }

        await pipes.CloseClientInputAsync();
        Assert.That(await FinishAsync(host), Is.EqualTo(LinuxWatchHelperHost.ExitCodeEndOfInput));
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
            Assert.That(batch.Batch.Entries, Is.EqualTo(new[]
            {
                new VolumeChangeEntry("/srv/data/repos/a/.git/index", VolumeEntryKind.Modified),
                new VolumeChangeEntry("/srv/data/repos", VolumeEntryKind.Deleted),
                new VolumeChangeEntry("/srv/data/repos-moved", VolumeEntryKind.MovedIn)
            }));
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

    [TestCase("not json")]
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
            Assert.That(source.ArmedVolumes, Is.EqualTo(new[] { "/repos" }));
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

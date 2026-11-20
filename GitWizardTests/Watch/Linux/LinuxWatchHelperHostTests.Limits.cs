using GitWizard.Watch;
using GitWizard.Watch.Linux;
using GitWizardTests.TestSupport;

namespace GitWizardTests.Watch.Linux;

// Request and output limits and the root filter, split from LinuxWatchHelperHostTests to keep each file small.
public partial class LinuxWatchHelperHostTests
{
    /// <summary>Accepts a few writes, then leaves every later write pending until released, like a pipe nobody reads.</summary>
    sealed class StallingWriteStream(int writesBeforeStall) : Stream
    {
        readonly Lock _gate = new();
        readonly List<byte> _written = [];
        readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource _firstWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int _writes;

        public Task FirstWrite => _firstWrite.Task;
        public void Release() => _release.TrySetResult();

        public string[] WrittenLines
        {
            get
            {
                lock (_gate)
                {
                    return System.Text.Encoding.UTF8.GetString([.. _written]).Split('\n', StringSplitOptions.RemoveEmptyEntries);
                }
            }
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush()
        {
            // Nothing is buffered here.
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _writes) > writesBeforeStall)
            {
                await _release.Task;
                return;
            }

            lock (_gate)
            {
                _written.AddRange(buffer.ToArray());
            }

            _firstWrite.TrySetResult();
        }
    }

    /// <summary>A stdin whose one line is produced lazily and never held, then ends.</summary>
    sealed class LazyLineStream(long lineBytes) : Stream
    {
        long _produced;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(Span<byte> buffer)
        {
            if (_produced > lineBytes)
            {
                return 0;
            }

            var count = (int)Math.Min(buffer.Length, lineBytes - _produced);
            buffer[..count].Fill((byte)'x');
            _produced += count;
            if (count < buffer.Length)
            {
                buffer[count] = (byte)'\n';
                _produced++;
                count++;
            }

            return count;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));
    }

    static string ArmRequestOfExactLength(int length)
    {
        const string prefix = "{\"type\":\"arm\",\"roots\":[\"/";
        const string suffix = "\"]}";
        return prefix + new string('a', length - prefix.Length - suffix.Length) + suffix;
    }

    [Test]
    public async Task Watch_Filter_ForwardsExactRootDescendantAndDirectSiblingOnly_WithCaseDistinctRoots()
    {
        string[] roots = ["/x/Repos", "/x/repos", "/y/deep/Other"];
        (string Path, bool Forwarded)[] cases =
        [
            ("/x/Repos", true),
            ("/x/repos", true),
            ("/x/Repos/a/b/c", true),
            ("/x/repos/a", true),
            ("/x/REPOS/a", false),
            ("/x/REPOS", true),
            ("/x/Repos2/file", false),
            ("/x/Repos2", true),
            ("/x/Repo", true),
            ("/x/other", true),
            ("/x/other/deep", false),
            ("/x", false),
            ("/x/", true),
            ("/y/deep/Other/inner", true),
            ("/y/deep/sibling", true),
            ("/y/deep/sibling/inner", false),
            ("/y/deep", false),
            ("/y/other", false),
            ("/z/Repos", false),
            ("/", false)
        ];
        await using var pipes = new HelperPipes();
        var source = new ReadinessVolumeChangeSource { Mode = WatchReadinessMode.FilesystemMarks };
        var host = RunHost(pipes, source);
        await pipes.SendRequestAsync(new ArmRequestFrame(roots));
        await pipes.ReadFrameAsync<ArmResultFrame>();
        await pipes.SendRequestAsync(new WatchRequestFrame());
        await pipes.ReadFrameAsync<PumpStartedFrame>();

        source.Publish(new VolumeChangeBatch("/", [.. cases.Select(entry => new VolumeChangeEntry(entry.Path, VolumeEntryKind.Modified))]));
        var (batch, before) = await pipes.ReadFrameAsync<BatchFrame>();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(before, Is.Empty);
            Assert.That(batch.Batch.Entries.Select(entry => entry.FullPath),
                Is.EqualTo(cases.Where(entry => entry.Forwarded).Select(entry => entry.Path)));
        }

        await pipes.CloseClientInputAsync();
        await FinishAsync(host);
    }

    [Test]
    public async Task StalledOutput_BeyondTheQueuedByteLimit_StopsWithOutputFailedWhileInputIsStillOpen()
    {
        await using var pipes = new HelperPipes();
        await using var diagnostics = new StringWriter();
        using var output = new StallingWriteStream(writesBeforeStall: 2);
        var deadlineStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var neverExpires = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new ReadinessVolumeChangeSource();
        var host = LinuxWatchHelperHost.RunAsync(pipes.HelperInput, output, diagnostics, () => true, () => source,
            new LinuxWatchHelperOptions(() =>
            {
                deadlineStarted.TrySetResult();
                return neverExpires.Task;
            }, new LinuxWatchHelperLimits(1024 * 1024, 400)));
        try
        {
            // The first write is hello, so the session has subscribed to the source by now.
            await output.FirstWrite.WaitAsync(TestTimeouts.ShortHangGuard);
            var produced = new List<string> { LinuxWatchHelperProtocol.Serialize(new HelloFrame(LinuxWatchHelperProtocol.Version)) };
            for (var index = 0; index < 50; index++)
            {
                var message = $"drive failed {index}";
                produced.Add(LinuxWatchHelperProtocol.Serialize(new DriveFailedFrame("/repos", message)));
                source.FailDrive("/repos", message);
            }

            await deadlineStarted.Task.WaitAsync(TestTimeouts.ShortHangGuard);
            var exitCode = await FinishAsync(host);
            var written = output.WrittenLines;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(exitCode, Is.EqualTo(LinuxWatchHelperHost.ExitCodeOutputFailed));
                Assert.That(source.DisposeCount, Is.EqualTo(1));
                Assert.That(written, Is.EqualTo(produced.Take(written.Length)), "Written frames are an ordered prefix; none was skipped.");
                Assert.That(written, Has.Length.LessThanOrEqualTo(2));
                Assert.That(diagnostics.ToString(), Does.Contain("Desktop stopped reading").And.Contain("bytes queued"));
            }
        }
        finally
        {
            output.Release();
            neverExpires.TrySetResult();
        }
    }

    [TestCase(0, true, TestName = "RequestAtTheByteLimit_IsParsed")]
    [TestCase(1, false, TestName = "RequestOneByteOverTheLimit_IsRejectedWithoutParsing")]
    public async Task Request_AtAndOverTheByteLimit(int bytesOverLimit, bool accepted)
    {
        const int limit = 200;
        await using var pipes = new HelperPipes();
        var source = new ReadinessVolumeChangeSource();
        var host = RunHost(pipes, source, limits: new LinuxWatchHelperLimits(limit, 1024 * 1024));
        await pipes.ReadFrameAsync<HelloFrame>();

        await pipes.SendRequestLineAsync(ArmRequestOfExactLength(limit + bytesOverLimit));
        var first = accepted ? (await pipes.ReadFrameAsync<ArmResultFrame>()).Frame : await pipes.ReadFrameAsync();
        await pipes.CloseClientInputAsync();

        using (Assert.EnterMultipleScope())
        {
            if (accepted)
            {
                Assert.That(first, Is.TypeOf<ArmResultFrame>());
                Assert.That(source.ArmedVolumes, Has.Count.EqualTo(1));
            }
            else
            {
                Assert.That(first, Is.TypeOf<ErrorFrame>());
                Assert.That(((ErrorFrame)first!).Message, Is.EqualTo($"Rejected request: the request exceeds {limit} bytes."));
                Assert.That(source.ArmedVolumes, Is.Null);
            }

            Assert.That(await FinishAsync(host), Is.EqualTo(LinuxWatchHelperHost.ExitCodeEndOfInput));
        }
    }

    [Test]
    public async Task OversizeRequest_IsDiscardedAcrossReadsAndTheHelperKeepsServing()
    {
        await using var pipes = new HelperPipes();
        var source = new ReadinessVolumeChangeSource();
        var host = RunHost(pipes, source, limits: new LinuxWatchHelperLimits(64, 1024 * 1024));
        await pipes.ReadFrameAsync<HelloFrame>();

        await pipes.SendRequestLineAsync(new string('x', 50_000));
        var rejection = await pipes.ReadFrameAsync();
        await pipes.SendRequestAsync(new ArmRequestFrame(["/repos"]));
        var (armResult, _) = await pipes.ReadFrameAsync<ArmResultFrame>();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rejection, Is.TypeOf<ErrorFrame>());
            Assert.That(armResult.Errors, Is.Empty);
            Assert.That(source.ArmedVolumes, Is.EqualTo(["/repos"]));
        }

        await pipes.CloseClientInputAsync();
        await FinishAsync(host);
    }

    [Test]
    public async Task RequestOverTheDefaultSixteenMebibyteLimit_IsRejectedWithoutBeingMaterialized()
    {
        await using var pipes = new HelperPipes();
        var source = new ReadinessVolumeChangeSource();
        var host = LinuxWatchHelperHost.RunAsync(new LazyLineStream(LinuxWatchHelperLimits.DefaultMaximumRequestBytes + 1),
            pipes.HelperOutput, TextWriter.Null, () => true, () => source);

        await pipes.ReadFrameAsync<HelloFrame>();
        var (rejection, _) = await pipes.ReadFrameAsync<ErrorFrame>();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rejection.Message, Is.EqualTo("Rejected request: the request exceeds 16 MiB."));
            Assert.That(await FinishAsync(host), Is.EqualTo(LinuxWatchHelperHost.ExitCodeEndOfInput));
        }
    }
}

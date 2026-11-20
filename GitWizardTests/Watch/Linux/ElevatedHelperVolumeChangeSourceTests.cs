using System.ComponentModel;
using System.Diagnostics;
using GitWizard;
using GitWizard.Watch;
using GitWizard.Watch.Linux;
using GitWizardTests.TestSupport;

namespace GitWizardTests.Watch.Linux;

[TestFixture]
public class ElevatedHelperVolumeChangeSourceTests
{
    [SetUp]
    public void SetUp() => GitWizardLog.SilentMode = true;

    sealed class RecordingLauncher(HelperPipes pipes)
    {
        public List<ProcessStartInfo> Starts { get; } = [];

        public LinuxWatchHelperProcess Launch(ProcessStartInfo startInfo)
        {
            Starts.Add(startInfo);
            return pipes.Process;
        }
    }

    static ElevatedHelperVolumeChangeSource CreateSource(LinuxWatchHelperLauncher launcher, TimeProvider? clock = null) =>
        new(LinuxWatchHelperLaunch.CreatePkexecStartInfo("/opt/git-wizard/GitWizardUI"), launcher,
            clock ?? TimeProvider.System, TestTimeouts.ShortHangGuard, _ => false);

    // Plays the helper's side of a successful arm and returns the roots the client requested.
    static async Task<IReadOnlyList<string>> AnswerArmAsync(HelperPipes pipes, IReadOnlyDictionary<string, string>? errors = null,
        params VolumeWatchStatus[] readiness)
    {
        await pipes.SendFrameAsync(new HelloFrame(LinuxWatchHelperProtocol.Version));
        var request = (ArmRequestFrame)(await pipes.ReadRequestAsync())!;
        if (readiness.Length > 0)
        {
            await pipes.SendFrameAsync(new ReadinessFrame(readiness));
        }

        await pipes.SendFrameAsync(new ArmResultFrame(errors ?? new Dictionary<string, string>()));
        return request.Roots;
    }

    [Test]
    public async Task Arm_LaunchesPkexecOnce_AndMapsTheHelperArmResult()
    {
        await using var pipes = new HelperPipes();
        var launcher = new RecordingLauncher(pipes);
        await using var source = CreateSource(launcher.Launch);
        var events = new List<string>();
        source.Connecting += () => events.Add("connecting");
        source.Connected += () => events.Add("connected");

        var arm = source.ArmAndCatchUpAsync(["/repos", "/missing"], CancellationToken.None);
        var roots = await AnswerArmAsync(pipes, new Dictionary<string, string> { ["/missing"] = "Directory does not exist: /missing" },
            VolumeWatchStatus.CaughtUp("/repos", VolumeBlockOrigin.Unknown),
            VolumeWatchStatus.Excluded("/missing", VolumeBlockOrigin.Unknown, "Directory does not exist: /missing"));
        var result = await arm.WaitAsync(TestTimeouts.ShortHangGuard);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(launcher.Starts, Has.Count.EqualTo(1));
            Assert.That(launcher.Starts[0].FileName, Is.EqualTo("pkexec"));
            Assert.That(launcher.Starts[0].ArgumentList, Is.EqualTo(["/opt/git-wizard/GitWizardUI", LinuxWatchHelperHost.Flag]));
            Assert.That(launcher.Starts[0].RedirectStandardInput && launcher.Starts[0].RedirectStandardOutput &&
                        launcher.Starts[0].RedirectStandardError, Is.True);
            Assert.That(roots, Is.EqualTo(["/repos", "/missing"]));
            Assert.That(result.Errors, Is.EqualTo(new Dictionary<string, string> { ["/missing"] = "Directory does not exist: /missing" }));
            Assert.That(events, Is.EqualTo(["connecting", "connected"]));
            Assert.That(source.Mode, Is.EqualTo(WatchReadinessMode.FilesystemMarks));
            Assert.That(source.Readiness.CaughtUpVolumes, Is.EqualTo(["/repos"]));
            Assert.That(source.Readiness.ExcludedVolumes.Select(volume => volume.Volume), Is.EqualTo(["/missing"]));
        }
    }

    [Test]
    public async Task Arm_WithoutAbsoluteRoots_RaisesNoPrompt()
    {
        var launches = 0;
        await using var source = CreateSource(_ =>
        {
            launches++;
            throw new AssertionException("Nothing to watch must not launch pkexec.");
        });

        var result = await source.ArmAndCatchUpAsync(["relative/path", " "], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(launches, Is.Zero);
            Assert.That(result.Errors.Values, Is.All.EqualTo(ElevatedHelperVolumeChangeSource.RelativeRootMessage));
            Assert.That(result.Errors.Keys, Is.EquivalentTo(["relative/path", " "]));
        }
    }

    [Test]
    [Platform("Linux")]
    public async Task Arm_WithOnlyConclusivelyMissingRoots_RaisesNoPrompt()
    {
        using var parent = new TempDirectory("gw-helper-missing-");
        var missing = Path.Combine(parent.Path, "absent");
        var file = Path.Combine(parent.Path, "file");
        await File.WriteAllTextAsync(file, "not a directory");
        await using var source = new ElevatedHelperVolumeChangeSource(
            LinuxWatchHelperLaunch.CreatePkexecStartInfo("/opt/git-wizard/GitWizardUI"),
            _ => throw new AssertionException("A watch that cannot start must not prompt."), TimeProvider.System,
            TestTimeouts.ShortHangGuard);

        var result = await source.ArmAndCatchUpAsync([missing, file], CancellationToken.None);

        Assert.That(result.Errors, Is.EqualTo(new Dictionary<string, string>
        {
            [missing] = $"Directory does not exist: {missing}",
            [file] = $"Directory does not exist: {file}"
        }));
    }

    [TestCase(126, ElevatedHelperVolumeChangeSource.AuthenticationCancelledMessage)]
    [TestCase(127, ElevatedHelperVolumeChangeSource.AuthorizationFailedMessage)]
    [TestCase(1, "The elevated Live helper ended before it started (exit code 1).")]
    public async Task PkexecEndsBeforeTheHelperStarts_ReportsItInWords(int exitCode, string expected)
    {
        await using var pipes = new HelperPipes();
        await using var source = CreateSource(new RecordingLauncher(pipes).Launch);
        var deaths = new List<string>();
        source.SourceDied += deaths.Add;

        var arm = source.ArmAndCatchUpAsync(["/repos"], CancellationToken.None);
        await pipes.ExitAsync(exitCode);
        var result = await arm.WaitAsync(TestTimeouts.ShortHangGuard);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Errors, Is.EqualTo(new Dictionary<string, string> { ["/repos"] = expected }));
            Assert.That(source.Readiness.ExcludedVolumes.Single().ExclusionReason, Is.EqualTo(expected));
            Assert.That(deaths, Is.EqualTo([expected]));
        }
    }

    [TestCase(2, ElevatedHelperVolumeChangeSource.PkexecMissingMessage)]
    [TestCase(13, ElevatedHelperVolumeChangeSource.LaunchFailedMessage)]
    public async Task PkexecCannotStart_ReportsItInWords(int errorNumber, string expected)
    {
        await using var source = CreateSource(_ => throw new Win32Exception(errorNumber));

        var result = await source.ArmAndCatchUpAsync(["/repos"], CancellationToken.None);

        Assert.That(result.Errors, Is.EqualTo(new Dictionary<string, string> { ["/repos"] = expected }));
    }

    [Test]
    public async Task UnknownExecutable_ReportsItInWords()
    {
        await using var source = new ElevatedHelperVolumeChangeSource(LinuxWatchHelperLaunch.CreatePkexecStartInfo(null),
            _ => throw new AssertionException("No executable means no launch."), TimeProvider.System, TestTimeouts.ShortHangGuard,
            _ => false);

        var result = await source.ArmAndCatchUpAsync(["/repos"], CancellationToken.None);

        Assert.That(result.Errors["/repos"], Is.EqualTo(ElevatedHelperVolumeChangeSource.UnknownExecutableMessage));
    }

    [Test]
    public async Task RejectedArm_DoesNotHang()
    {
        await using var pipes = new HelperPipes();
        await using var source = CreateSource(new RecordingLauncher(pipes).Launch);

        var arm = source.ArmAndCatchUpAsync(["/repos"], CancellationToken.None);
        await pipes.SendFrameAsync(new HelloFrame(LinuxWatchHelperProtocol.Version));
        await pipes.ReadRequestAsync();
        await pipes.SendFrameAsync(new ErrorFrame("Rejected request: no."));
        var result = await arm.WaitAsync(TestTimeouts.ShortHangGuard);

        Assert.That(result.Errors["/repos"], Is.EqualTo(ElevatedHelperVolumeChangeSource.RejectedRequestMessage));
    }

    [Test]
    public async Task Watch_RequestsTheWatch_AndReplaysPumpStartAndBatchesInOrder()
    {
        await using var pipes = new HelperPipes();
        await using var source = CreateSource(new RecordingLauncher(pipes).Launch);
        var arm = source.ArmAndCatchUpAsync(["/repos"], CancellationToken.None);
        await AnswerArmAsync(pipes);
        await arm.WaitAsync(TestTimeouts.ShortHangGuard);

        using var cancellation = new CancellationTokenSource(TestTimeouts.ShortHangGuard);
        await using var enumerator = source.WatchAsync(cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        var first = enumerator.MoveNextAsync().AsTask();
        var request = await pipes.ReadRequestAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(request, Is.TypeOf<WatchRequestFrame>());
            Assert.That(source.PumpStarted.IsCompleted, Is.False);
        }

        await pipes.SendFrameAsync(new PumpStartedFrame());
        var batches = new[]
        {
            new VolumeChangeBatch("/", [new VolumeChangeEntry("/repos/a/.git/index", VolumeEntryKind.Modified)]),
            new VolumeChangeBatch("/", [new VolumeChangeEntry("/repos/b", VolumeEntryKind.MovedIn)])
        };
        foreach (var batch in batches)
        {
            await pipes.SendFrameAsync(new BatchFrame(batch));
        }

        Assert.That(await first, Is.True);
        var received = new List<VolumeChangeBatch> { enumerator.Current };
        Assert.That(await enumerator.MoveNextAsync(), Is.True);
        received.Add(enumerator.Current);

        using (Assert.EnterMultipleScope())
        {
            await source.PumpStarted.WaitAsync(TestTimeouts.ShortHangGuard);
            Assert.That(received.Select(batch => batch.Entries), Is.EqualTo(batches.Select(batch => batch.Entries)));
        }
    }

    [Test]
    public async Task DriveFailure_IsForwarded()
    {
        await using var pipes = new HelperPipes();
        await using var source = CreateSource(new RecordingLauncher(pipes).Launch);
        var failures = new List<(string, string)>();
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.DriveFailed += (volume, message) =>
        {
            failures.Add((volume, message));
            failed.TrySetResult();
        };
        var arm = source.ArmAndCatchUpAsync(["/repos"], CancellationToken.None);
        await AnswerArmAsync(pipes);
        await arm.WaitAsync(TestTimeouts.ShortHangGuard);

        await pipes.SendFrameAsync(new DriveFailedFrame("/repos", "watch failed"));
        await failed.Task.WaitAsync(TestTimeouts.ShortHangGuard);

        Assert.That(failures, Is.EqualTo([("/repos", "watch failed")]));
    }

    [Test]
    public async Task HelperDeath_RaisesSourceDiedOnce_AndReplaysItToLateSubscribers()
    {
        await using var pipes = new HelperPipes();
        await using var source = CreateSource(new RecordingLauncher(pipes).Launch);
        var deaths = new List<string>();
        source.SourceDied += deaths.Add;
        var arm = source.ArmAndCatchUpAsync(["/repos"], CancellationToken.None);
        await AnswerArmAsync(pipes, readiness: VolumeWatchStatus.CaughtUp("/repos", VolumeBlockOrigin.Unknown));
        await arm.WaitAsync(TestTimeouts.ShortHangGuard);

        await pipes.SendFrameAsync(new DiedFrame("fanotify event queue overflowed."));
        await pipes.ExitAsync(LinuxWatchHelperHost.ExitCodeSourceEnded);
        await AsyncCondition.WaitUntilAsync(() => deaths.Count > 0, TestTimeouts.ShortHangGuard);
        var late = new List<string>();
        source.SourceDied += late.Add;
        var batches = new List<VolumeChangeBatch>();
        await foreach (var batch in source.WatchAsync(CancellationToken.None))
        {
            batches.Add(batch);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(deaths, Is.EqualTo(["fanotify event queue overflowed."]));
            Assert.That(late, Is.EqualTo(["fanotify event queue overflowed."]));
            Assert.That(batches, Is.Empty, "A dead helper's watch ends instead of hanging.");
            Assert.That(source.Readiness.ExcludedVolumes.Single().ExclusionReason, Is.EqualTo("fanotify event queue overflowed."));
        }
    }

    [Test]
    public async Task OutputEndAfterHello_IsAnUnexpectedEnd()
    {
        await using var pipes = new HelperPipes();
        await using var source = CreateSource(new RecordingLauncher(pipes).Launch);
        var deaths = new List<string>();
        source.SourceDied += deaths.Add;
        var arm = source.ArmAndCatchUpAsync(["/repos"], CancellationToken.None);
        await AnswerArmAsync(pipes);
        await arm.WaitAsync(TestTimeouts.ShortHangGuard);

        await pipes.ExitAsync(0);
        await AsyncCondition.WaitUntilAsync(() => deaths.Count > 0, TestTimeouts.ShortHangGuard);

        Assert.That(deaths, Is.EqualTo(["The elevated Live helper ended unexpectedly (exit code 0)."]));
    }

    [Test]
    public async Task Dispose_ClosesStdinAndWaitsForTheHelper_WithoutReportingADeath()
    {
        await using var pipes = new HelperPipes();
        var source = CreateSource(new RecordingLauncher(pipes).Launch);
        var deaths = new List<string>();
        source.SourceDied += deaths.Add;
        var arm = source.ArmAndCatchUpAsync(["/repos"], CancellationToken.None);
        await AnswerArmAsync(pipes);
        await arm.WaitAsync(TestTimeouts.ShortHangGuard);

        var disposal = source.DisposeAsync().AsTask();
        var request = await pipes.ReadRequestAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(request, Is.Null, "Disposal must close the helper's stdin.");
            Assert.That(disposal.IsCompleted, Is.False, "Disposal waits for the helper to exit.");
        }

        await pipes.ExitAsync(0);
        await disposal.WaitAsync(TestTimeouts.ShortHangGuard);

        Assert.That(deaths, Is.Empty);
    }

    [Test]
    public async Task Dispose_GivesUpOnAHelperThatDoesNotExit_AfterTheBoundedWait()
    {
        await using var pipes = new HelperPipes();
        var clock = new ControlledDelayClock();
        var source = CreateSource(new RecordingLauncher(pipes).Launch, clock);
        var arm = source.ArmAndCatchUpAsync(["/repos"], CancellationToken.None);
        await AnswerArmAsync(pipes);
        await arm.WaitAsync(TestTimeouts.ShortHangGuard);

        var disposal = source.DisposeAsync().AsTask();
        Assert.That(await pipes.ReadRequestAsync(), Is.Null);
        await AsyncCondition.WaitUntilAsync(() => clock.Timer is not null, TestTimeouts.ShortHangGuard);
        Assert.That(disposal.IsCompleted, Is.False);
        clock.Timer!.Fire();

        await disposal.WaitAsync(TestTimeouts.ShortHangGuard);
    }
}

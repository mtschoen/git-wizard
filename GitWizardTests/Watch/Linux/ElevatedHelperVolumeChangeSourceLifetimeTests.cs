using System.Diagnostics;
using GitWizard;
using GitWizard.Watch;
using GitWizard.Watch.Linux;
using GitWizardTests.TestSupport;

namespace GitWizardTests.Watch.Linux;

// The pkexec dialog window before the helper's hello, request writes, and local readiness, split
// from ElevatedHelperVolumeChangeSourceTests to keep each fixture under 500 lines.
[TestFixture]
public class ElevatedHelperVolumeChangeSourceLifetimeTests
{
    sealed class RecordingLauncher(HelperPipes pipes)
    {
        public int Starts { get; private set; }

        public LinuxWatchHelperProcess Launch(ProcessStartInfo startInfo)
        {
            Starts++;
            return pipes.Process;
        }
    }

    static ElevatedHelperVolumeChangeSource CreateSource(HelperPipes pipes, TimeProvider? clock = null) =>
        new(LinuxWatchHelperLaunch.CreatePkexecStartInfo("/opt/git-wizard/GitWizardUI"), new RecordingLauncher(pipes).Launch,
            clock ?? TimeProvider.System, TestTimeouts.ShortHangGuard, _ => false);

    [Test]
    public async Task CancelDuringRootValidation_DoesNotLaunchAfterTheProbeReturns()
    {
        await using var pipes = new HelperPipes();
        var launcher = new RecordingLauncher(pipes);
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        var probing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool Probe(string root)
        {
            probing.SetResult();
            return resume.Task.WaitAsync(TestTimeouts.HangGuard).GetAwaiter().GetResult();
        }

        var arm = Task.Run(async () =>
        {
            await using var source = new ElevatedHelperVolumeChangeSource(LinuxWatchHelperLaunch.CreatePkexecStartInfo("/opt/git-wizard/GitWizardUI"),
                launcher.Launch, TimeProvider.System, TestTimeouts.ShortHangGuard, Probe);
            await source.ArmAndCatchUpAsync(["/repos"], token);
        }, token);
        try
        {
            await probing.Task.WaitAsync(TestTimeouts.ShortHangGuard);
            await cancellation.CancelAsync();
        }
        finally
        {
            resume.SetResult(false);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(async () => await arm.WaitAsync(TestTimeouts.ShortHangGuard), Throws.InstanceOf<OperationCanceledException>());
            Assert.That(launcher.Starts, Is.Zero);
        }
    }

    [Test]
    public async Task CancelWhileTheDialogIsOpen_TerminatesPkexec_WithoutAWrittenRequestOrADeath()
    {
        await using var pipes = new HelperPipes();
        var source = CreateSource(pipes);
        var deaths = new List<string>();
        source.SourceDied += deaths.Add;
        using var cancellation = new CancellationTokenSource();

        var arm = source.ArmAndCatchUpAsync(["/repos"], cancellation.Token);
        await cancellation.CancelAsync();

        Assert.That(async () => await arm.WaitAsync(TestTimeouts.ShortHangGuard), Throws.InstanceOf<OperationCanceledException>());
        await source.DisposeAsync().AsTask().WaitAsync(TestTimeouts.ShortHangGuard);
        var request = await pipes.ReadRequestAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pipes.TerminateCount, Is.GreaterThanOrEqualTo(1));
            Assert.That(request, Is.Null, "No request may be written before the helper's hello.");
            Assert.That(deaths, Is.Empty, "A termination this source caused is not a failure.");
        }
    }

    [Test]
    public async Task DisposeWhileTheDialogIsOpen_TerminatesPkexec_AndTheArmEndsWithoutAnAuthenticationError()
    {
        await using var pipes = new HelperPipes();
        var source = CreateSource(pipes);
        var deaths = new List<string>();
        source.SourceDied += deaths.Add;

        var arm = source.ArmAndCatchUpAsync(["/repos"], CancellationToken.None);
        await source.DisposeAsync().AsTask().WaitAsync(TestTimeouts.ShortHangGuard);
        var result = await arm.WaitAsync(TestTimeouts.ShortHangGuard);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pipes.TerminateCount, Is.EqualTo(1));
            Assert.That(result.Errors["/repos"], Is.EqualTo(ElevatedHelperVolumeChangeSource.StoppedMessage));
            Assert.That(deaths, Is.Empty);
        }
    }

    [Test]
    public async Task AfterHello_CancelAndDispose_NeverSignalTheHelper()
    {
        await using var pipes = new HelperPipes();
        var source = CreateSource(pipes);
        using var cancellation = new CancellationTokenSource();
        var arm = source.ArmAndCatchUpAsync(["/repos"], cancellation.Token);
        await pipes.SendFrameAsync(new HelloFrame(LinuxWatchHelperProtocol.Version));
        Assert.That(await pipes.ReadRequestAsync(), Is.TypeOf<ArmRequestFrame>());

        await cancellation.CancelAsync();
        Assert.That(async () => await arm.WaitAsync(TestTimeouts.ShortHangGuard), Throws.InstanceOf<OperationCanceledException>());
        var disposal = source.DisposeAsync().AsTask();
        Assert.That(await pipes.ReadRequestAsync(), Is.Null, "Disposal closes the root helper's stdin.");
        await pipes.ExitAsync(LinuxWatchHelperHost.ExitCodeEndOfInput);
        await disposal.WaitAsync(TestTimeouts.ShortHangGuard);

        Assert.That(pipes.TerminateCount, Is.Zero);
    }

    [Test]
    public async Task AfterATerminatedDialog_ANewSourceArmsNormally()
    {
        await using (var first = new HelperPipes())
        {
            var source = CreateSource(first);
            var arm = source.ArmAndCatchUpAsync(["/repos"], CancellationToken.None);
            await source.DisposeAsync();
            await arm.WaitAsync(TestTimeouts.ShortHangGuard);
            Assert.That(first.TerminateCount, Is.EqualTo(1));
        }

        await using var second = new HelperPipes();
        await using var next = CreateSource(second);
        var nextArm = next.ArmAndCatchUpAsync(["/repos"], CancellationToken.None);
        await second.SendFrameAsync(new HelloFrame(LinuxWatchHelperProtocol.Version));
        await second.ReadRequestAsync();
        await second.SendFrameAsync(new ArmResultFrame(new Dictionary<string, string>()));

        Assert.That((await nextArm.WaitAsync(TestTimeouts.ShortHangGuard)).Errors, Is.Empty);
    }

    [Test]
    public async Task AnArmWriteThatNeverCompletes_BlocksNeitherCancellationNorDisposal()
    {
        await using var pipes = new HelperPipes(holdClientWrites: true);
        var clock = new ControlledDelayClock();
        var source = CreateSource(pipes, clock);
        using var cancellation = new CancellationTokenSource();
        var roots = Enumerable.Range(0, 1200).Select(index => $"/srv/data/repositories/repository-{index:D5}").ToArray();
        var arm = source.ArmAndCatchUpAsync(roots, cancellation.Token);
        await pipes.SendFrameAsync(new HelloFrame(LinuxWatchHelperProtocol.Version));
        await pipes.ClientWriteStarted.WaitAsync(TestTimeouts.ShortHangGuard);

        await cancellation.CancelAsync();
        Assert.That(async () => await arm.WaitAsync(TestTimeouts.ShortHangGuard), Throws.InstanceOf<OperationCanceledException>());
        var disposal = source.DisposeAsync().AsTask();
        await pipes.ExitAsync(LinuxWatchHelperHost.ExitCodeEndOfInput);
        ControlledDelayClock.ScheduledTimer? fired = null;
        while (!disposal.IsCompleted)
        {
            await AsyncCondition.WaitUntilAsync(() => disposal.IsCompleted || (clock.Timer is { } timer && !ReferenceEquals(timer, fired)),
                TestTimeouts.ShortHangGuard);
            if (!disposal.IsCompleted)
            {
                fired = clock.Timer!;
                fired.Fire();
            }
        }

        await disposal.WaitAsync(TestTimeouts.ShortHangGuard);
        Assert.That(pipes.TerminateCount, Is.Zero, "After hello the helper is root and is never signalled.");
    }

    [Test]
    public async Task HelperReadiness_KeepsTheRootsExcludedLocally()
    {
        await using var pipes = new HelperPipes();
        await using var source = CreateSource(pipes);
        var arm = source.ArmAndCatchUpAsync(["relative/path", "/repos"], CancellationToken.None);
        await pipes.SendFrameAsync(new HelloFrame(LinuxWatchHelperProtocol.Version));
        await pipes.ReadRequestAsync();
        await pipes.SendFrameAsync(new ReadinessFrame([VolumeWatchStatus.CaughtUp("/repos", VolumeBlockOrigin.Unknown)]));
        await pipes.SendFrameAsync(new ArmResultFrame(new Dictionary<string, string>()));
        await arm.WaitAsync(TestTimeouts.ShortHangGuard);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(source.Readiness.CaughtUpVolumes, Is.EqualTo(["/repos"]));
            Assert.That(source.Readiness.ExcludedVolumes, Is.EqualTo(
            [
                VolumeWatchStatus.Excluded("relative/path", VolumeBlockOrigin.Unknown, ElevatedHelperVolumeChangeSource.RelativeRootMessage)
            ]));
        }
    }

    [TestCase("/usr/share/dotnet/dotnet", ElevatedHelperVolumeChangeSource.DotnetHostMessage)]
    [TestCase(null, ElevatedHelperVolumeChangeSource.UnknownExecutableMessage)]
    public async Task UnusableExecutablePath_RaisesNoPrompt(string? executablePath, string expected)
    {
        await using var source = ElevatedHelperVolumeChangeSource.CreatePkexec(executablePath);

        var result = await source.ArmAndCatchUpAsync(["/"], CancellationToken.None);

        Assert.That(result.Errors["/"], Is.EqualTo(expected));
    }

    [TestCase("/usr/share/dotnet/dotnet", true)]
    [TestCase("/usr/bin/dotnet", true)]
    [TestCase("/opt/git-wizard/GitWizardUI", false)]
    public void IsDotnetHost_RecognizesTheHostByFileName(string path, bool expected) =>
        Assert.That(LinuxWatchHelperLaunch.IsDotnetHost(path), Is.EqualTo(expected));

    [Test]
    public async Task AlreadyExitedHelper_PreservesItsPipesAndExitCode()
    {
        var windows = OperatingSystem.IsWindows();
        var startInfo = LinuxWatchHelperLaunch.CreateRedirectedStartInfo(
            windows ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : "/bin/sh");
        startInfo.Arguments = windows
            ? "/d /c \"(echo output)&(echo diagnostic) 1>&2&exit /b 127\""
            : "-c \"echo output; echo diagnostic >&2; exit 127\"";
        using var child = Process.Start(startInfo)!;
        await child.WaitForExitAsync().WaitAsync(TestTimeouts.ShortHangGuard);

        var helper = LinuxWatchHelperLaunch.Attach(child);
        await using var input = helper.Input;
        using var output = new StreamReader(helper.Output);
        using var error = new StreamReader(helper.Error);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await helper.Exited, Is.EqualTo(127));
            Assert.That(await output.ReadLineAsync(), Is.EqualTo("output"));
            Assert.That(await error.ReadLineAsync(), Is.EqualTo("diagnostic"));
        }
    }

    [Test]
    [Platform("Linux")]
    public async Task RealTerminate_EndsAProcessStillRunningAsTheCaller()
    {
        var startInfo = LinuxWatchHelperLaunch.CreateRedirectedStartInfo("sleep");
        startInfo.ArgumentList.Add("30");
        var process = LinuxWatchHelperLaunch.Start(startInfo);
        try
        {
            process.Terminate();
            var exitCode = await process.Exited.WaitAsync(TestTimeouts.ShortHangGuard);
            // Terminating an exited process is a no-op and must not throw.
            process.Terminate();
            Assert.That(exitCode, Is.EqualTo(HelperPipes.TerminatedExitCode));
        }
        finally
        {
            await process.Input.DisposeAsync();
        }
    }

    [Test]
    [NonParallelizable]
    public async Task DisposeBeforeHello_WithAPkexecThatDoesNotEnd_ReturnsAndWarnsAboutTheOpenDialog()
    {
        await using var pipes = new HelperPipes { ExitOnTerminate = false, ExitWhenClientInputCloses = false };
        var clock = new ControlledDelayClock();
        var source = CreateSource(pipes, clock);
        var warnings = new List<string>();
        var previousSink = GitWizardLog.TypedLogMethod;
        GitWizardLog.TypedLogMethod = (message, type) =>
        {
            if (type == GitWizardLog.LogType.Warning && message?.Contains("has not exited yet", StringComparison.Ordinal) == true)
            {
                warnings.Add(message);
            }
        };
        try
        {
            _ = source.ArmAndCatchUpAsync(["/repos"], CancellationToken.None);
            var disposal = source.DisposeAsync().AsTask();
            await AsyncCondition.WaitUntilAsync(() => clock.Timer is not null, TestTimeouts.ShortHangGuard);
            Assert.That(disposal.IsCompleted, Is.False);
            clock.Timer!.Fire();
            await disposal.WaitAsync(TestTimeouts.ShortHangGuard);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(warnings, Has.Count.EqualTo(1));
                Assert.That(warnings[0], Does.Contain("a pkexec authentication dialog that could not be closed stays until it is dismissed"));
            }
        }
        finally
        {
            GitWizardLog.TypedLogMethod = previousSink;
        }
    }

    [TestCase("pkexec")]
    [TestCase("/usr/bin/pkexec")]
    public void ForbiddenPkexec_IsRefusedByFileName(string fileName) =>
        Assert.That(() => LinuxWatchHelperLaunch.Start(LinuxWatchHelperLaunch.CreateRedirectedStartInfo(fileName)),
            Throws.InvalidOperationException.With.Message.Contains("forbidden"));
}

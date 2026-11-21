using GitWizard;
using GitWizard.Watch;
using GitWizard.Watch.Linux;
using GitWizardTests.TestSupport;

namespace GitWizardTests.Watch.Linux;

// Replay, lifecycle and launch-failure behavior of the helper client over the in-memory pipes; none
// of it touches a platform API, so it holds on every OS.
[TestFixture]
public class ElevatedHelperVolumeChangeSourceReplayTests
{
    static ElevatedHelperVolumeChangeSource CreateSource(LinuxWatchHelperLauncher launcher, TimeSpan? exitWait = null,
        Func<string, bool>? isConclusivelyMissing = null) =>
        new(LinuxWatchHelperLaunch.CreatePkexecStartInfo("/opt/git-wizard/GitWizardUI"), launcher, TimeProvider.System,
            exitWait ?? TestTimeouts.ShortHangGuard, isConclusivelyMissing ?? (_ => false));

    static LinuxWatchHelperLauncher Launcher(HelperPipes pipes) => _ => pipes.Process;

    static async Task ArmAfterHelloAsync(HelperPipes pipes, ElevatedHelperVolumeChangeSource source)
    {
        var arm = source.ArmAndCatchUpAsync(["/repos"], CancellationToken.None);
        await pipes.SendFrameAsync(new HelloFrame(LinuxWatchHelperProtocol.Version));
        await pipes.ReadRequestAsync();
        await pipes.SendFrameAsync(new ArmResultFrame(new Dictionary<string, string>()));
        await arm.WaitAsync(TestTimeouts.ShortHangGuard);
    }

    [Test]
    public async Task MalformedLineAndUnexpectedFrame_AreIgnored_AndTheArmStillCompletes()
    {
        await using var pipes = new HelperPipes();
        await using var source = CreateSource(Launcher(pipes));
        var deaths = new List<string>();
        source.SourceDied += deaths.Add;

        var arm = source.ArmAndCatchUpAsync(["/repos"], CancellationToken.None);
        await pipes.SendLineAsync("this is not a frame");
        await pipes.SendFrameAsync(new ArmRequestFrame(["/not/from/a/helper"]));
        await pipes.SendFrameAsync(new HelloFrame(LinuxWatchHelperProtocol.Version));
        var request = (ArmRequestFrame)(await pipes.ReadRequestAsync())!;
        await pipes.SendFrameAsync(new ArmResultFrame(new Dictionary<string, string>()));
        var result = await arm.WaitAsync(TestTimeouts.ShortHangGuard);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(request.Roots, Is.EqualTo(["/repos"]));
            Assert.That(result.Errors, Is.Empty);
            Assert.That(deaths, Is.Empty);
        }
    }

    [Test]
    public async Task HelloWithAnotherProtocolVersion_EndsTheHelperWithAReason()
    {
        await using var pipes = new HelperPipes();
        await using var source = CreateSource(Launcher(pipes));
        var deaths = new List<string>();
        source.SourceDied += deaths.Add;
        var expected = $"The elevated Live helper speaks protocol {LinuxWatchHelperProtocol.Version + 1}, not {LinuxWatchHelperProtocol.Version}.";

        var arm = source.ArmAndCatchUpAsync(["/repos"], CancellationToken.None);
        await pipes.SendFrameAsync(new HelloFrame(LinuxWatchHelperProtocol.Version + 1));
        var result = await arm.WaitAsync(TestTimeouts.ShortHangGuard);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Errors, Is.EqualTo(new Dictionary<string, string> { ["/repos"] = expected }));
            Assert.That(deaths, Is.EqualTo([expected]));
        }
    }

    [Test]
    public async Task HelperEndingWithoutAnExitCode_DescribesTheEndWithoutOne()
    {
        await using var pipes = new HelperPipes();
        await using var source = CreateSource(Launcher(pipes), TimeSpan.FromMilliseconds(1));
        var deaths = new List<string>();
        source.SourceDied += deaths.Add;

        var arm = source.ArmAndCatchUpAsync(["/repos"], CancellationToken.None);
        await pipes.EndOutputAsync();
        await arm.WaitAsync(TestTimeouts.ShortHangGuard);

        Assert.That(deaths, Is.EqualTo(["The elevated Live helper ended before it started."]));
    }

    [Test]
    [NonParallelizable]
    public async Task HelperStandardError_IsLoggedAsWarnings_AndBlankLinesAreSkipped()
    {
        var previousSink = GitWizardLog.TypedLogMethod;
        var warnings = new List<string>();
        GitWizardLog.TypedLogMethod = (message, type) =>
        {
            if (type == GitWizardLog.LogType.Warning && message?.Contains("Elevated Live helper:", StringComparison.Ordinal) == true)
            {
                lock (warnings)
                {
                    warnings.Add(message);
                }
            }
        };
        try
        {
            await using var pipes = new HelperPipes();
            await using var source = CreateSource(Launcher(pipes));
            var arm = source.ArmAndCatchUpAsync(["/repos"], CancellationToken.None);
            await pipes.SendErrorLineAsync("   ");
            await pipes.SendErrorLineAsync("helper complaint");
            await AsyncCondition.WaitUntilAsync(() => { lock (warnings) { return warnings.Count == 1; } });
            await pipes.SendFrameAsync(new HelloFrame(LinuxWatchHelperProtocol.Version));
            await pipes.ReadRequestAsync();
            await pipes.SendFrameAsync(new ArmResultFrame(new Dictionary<string, string>()));
            await arm.WaitAsync(TestTimeouts.ShortHangGuard);

            lock (warnings)
            {
                Assert.That(warnings.Single(), Does.EndWith("Elevated Live helper: helper complaint"));
            }
        }
        finally
        {
            GitWizardLog.TypedLogMethod = previousSink;
        }
    }

    [Test]
    public async Task ArmingTwice_Throws_AndTheStartInfoIsExposed()
    {
        await using var pipes = new HelperPipes();
        await using var source = CreateSource(Launcher(pipes));
        await ArmAfterHelloAsync(pipes, source);

        var secondArm = source.ArmAndCatchUpAsync(["/repos"], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(async () => await secondArm, Throws.InvalidOperationException);
            Assert.That(source.StartInfo!.FileName, Is.EqualTo("pkexec"));
        }
    }

    [Test]
    public async Task WatchingBeforeArming_Throws()
    {
        await using var pipes = new HelperPipes();
        await using var source = CreateSource(Launcher(pipes));

        var firstBatch = source.WatchAsync(CancellationToken.None).GetAsyncEnumerator().MoveNextAsync().AsTask();

        Assert.That(async () => await firstBatch, Throws.InvalidOperationException);
    }

    [Test]
    public async Task SourceDiedHandler_RemovedBeforeTheDeath_IsNotInvoked_AndDisposalIsIdempotent()
    {
        await using var pipes = new HelperPipes();
        var source = CreateSource(Launcher(pipes));
        var removed = new List<string>();
        var kept = new List<string>();
        source.SourceDied += removed.Add;
        source.SourceDied += kept.Add;
        source.SourceDied -= removed.Add;
        Action<JournalWarning> ignoreWarning = _ => { };
        var ignoreCapacity = () => { };
        source.JournalWarningDetected += ignoreWarning;
        source.JournalWarningDetected -= ignoreWarning;
        source.CapacityWarningsChanged += ignoreCapacity;
        source.CapacityWarningsChanged -= ignoreCapacity;

        var arm = source.ArmAndCatchUpAsync(["/repos"], CancellationToken.None);
        await pipes.ExitAsync(1);
        await arm.WaitAsync(TestTimeouts.ShortHangGuard);
        await source.DisposeAsync();
        await source.DisposeAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(removed, Is.Empty);
            Assert.That(kept, Has.Count.EqualTo(1));
        }
    }

    [Test]
    public async Task LauncherThatThrowsInvalidOperation_ReportsTheLaunchAsFailed()
    {
        await using var source = CreateSource(_ => throw new InvalidOperationException("The helper process did not start."));

        var result = await source.ArmAndCatchUpAsync(["/repos"], CancellationToken.None);

        Assert.That(result.Errors, Is.EqualTo(new Dictionary<string, string>
        {
            ["/repos"] = ElevatedHelperVolumeChangeSource.LaunchFailedMessage
        }));
    }

    [Test]
    public async Task RootThatIsConclusivelyMissing_IsExcludedWithoutLaunching()
    {
        var launches = 0;
        await using var source = CreateSource(_ =>
        {
            launches++;
            throw new AssertionException("A missing root must not launch pkexec.");
        }, isConclusivelyMissing: root => root == "/gone");

        var result = await source.ArmAndCatchUpAsync(["/gone"], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(launches, Is.Zero);
            Assert.That(result.Errors, Is.EqualTo(new Dictionary<string, string> { ["/gone"] = "Directory does not exist: /gone" }));
        }
    }
}

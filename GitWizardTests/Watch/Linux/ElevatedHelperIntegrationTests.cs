using System.Runtime.Versioning;
using GitWizard;
using GitWizard.Watch;
using GitWizard.Watch.Linux;
using GitWizardTests.TestSupport;

namespace GitWizardTests.Watch.Linux;

// Runs the built GitWizardUI helper as root through `sudo -n` in place of pkexec, so the real
// process boundary is exercised without raising a polkit dialog. Explicit: it needs passwordless
// sudo, which neither CI runner nor an ordinary run may assume.
[TestFixture]
[SupportedOSPlatform("linux")]
public class ElevatedHelperIntegrationTests
{
    [Test]
    [Explicit("Starts the real helper as root through passwordless sudo.")]
    [Category("RequiresSudo")]
    [Platform("Linux")]
    public async Task RealHelperUnderSudo_ArmsWatchesAndExitsOnEndOfInput()
    {
        GitWizardLog.SilentMode = true;
        var executable = Path.Combine(TestContext.CurrentContext.TestDirectory, "GitWizardUI");
        var startInfo = LinuxWatchHelperLaunch.CreateRedirectedStartInfo("sudo");
        startInfo.ArgumentList.Add("-n");
        startInfo.ArgumentList.Add(executable);
        startInfo.ArgumentList.Add(LinuxWatchHelperHost.Flag);
        LinuxWatchHelperProcess? started = null;
        using var root = new TempDirectory("gw-helper-sudo-");
        var deaths = new List<string>();
        using var cancellation = new CancellationTokenSource(TestTimeouts.HangGuard);
        var source = new ElevatedHelperVolumeChangeSource(startInfo,
            info => started = LinuxWatchHelperLaunch.Start(info), TimeProvider.System, TestTimeouts.ShortHangGuard);
        try
        {
            source.SourceDied += deaths.Add;
            var result = await source.ArmAndCatchUpAsync([root.Path], cancellation.Token);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.Errors, Is.Empty, string.Join("; ", result.Errors.Values));
                Assert.That(source.Readiness.CaughtUpVolumes, Is.EqualTo([root.Path]));
            }

            await using var enumerator = source.WatchAsync(cancellation.Token).GetAsyncEnumerator(cancellation.Token);
            var next = enumerator.MoveNextAsync().AsTask();
            await source.PumpStarted.WaitAsync(cancellation.Token);
            var file = Path.Combine(root.Path, "created.txt");
            await File.WriteAllTextAsync(file, "hello", cancellation.Token);
            var observed = new List<VolumeChangeEntry>();
            while (!observed.Contains(new VolumeChangeEntry(file, VolumeEntryKind.Created)))
            {
                Assert.That(await next, Is.True, "The helper's watch ended before the creation arrived.");
                observed.AddRange(enumerator.Current.Entries);
                next = enumerator.MoveNextAsync().AsTask();
            }

            await source.DisposeAsync();
            await ObserveAsync(next);
        }
        finally
        {
            await source.DisposeAsync();
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(started, Is.Not.Null);
            Assert.That(await started!.Exited.WaitAsync(TestTimeouts.ShortHangGuard), Is.EqualTo(LinuxWatchHelperHost.ExitCodeEndOfInput));
            Assert.That(deaths, Is.Empty);
        }
    }

    static async Task ObserveAsync(Task<bool> next)
    {
        try
        {
            await next.WaitAsync(TestTimeouts.ShortHangGuard);
        }
        catch (OperationCanceledException)
        {
            // The watch may end by cancellation or by completion after disposal.
        }
    }
}

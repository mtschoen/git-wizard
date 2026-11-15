using System.Collections.Concurrent;
using GitWizard.CLI;
using GitWizard.Watch;
using GitWizardTests.TestSupport;

namespace GitWizardTests;

// The --watch output for a drive whose index block could not store a journal change: one line,
// naming the drive and the step that rescans it, with no prompt.
[TestFixture]
public class ProgramWatchCapacityWarningTests
{
    [Test]
    public async Task CapacityWarning_PrintsOneLineNamingTheDriveAndTheRescanStep()
    {
        await using var source = new DiagnosticVolumeChangeSource();
        var service = new RepositoryWatchService(source, [@"C:\repo", @"D:\repo"], [], TimeSpan.Zero);
        var lines = new ConcurrentQueue<string>();
        using var cancellation = new CancellationTokenSource(TestTimeouts.ShortHangGuard);
        var running = Program.RunWatchLoopAsync(service, lines.Enqueue, _ => { }, cancellation.Token);
        await source.Started;

        var warning = new CapacityWarning { DriveLetter = "C", Detail = "the name region is at its clamp" };
        source.SetCapacityWarnings(warning);
        // A later notification for another reason must not print the same drive's line again.
        source.SetCapacityWarnings(warning);
        source.Publish(new VolumeChangeBatch("D", [new VolumeChangeEntry(@"D:\repo\file", VolumeEntryKind.Modified)]));
        await AsyncCondition.WaitUntilAsync(() => lines.Any(line => line.StartsWith("changed:", StringComparison.Ordinal)));
        await cancellation.CancelAsync();
        try { await running; } catch (OperationCanceledException) { }

        var capacityLines = lines.Where(line => line.Contains("Index on C:", StringComparison.Ordinal)).ToArray();
        Assert.That(capacityLines, Has.Length.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(capacityLines[0], Does.Contain("the name region is at its clamp")
                .And.Contain("Restart git-wizard --watch to rescan C:"));
            Assert.That(lines, Has.None.Contains("Index on D:"));
        }
    }
}

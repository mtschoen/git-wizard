using GitWizard.Watch;
using GitWizardTests.TestSupport;

namespace GitWizardTests.Watch;

// Covers how RepositoryWatchService carries a source's capacity warnings to its consumers.
[TestFixture]
public class RepositoryWatchServiceCapacityTests
{
    [Test]
    public async Task CapacityWarnings_FollowTheSourceAndNotifyWithoutRepositoryEvents()
    {
        await using var source = new DiagnosticVolumeChangeSource();
        // Raised before the run subscribed, as an arm on a flagged block does.
        source.SetCapacityWarnings(Warning("X"));
        var service = new RepositoryWatchService(source, [@"X:\repo", @"Y:\repo"], [], TimeSpan.Zero);
        int[] notifications = [0];
        service.ScanWarningsChanged += () => Interlocked.Increment(ref notifications[0]);
        using var cancellation = new CancellationTokenSource(TestTimeouts.ShortHangGuard);
        var reader = service.RunAsync(cancellation.Token).GetAsyncEnumerator();
        var pending = reader.MoveNextAsync().AsTask();
        try
        {
            await source.Started;
            Assert.That(Drives(service), Is.EqualTo(["X"]));

            var beforeFault = Volatile.Read(ref notifications[0]);
            source.SetCapacityWarnings(Warning("X"), Warning("Y"));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(Drives(service), Is.EqualTo(["X", "Y"]));
                Assert.That(Volatile.Read(ref notifications[0]), Is.EqualTo(beforeFault + 1));
            }

            // A rescan of X clears its warning; Y keeps its own.
            source.SetCapacityWarnings(Warning("Y"));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(Drives(service), Is.EqualTo(["Y"]));
                Assert.That(service.ScanWarnings, Is.Empty, "A capacity warning is not a journal hint.");
                Assert.That(service.ScanErrors, Is.Empty, "A capacity warning is not a drive failure.");
            }
        }
        finally
        {
            source.Complete();
            try
            {
                await pending;
            }
            finally
            {
                await reader.DisposeAsync();
            }
        }
        Assert.That(source.DiagnosticSubscribers, Is.Zero);
    }

    static CapacityWarning Warning(string drive) => new() { DriveLetter = drive, Detail = "the name region is at its clamp" };

    static string[] Drives(RepositoryWatchService service) =>
        [.. service.CapacityWarnings.Select(warning => warning.DriveLetter).Order(StringComparer.Ordinal)];
}

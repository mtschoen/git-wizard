using GitWizard.Watch;
using GitWizardTests.TestSupport;

namespace GitWizardTests.UI;

public class JournalReviewRegressionTests
{
    [Test]
    public async Task EnlargeWithoutLiveSessionFails()
    {
        await using var viewModel = TestViewModels.Create();
        var loss = FakeJournalSettingsSource.TrimmedLoss('C', 32L * 1024 * 1024, 512L * 1024 * 1024);
        Assert.ThrowsAsync<InvalidOperationException>(() => viewModel.EnlargeJournalAsync(
            new JournalWarning { DriveLetter = "C", VolumeSerial = 123, Loss = loss }));
    }

    [Test]
    public async Task EmptyArmPublishesWarningSnapshot()
    {
        await using var source = new DiagnosticVolumeChangeSource();
        source.Complete();
        var service = new RepositoryWatchService(source, [@"C:\repository"], []);
        var notifications = 0;
        service.ScanWarningsChanged += () => notifications++;
        await foreach (var change in service.RunAsync(CancellationToken.None))
            Assert.Fail($"Unexpected change: {change}");
        Assert.That(notifications, Is.EqualTo(1), "An empty re-arm must clear previous warnings.");
    }
}

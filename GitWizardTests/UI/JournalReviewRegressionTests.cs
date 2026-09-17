using GitWizard.Watch;
using GitWizardTests.TestSupport;
using GitWizardUI.ViewModels;
using GitWizardUI.ViewModels.Services;

namespace GitWizardTests.UI;

public class JournalReviewRegressionTests
{
    [Test]
    public async Task EnlargeWithoutLiveSessionFails()
    {
        await using var viewModel = new MainViewModel(
            new StubUiDispatcher(), new StubUserDialogs(), new StubClipboardService());
        Assert.ThrowsAsync<InvalidOperationException>(() => viewModel.EnlargeJournalAsync(
            new JournalWarning("C", 123, 32 * 1024 * 1024, 512 * 1024 * 1024, 8 * 1024 * 1024)));
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

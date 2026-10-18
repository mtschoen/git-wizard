using GitWizard.Watch;
using GitWizardUI.Services;
using GitWizardUI.ViewModels;

namespace GitWizardTests.TestSupport;

// Inspect consumer subscriptions directly because upstream detachment and UI guards can hide leaks.
internal static class WatchSubscriptionProbe
{
    public static RepositoryWatchService Service(LiveWatchController controller) =>
        controller.Service
        ?? throw new InvalidOperationException("The controller has not created its service.");

    public static LiveWatchController Controller(MainViewModel viewModel) =>
        viewModel._liveController
        ?? throw new InvalidOperationException("The view model has not created its controller.");

    public static int ScanErrorsSubscribers(RepositoryWatchService publisher) =>
        publisher.ScanErrorsSubscriberCount;

    public static int ScanErrorsSubscribers(LiveWatchController publisher) =>
        publisher.ScanErrorsSubscriberCount;

    public static int ReadySubscribers(RepositoryWatchService publisher) =>
        publisher.ReadySubscriberCount;
}

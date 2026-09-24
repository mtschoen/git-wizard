using System.Reflection;
using GitWizard.Watch;
using GitWizardUI.Services;
using GitWizardUI.ViewModels;

namespace GitWizardTests.TestSupport;

// Inspect consumer subscriptions directly because upstream detachment and UI guards can hide leaks.
// Reflection stays within git-wizard types and avoids adding production seams solely for tests.
internal static class WatchSubscriptionProbe
{
    public static RepositoryWatchService Service(LiveWatchController controller) =>
        ReadField<RepositoryWatchService>(controller, "_service")
        ?? throw new InvalidOperationException("The controller has not created its service.");

    public static LiveWatchController Controller(MainViewModel viewModel) =>
        ReadField<LiveWatchController>(viewModel, "_liveController")
        ?? throw new InvalidOperationException("The view model has not created its controller.");

    public static int ScanErrorsSubscribers(object publisher) =>
        ReadField<Action>(publisher, "ScanErrorsChanged")?.GetInvocationList().Length ?? 0;

    public static int ReadySubscribers(object publisher) =>
        ReadField<Action<WatchReadiness>>(publisher, "Ready")?.GetInvocationList().Length ?? 0;

    static TValue? ReadField<TValue>(object instance, string fieldName) where TValue : class
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing {instance.GetType().Name}.{fieldName} probe field.");
        return (TValue?)field.GetValue(instance);
    }
}

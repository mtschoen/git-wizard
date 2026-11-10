using GitWizard.Watch;

namespace GitWizardTests.TestSupport;

/// <summary>
/// Shared drain helper for <see cref="RepositoryWatchService"/> fixtures: enumerates the
/// service's event stream to completion under a short hang guard and returns the events
/// in delivery order.
/// </summary>
static class WatchEventDrain
{
    public static async Task<List<RepositoryChangeEvent>> DrainAsync(RepositoryWatchService service)
    {
        var events = new List<RepositoryChangeEvent>();
        using var cancellation = new CancellationTokenSource(TestTimeouts.ShortHangGuard);
        await foreach (var changeEvent in service.RunAsync(cancellation.Token))
        {
            events.Add(changeEvent);
        }

        return events;
    }
}

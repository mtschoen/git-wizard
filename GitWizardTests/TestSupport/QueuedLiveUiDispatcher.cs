using System.Collections.Concurrent;
using GitWizardUI.ViewModels.Services;

namespace GitWizardTests.TestSupport;

internal sealed class QueuedLiveUiDispatcher : IUiDispatcher
{
    readonly ConcurrentQueue<Action> _posted = new();
    public bool IsOnUiThread => true;
    public void Post(Action action) => _posted.Enqueue(action);
    public Task InvokeAsync(Action action)
    {
        action();
        return Task.CompletedTask;
    }
    public Task InvokeAsync(Func<Task> action) => action();
    public void Drain()
    {
        while (_posted.TryDequeue(out var action))
            action();
    }
}

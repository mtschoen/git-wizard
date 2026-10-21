using System.Collections.Concurrent;
using GitWizardUI.ViewModels.Services;

namespace GitWizardTests.TestSupport;

internal sealed class QueuedUiDispatcher(bool isOnUiThread = true) : IUiDispatcher
{
    readonly ConcurrentQueue<Action> _posted = new();
    public bool IsOnUiThread => isOnUiThread;
    public int QueuedCount => _posted.Count;
    public void Post(Action action) => _posted.Enqueue(action);
    public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
    public Task InvokeAsync(Func<Task> action) => action();
    public Action TakeNext() => _posted.TryDequeue(out var action)
        ? action : throw new InvalidOperationException("No UI callback is queued.");
    public void RunNext() => TakeNext()();
    public void Drain()
    {
        while (_posted.TryDequeue(out var action))
            action();
    }
}

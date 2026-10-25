using System.Collections.Concurrent;
using GitWizardUI.ViewModels.Services;

namespace GitWizardTests.TestSupport;

/// <summary>
/// Test dispatcher backed by a real dedicated thread, so <see cref="IsOnUiThread"/> reflects
/// genuine thread identity. Work is queued FIFO and pumped on that thread.
/// </summary>
internal sealed class ThreadedUiDispatcher : IUiDispatcher, IDisposable
{
    readonly BlockingCollection<Action> _queue = new();
    readonly Thread _uiThread;

    public ThreadedUiDispatcher()
    {
        _uiThread = new Thread(Pump) { IsBackground = true, Name = "test-ui-thread" };
        _uiThread.Start();
    }

    void Pump()
    {
        foreach (var action in _queue.GetConsumingEnumerable())
        {
            // Swallow so a stray callback (e.g. the background UI-update loop firing after
            // disposal) can't tear down the pump thread mid-test; InvokeAsync surfaces its
            // own failures through the returned Task.
            try { action(); } catch { /* test pump stays alive */ }
        }
    }

    public bool IsOnUiThread => Thread.CurrentThread == _uiThread;

    public int UiThreadId => _uiThread.ManagedThreadId;

    public void Post(Action action, string callbackName = "")
    {
        if (!_queue.IsAddingCompleted)
        {
            _queue.Add(action);
        }
    }

    public Task InvokeAsync(Action action, string callbackName = "")
    {
        if (_queue.IsAddingCompleted)
        {
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource();
        _queue.Add(() =>
        {
            try { action(); completion.SetResult(); }
            catch (Exception exception) { completion.SetException(exception); }
        });
        return completion.Task;
    }

    public Task InvokeAsync(Func<Task> action, string callbackName = "")
    {
        if (_queue.IsAddingCompleted)
        {
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource();
        _queue.Add(() => _ = RunAndSignalAsync(action, completion));
        return completion.Task;
    }

    static async Task RunAndSignalAsync(Func<Task> action, TaskCompletionSource completion)
    {
        try { await action(); completion.SetResult(); }
        catch (Exception exception) { completion.SetException(exception); }
    }

    public void Dispose() => _queue.CompleteAdding();
}

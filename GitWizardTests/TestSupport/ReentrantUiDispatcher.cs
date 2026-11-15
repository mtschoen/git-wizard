using GitWizardUI.ViewModels.Services;

namespace GitWizardTests.TestSupport;

/// <summary>
/// Synchronous dispatcher that runs one continuation right after the first dispatched action that makes a
/// condition true, inside the same dispatch, so a test can act the way a user does when the outcome of the
/// previous action becomes visible. The continuation is disarmed before it runs, so it cannot re-enter itself
/// when the work it starts completes synchronously.
/// </summary>
sealed class ReentrantUiDispatcher : IUiDispatcher
{
    Func<bool>? _condition;
    Action? _continuation;
    public bool IsOnUiThread => true;

    public void RunOnceWhen(Func<bool> condition, Action continuation)
    {
        _condition = condition;
        _continuation = continuation;
    }

    public void Post(Action action, string callbackName = "") => action();

    public Task InvokeAsync(Action action, string callbackName = "")
    {
        action();
        if (_condition?.Invoke() == true)
        {
            var continuation = _continuation!;
            _condition = null;
            _continuation = null;
            continuation();
        }

        return Task.CompletedTask;
    }

    public Task InvokeAsync(Func<Task> action, string callbackName = "") => action();
}

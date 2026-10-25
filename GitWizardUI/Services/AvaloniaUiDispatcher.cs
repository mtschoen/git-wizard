using System.Runtime.CompilerServices;
using Avalonia.Threading;
using GitWizardUI.ViewModels.Services;

namespace GitWizardUI.Services;

public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    readonly DispatcherWatchdog _watchdog;

    public AvaloniaUiDispatcher(DispatcherWatchdog? watchdog = null) =>
        _watchdog = watchdog ?? DispatcherWatchdog.Default;

    public bool IsOnUiThread => Dispatcher.UIThread.CheckAccess();

    public void Post(Action action, [CallerMemberName] string callbackName = "") =>
        Dispatcher.UIThread.Post(_watchdog.Inspect(callbackName, action));

    public Task InvokeAsync(Action action, [CallerMemberName] string callbackName = "")
    {
        var inspected = _watchdog.Inspect(callbackName, action);
        var completion = new TaskCompletionSource();
        Dispatcher.UIThread.Post(() => { inspected(); completion.SetResult(); });
        return completion.Task;
    }

    public Task InvokeAsync(Func<Task> action, [CallerMemberName] string callbackName = "")
    {
        var inspected = _watchdog.Inspect(callbackName, action);
        var completion = new TaskCompletionSource();
        Dispatcher.UIThread.Post(() => _ = AwaitAndSignalAsync(inspected, completion));
        return completion.Task;
    }

    static async Task AwaitAndSignalAsync(Func<Task> action, TaskCompletionSource completion)
    {
        try
        {
            await action();
            completion.SetResult();
        }
        catch (Exception exception)
        {
            completion.SetException(exception);
        }
    }
}

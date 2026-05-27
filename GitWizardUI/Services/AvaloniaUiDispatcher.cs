using Avalonia.Threading;
using GitWizardUI.ViewModels.Services;

namespace GitWizardUI.Services;

public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public bool IsOnUiThread => Dispatcher.UIThread.CheckAccess();
    public void Post(Action action) => Dispatcher.UIThread.Post(action);
    public Task InvokeAsync(Action action)
    {
        var tcs = new TaskCompletionSource();
        Dispatcher.UIThread.Post(() => { action(); tcs.SetResult(); });
        return tcs.Task;
    }
    public Task InvokeAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource();
        Dispatcher.UIThread.Post(() => _ = AwaitAndSignalAsync(action, completion));
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

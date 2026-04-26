using System;
using Avalonia.Threading;
using GitWizardUI.ViewModels.Services;

namespace GitWizardAvalonia.Services;

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
        var tcs = new TaskCompletionSource();
        Dispatcher.UIThread.Post(async () => { await action(); tcs.SetResult(); });
        return tcs.Task;
    }
}

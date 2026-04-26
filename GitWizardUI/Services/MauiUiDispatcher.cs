using GitWizardUI.ViewModels.Services;
using Microsoft.Maui.ApplicationModel;

namespace GitWizardUI.Services;

public sealed class MauiUiDispatcher : IUiDispatcher
{
    public bool IsOnUiThread => MainThread.IsMainThread;
    public void Post(Action action) => MainThread.BeginInvokeOnMainThread(action);
    public Task InvokeAsync(Action action) => MainThread.InvokeOnMainThreadAsync(action);
    public Task InvokeAsync(Func<Task> action) => MainThread.InvokeOnMainThreadAsync(action);
}

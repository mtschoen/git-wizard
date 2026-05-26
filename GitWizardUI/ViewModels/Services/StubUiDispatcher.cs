namespace GitWizardUI.ViewModels.Services;

/// <summary>Synchronous test stub. Pretends every call site is the UI thread.</summary>
public sealed class StubUiDispatcher : IUiDispatcher
{
    public bool IsOnUiThread => true;
    public void Post(Action action) => action();
    public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
    public Task InvokeAsync(Func<Task> action) => action();
}

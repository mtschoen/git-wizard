using System.Runtime.CompilerServices;

namespace GitWizardUI.ViewModels.Services;

/// <summary>Marshals work onto the UI thread. Implementations wrap the host framework's dispatcher.</summary>
public interface IUiDispatcher
{
    bool IsOnUiThread { get; }

    /// <summary>Fire-and-forget enqueue onto the UI thread.</summary>
    void Post(Action action, [CallerMemberName] string callbackName = "");

    /// <summary>Run on the UI thread and await completion.</summary>
    Task InvokeAsync(Action action, [CallerMemberName] string callbackName = "");

    /// <summary>Run async work on the UI thread and await completion.</summary>
    Task InvokeAsync(Func<Task> action, [CallerMemberName] string callbackName = "");
}

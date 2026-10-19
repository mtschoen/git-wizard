using GitWizard;

namespace GitWizardUI.Services;

public static class TaskFaultLogging
{
    /// <summary>
    /// Awaits <paramref name="task"/> and writes any exception to <see cref="GitWizardLog"/>,
    /// naming the operation. Code-behind calls a view model's async method fire-and-forget as
    /// <c>_ = vm.FooAsync().RunAndLogFaults("...")</c> so a faulted task is logged instead of
    /// going unobserved.
    /// </summary>
    public static async Task RunAndLogFaults(this Task task, string operationName)
    {
        try
        {
            await task;
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, $"{operationName} failed.");
        }
    }
}

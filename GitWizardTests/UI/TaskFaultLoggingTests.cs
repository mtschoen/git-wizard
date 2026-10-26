using GitWizard;
using GitWizardUI.Services;

namespace GitWizardTests;

/// <summary>
/// Covers <see cref="TaskFaultLogging.RunAndLogFaults"/>: the fire-and-forget helper behind the
/// code-behind's <c>_ = vm.FooAsync().RunAndLogFaults("...")</c> calls. NonParallelizable because
/// <see cref="GitWizardLog.LogMethod"/> is global static state.
/// </summary>
[NonParallelizable]
public class TaskFaultLoggingTests
{
    Action<string?>? _originalLogMethod;
    bool _originalSilentMode;

    [SetUp]
    public void SetUp()
    {
        _originalLogMethod = GitWizardLog.LogMethod;
        _originalSilentMode = GitWizardLog.SilentMode;
        GitWizardLog.SilentMode = false;
    }

    [TearDown]
    public void TearDown()
    {
        GitWizardLog.SilentMode = _originalSilentMode;
        GitWizardLog.LogMethod = _originalLogMethod!;
    }

    [Test]
    public async Task RunAndLogFaults_FaultedTask_LogsOperationAndException()
    {
        var outputs = new List<string>();
        GitWizardLog.LogMethod = message =>
        {
            if (message is not null)
            {
                outputs.Add(message);
            }
        };

        var fault = Task.FromException(new InvalidOperationException("clipboard exploded"));
        await fault.RunAndLogFaults("Copying the working directory");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outputs.Any(output => output.Contains("Copying the working directory failed.")),
                Is.True, "The operation name must be logged as the exception's context.");
            Assert.That(outputs.Any(output => output.Contains("clipboard exploded")),
                Is.True, "The exception message must reach the log.");
        }
    }

    [Test]
    public async Task RunAndLogFaults_SuccessfulTask_LogsNothing()
    {
        var outputs = new List<string>();
        GitWizardLog.LogMethod = message =>
        {
            if (message is not null)
            {
                outputs.Add(message);
            }
        };

        await Task.CompletedTask.RunAndLogFaults("Refreshing repositories");

        Assert.That(outputs, Is.Empty);
    }

    [Test]
    public void RunAndLogFaults_FaultedTask_DoesNotRethrow()
    {
        GitWizardLog.LogMethod = _ => { };

        var fault = Task.FromException(new InvalidOperationException("boom"));

        Assert.DoesNotThrowAsync(async () => await fault.RunAndLogFaults("Toggling Live watch"));
    }
}

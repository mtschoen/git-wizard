using GitWizard;
using GitWizardUI.ViewModels;

namespace GitWizardTests;

/// <summary>
/// Covers the async command variants (<see cref="AsyncRelayCommand"/> and
/// <see cref="AsyncRelayCommand{T}"/>): they run the task, route a faulted task through the audited
/// handler instead of crashing, honor CanExecute, and raise CanExecuteChanged. The synchronous
/// RelayCommand variants are covered by RelayCommandTests.
/// </summary>
public class RelayCommandAsyncTests
{
    [SetUp]
    public void SetUp() => GitWizardLog.SilentMode = true;

    [Test]
    public void AsyncRelayCommand_Execute_RunsTheTask()
    {
        var ran = false;
        var command = new AsyncRelayCommand(() => { ran = true; return Task.CompletedTask; });

        command.Execute(null);

        Assert.That(ran, Is.True);
    }

    [Test]
    public void AsyncRelayCommand_Execute_SwallowsFaultedTask()
    {
        var ran = false;
        var command = new AsyncRelayCommand(() =>
        {
            ran = true;
            throw new InvalidOperationException("boom");
        });

        Assert.DoesNotThrow(() => command.Execute(null));
        Assert.That(ran, Is.True, "The task must have run before its failure was swallowed.");
    }

    [Test]
    public void AsyncRelayCommand_CanExecute_DefaultsTrueAndHonorsPredicate()
    {
        var always = new AsyncRelayCommand(() => Task.CompletedTask);
        var gated = new AsyncRelayCommand(() => Task.CompletedTask, () => false);

        Assert.Multiple(() =>
        {
            Assert.That(always.CanExecute(null), Is.True);
            Assert.That(gated.CanExecute(null), Is.False);
        });
    }

    [Test]
    public void AsyncRelayCommand_RaiseCanExecuteChanged_FiresEvent()
    {
        var command = new AsyncRelayCommand(() => Task.CompletedTask);
        var fired = false;
        command.CanExecuteChanged += (_, _) => fired = true;

        command.RaiseCanExecuteChanged();

        Assert.That(fired, Is.True);
    }

    [Test]
    public void AsyncRelayCommand_ThrowsOnNullExecute()
        => Assert.Throws<ArgumentNullException>(() => _ = new AsyncRelayCommand(null!));

    [Test]
    public void AsyncRelayCommandT_Execute_RunsWithMatchingParameterAndIgnoresMismatch()
    {
        var received = 0;
        var command = new AsyncRelayCommand<int>(n => { received = n; return Task.CompletedTask; });

        command.Execute(7);
        Assert.That(received, Is.EqualTo(7));

        command.Execute("not an int"); // type mismatch: must be a no-op, not a throw
        Assert.That(received, Is.EqualTo(7), "A parameter of the wrong type must not invoke the action.");
    }

    [Test]
    public void AsyncRelayCommandT_Execute_SwallowsFaultedTask()
    {
        var ran = false;
        var command = new AsyncRelayCommand<int>(_ =>
        {
            ran = true;
            throw new InvalidOperationException("boom");
        });

        Assert.DoesNotThrow(() => command.Execute(3));
        Assert.That(ran, Is.True);
    }

    [Test]
    public void AsyncRelayCommandT_CanExecute_HonorsPredicateAndTypeMatch()
    {
        var open = new AsyncRelayCommand<int>(_ => Task.CompletedTask);
        var positive = new AsyncRelayCommand<int>(_ => Task.CompletedTask, n => n > 0);

        Assert.Multiple(() =>
        {
            Assert.That(open.CanExecute(5), Is.True, "No predicate means always executable for a matching type.");
            Assert.That(positive.CanExecute(5), Is.True);
            Assert.That(positive.CanExecute(-1), Is.False);
            Assert.That(positive.CanExecute("x"), Is.False, "A wrong-typed parameter must fail CanExecute.");
        });
    }

    [Test]
    public void AsyncRelayCommandT_RaiseCanExecuteChanged_FiresEvent()
    {
        var command = new AsyncRelayCommand<int>(_ => Task.CompletedTask);
        var fired = false;
        command.CanExecuteChanged += (_, _) => fired = true;

        command.RaiseCanExecuteChanged();

        Assert.That(fired, Is.True);
    }

    [Test]
    public void AsyncRelayCommandT_ThrowsOnNullExecute()
        => Assert.Throws<ArgumentNullException>(() => _ = new AsyncRelayCommand<int>(null!));
}

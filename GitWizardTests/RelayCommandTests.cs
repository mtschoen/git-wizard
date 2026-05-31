using GitWizardUI.ViewModels;

namespace GitWizardTests;

public class RelayCommandTests
{
    [Test]
    public void Execute_InvokesAction()
    {
        var executed = false;
        var command = new RelayCommand(() => executed = true);

        command.Execute(null);

        Assert.That(executed, Is.True);
    }

    [Test]
    public void Execute_CanExecute_WithTruePredicate()
    {
        var command = new RelayCommand(() => { }, () => true);
        Assert.That(command.CanExecute(null), Is.True);
    }

    [Test]
    public void Execute_CanExecute_WithFalsePredicate()
    {
        var command = new RelayCommand(() => { }, () => false);
        Assert.That(command.CanExecute(null), Is.False);
    }

    [Test]
    public void Execute_CanExecute_WithoutPredicate_ReturnsTrue()
    {
        var command = new RelayCommand(() => { });
        Assert.That(command.CanExecute(null), Is.True);
    }

    [Test]
    public void RaiseCanExecuteChanged_FiresEvent()
    {
        var fired = false;
        var command = new RelayCommand(() => { });
        command.CanExecuteChanged += (_, _) => fired = true;

        command.RaiseCanExecuteChanged();

        Assert.That(fired, Is.True);
    }

    [Test]
    public void RelayCommand_ThrowsOnNullAction()
    {
        Assert.Throws<ArgumentNullException>(() => _ = new RelayCommand(null!));
    }

    [Test]
    public void ExecuteParameterized_InvokesActionWithParameter()
    {
        var receivedParameter = string.Empty;
        var command = new RelayCommand<string>(p => receivedParameter = p);

        command.Execute("test parameter");

        Assert.That(receivedParameter, Is.EqualTo("test parameter"));
    }

    [Test]
    public void ExecuteParameterized_CanExecute_WithTruePredicate()
    {
        var command = new RelayCommand<string>(_ => { }, _ => true);
        Assert.That(command.CanExecute("param"), Is.True);
    }

    [Test]
    public void ExecuteParameterized_CanExecute_WithFalsePredicate()
    {
        var command = new RelayCommand<string>(_ => { }, _ => false);
        Assert.That(command.CanExecute("param"), Is.False);
    }

    [Test]
    public void ExecuteParameterized_CanExecute_WithTypeMismatch_DoesNotThrow()
    {
        var command = new RelayCommand<string>(_ => { });
        // A non-string parameter no longer matches T, so CanExecute returns true (no predicate) without throwing
        Assert.DoesNotThrow(() => command.CanExecute(42));
    }

    [Test]
    public void ExecuteParameterized_RaiseCanExecuteChanged_FiresEvent()
    {
        var fired = false;
        var command = new RelayCommand<string>(_ => { });
        command.CanExecuteChanged += (_, _) => fired = true;

        command.RaiseCanExecuteChanged();

        Assert.That(fired, Is.True);
    }

    [Test]
    public void ExecuteParameterized_ThrowsOnNullAction()
    {
        Assert.Throws<ArgumentNullException>(() => _ = new RelayCommand<string>(null!));
    }

    [Test]
    public void ExecuteParameterized_IntParameter()
    {
        var receivedValue = 0;
        var command = new RelayCommand<int>(i => receivedValue = i);

        command.Execute(42);

        Assert.That(receivedValue, Is.EqualTo(42));
    }

    [Test]
    public void ExecuteParameterized_ObjectParameter()
    {
        var received = new object();
        object? captured = null;
        var command = new RelayCommand<object>(o => captured = o);

        command.Execute(received);

        Assert.That(captured, Is.SameAs(received));
    }

    [Test]
    public void CanExecuteChanged_Events_AreRaised()
    {
        var eventCount = 0;
        var command = new RelayCommand(() => { });
        command.CanExecuteChanged += (_, _) => eventCount++;

        command.RaiseCanExecuteChanged();
        command.RaiseCanExecuteChanged();
        command.RaiseCanExecuteChanged();

        Assert.That(eventCount, Is.EqualTo(3));
    }

    [Test]
    public void ExecuteParameterized_ComplexObject()
    {
        var obj = new { Name = "test", Value = 123 };
        var captured = (object?)null;
        var command = new RelayCommand<object>(o => captured = o);

        command.Execute(obj);

        Assert.That(captured, Is.Not.Null);
    }
}

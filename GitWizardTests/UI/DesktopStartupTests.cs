using System.Runtime.ExceptionServices;
using GitWizardUI;
using UiProgram = GitWizardUI.Program;

namespace GitWizardTests.UI;

public class DesktopStartupTests
{
    static string UniqueName() => $"GitWizardUI.Tests.{Guid.NewGuid():N}";

    static int OnOtherThread(Func<int> action)
    {
        var result = -1;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        })
        {
            IsBackground = true
        };
        thread.Start();
        Assert.That(thread.Join(TimeSpan.FromSeconds(10)), Is.True,
            "Startup did not finish; the guard must not block for ownership.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
        return result;
    }

    static int Start(string name, Func<string[], int> desktop, Action<string> notice)
        => UiProgram.Run([], _ => false,
            _ => null,
            () => name, desktop, notice);

    [Test]
    public void SecondStart_ReportsDuplicate_WithoutStartingDesktop()
    {
        var name = UniqueName();
        using var owner = DesktopInstance.CreateMutex(name);
        Assert.That(owner.WaitOne(0), Is.True);
        try
        {
            string? message = null;
            var starts = 0;
            var notices = 0;
            var result = OnOtherThread(() => Start(name, _ => { starts++; return 0; },
                value => { message = value; notices++; }));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(result, Is.EqualTo(1));
                Assert.That(message, Is.EqualTo("git-wizard is already running"));
                Assert.That(starts, Is.Zero);
                Assert.That(notices, Is.EqualTo(1));
            }
        }
        finally
        {
            owner.ReleaseMutex();
        }
    }

    [Test]
    public void FirstStart_HoldsMutexThroughDesktop_ThenReleasesIt()
    {
        var name = UniqueName();
        var result = Start(name, arguments =>
        {
            Assert.That(arguments, Is.Empty);
            var secondStart = OnOtherThread(() => Start(name,
                _ => throw new AssertionException("Second desktop started"), _ => { }));
            Assert.That(secondStart, Is.EqualTo(1));
            return 7;
        }, _ => throw new AssertionException("Unexpected notice"));
        Assert.That(result, Is.EqualTo(7));
        var finalStart = OnOtherThread(() => Start(name, _ => 8,
            _ => throw new AssertionException("Mutex leaked")));
        Assert.That(finalStart, Is.EqualTo(8));
    }

    [Test]
    public void DesktopFailure_ReleasesMutex_AndPropagates()
    {
        var name = UniqueName();
        Assert.Throws<InvalidOperationException>(() => Start(name,
            _ => throw new InvalidOperationException("startup failed"), _ => { }));
        Assert.That(OnOtherThread(() => Start(name, _ => 0,
            _ => throw new AssertionException("Mutex leaked"))), Is.Zero);
    }

    static int AcquireOnOtherThread(Mutex mutex) => OnOtherThread(() =>
    {
        Assert.That(mutex.WaitOne(0), Is.True);
        return 0;
    });

    [Test]
    public void AbandonedOwner_AllowsNextStart()
    {
        var name = UniqueName();
        using var retainedHandle = DesktopInstance.CreateMutex(name);
        var abandonedOwner = AcquireOnOtherThread(retainedHandle);
        Assert.That(abandonedOwner, Is.Zero);
        // The exited thread deliberately did not release; retain the kernel object.
        var started = Start(name, _ => 9,
            _ => throw new AssertionException("Abandoned owner rejected"));
        Assert.That(started, Is.EqualTo(9));
        var nextStart = OnOtherThread(() => Start(name, _ => 0, _ => { }));
        Assert.That(nextStart, Is.Zero);
    }

    [TestCase(false, true, 0)]
    [TestCase(false, false, 1)]
    [TestCase(true, true, 0)]
    public void ChildDispatch_PrecedesAnyMutexWork(bool broker, bool defenderSuccess,
        int expectedExitCode)
    {
        string[]? brokerArguments = null;
        var defenderCalls = 0;
        string[] arguments = broker
            ? ["--broker", "--pipe", "test-pipe", "--elevated-defender"]
            : ["--elevated-defender"];
        var result = UiProgram.Run(arguments, values => { brokerArguments = values; return broker; },
            _ => { defenderCalls++; return defenderSuccess ? 0 : 1; },
            () => throw new AssertionException("Child evaluated mutex name"),
            _ => throw new AssertionException("Child started desktop"),
            _ => throw new AssertionException("Child displayed notice"));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(expectedExitCode));
            Assert.That(brokerArguments, Is.SameAs(arguments));
            Assert.That(defenderCalls, Is.EqualTo(broker ? 0 : 1));
        }
    }

    [Test]
    public void Name_IsStableAcrossCalls()
    {
        var first = DesktopInstance.CreateName();
        var second = DesktopInstance.CreateName();
        Assert.That(first, Is.EqualTo(second));
        Assert.That(first, Does.StartWith("GitWizardUI.Desktop."));
    }
}

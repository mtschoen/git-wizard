using GitWizard.CLI;
using MFTLib;

namespace GitWizardTests;

/// <summary>
/// Verifies Program.TryHandleElevatedBrokerEntry's dispatch of MFTLib's broker child mode
/// with a fake <see cref="IElevatedEntryRunner"/> - no real elevated process, named pipe,
/// or scan involved. Real UAC/named-pipe behavior is a manual checkpoint (see the feature
/// report), not something a unit test can exercise.
/// </summary>
public class ElevatedBrokerEntryDispatchTests
{
    sealed class RecordingRunner : IElevatedEntryRunner
    {
        public int CallCount { get; private set; }
        public string? PipeName { get; private set; }
        public bool OneShot { get; private set; }

        public void RunBroker(string? pipeName, bool oneShot)
        {
            CallCount++;
            PipeName = pipeName;
            OneShot = oneShot;
        }
    }

    [Test]
    public void BrokerArgs_RouteToRunner_AndReturnTrue()
    {
        var runner = new RecordingRunner();

        var handled = Program.TryHandleElevatedBrokerEntry(
            ["--broker", "--pipe", "gitwizard-pipe-1"], runner);

        Assert.That(handled, Is.True);
        Assert.That(runner.CallCount, Is.EqualTo(1));
        Assert.That(runner.PipeName, Is.EqualTo("gitwizard-pipe-1"));
        Assert.That(runner.OneShot, Is.False);
    }

    [Test]
    public void NormalArgs_DoNotRoute_AndReturnFalse()
    {
        var runner = new RecordingRunner();

        var handled = Program.TryHandleElevatedBrokerEntry(["-summary", "-no-mft"], runner);

        Assert.That(handled, Is.False);
        Assert.That(runner.CallCount, Is.EqualTo(0));
    }

    [Test]
    public void NoArgs_DoNotRoute_AndReturnFalse()
    {
        var runner = new RecordingRunner();

        var handled = Program.TryHandleElevatedBrokerEntry([], runner);

        Assert.That(handled, Is.False);
        Assert.That(runner.CallCount, Is.EqualTo(0));
    }

    [Test]
    public void LeadingExecutablePath_IsSkipped_BrokerStillRoutes()
    {
        // Environment.GetCommandLineArgs() (what Main() actually passes) has the exe
        // path as element 0; dispatch must scan past it to find --broker.
        var runner = new RecordingRunner();

        var handled = Program.TryHandleElevatedBrokerEntry(
            [@"C:\apps\git-wizard.exe", "--broker", "--pipe", "p"], runner);

        Assert.That(handled, Is.True);
        Assert.That(runner.PipeName, Is.EqualTo("p"));
    }
}

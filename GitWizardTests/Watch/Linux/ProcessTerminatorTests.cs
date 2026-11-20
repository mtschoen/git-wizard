using System.Runtime.InteropServices;
using GitWizard.Watch.Linux;
using Microsoft.Win32.SafeHandles;

namespace GitWizardTests.Watch.Linux;

// The pre-hello stop signals through an identity acquired for the owned child, never a bare
// process id; these run against a fake signaller so no syscall is made and they hold on Windows.
[TestFixture]
public class ProcessTerminatorTests
{
    sealed class FakeIdentity : SafeHandleZeroOrMinusOneIsInvalid
    {
        public FakeIdentity()
            : base(true) => SetHandle(7);

        protected override bool ReleaseHandle() => true;
    }

    sealed class FakeSignaller(bool canOpen = true, int signalResult = 0) : IProcessSignaller
    {
        public int Opens { get; private set; }
        public List<SafeHandle> Signalled { get; } = [];
        public FakeIdentity? Opened { get; private set; }

        public bool TryOpen(int processId, out SafeHandle? identity, out int errorNumber)
        {
            Opens++;
            errorNumber = canOpen ? 0 : 38;
            Opened = canOpen ? new FakeIdentity() : null;
            identity = Opened;
            return canOpen;
        }

        public int Terminate(SafeHandle identity)
        {
            Signalled.Add(identity);
            return signalResult;
        }
    }

    [Test]
    public void ChildAlreadyExitedWhenIdentityAcquired_NeverSignals_AndReleasesTheIdentity()
    {
        var signaller = new FakeSignaller();
        var log = new List<string>();

        using var terminator = ProcessTerminator.Create(42, () => true, signaller, log.Add);
        terminator.Terminate();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(terminator.HasIdentity, Is.False);
            Assert.That(signaller.Signalled, Is.Empty);
            Assert.That(signaller.Opened!.IsClosed, Is.True);
            Assert.That(log, Has.Count.EqualTo(1));
        }
    }

    [Test]
    public void IdentityAcquired_StopSignalsExactlyOnceThroughTheHandle()
    {
        var signaller = new FakeSignaller();

        using var terminator = ProcessTerminator.Create(42, () => false, signaller, _ => { });
        terminator.Terminate();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(terminator.HasIdentity, Is.True);
            Assert.That(signaller.Signalled, Has.Count.EqualTo(1));
            Assert.That(signaller.Signalled[0], Is.SameAs(signaller.Opened));
        }
    }

    [Test]
    public void IdentityOpenFailed_NeverSignals_AndLogsADiagnostic()
    {
        var signaller = new FakeSignaller(canOpen: false);
        var log = new List<string>();

        using var terminator = ProcessTerminator.Create(42, () => false, signaller, log.Add);
        terminator.Terminate();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(terminator.HasIdentity, Is.False);
            Assert.That(signaller.Signalled, Is.Empty);
            Assert.That(log, Has.Count.EqualTo(1));
            Assert.That(log[0], Does.Contain("will not be signalled"));
        }
    }

    [Test]
    public void SignalReturnsNoSuchProcess_IsTreatedAsExited_WithoutADiagnostic()
    {
        var signaller = new FakeSignaller(signalResult: 3);
        var log = new List<string>();

        using var terminator = ProcessTerminator.Create(42, () => false, signaller, log.Add);

        Assert.That(terminator.Terminate, Throws.Nothing);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(signaller.Signalled, Has.Count.EqualTo(1));
            Assert.That(log, Is.Empty);
        }
    }

    [Test]
    public void SignalRefused_IsLoggedNotThrown()
    {
        var signaller = new FakeSignaller(signalResult: 1);
        var log = new List<string>();

        using var terminator = ProcessTerminator.Create(42, () => false, signaller, log.Add);
        terminator.Terminate();

        Assert.That(log, Has.Count.EqualTo(1));
    }

    [Test]
    public void StopAfterDispose_NeverSignals()
    {
        var signaller = new FakeSignaller();
        var terminator = ProcessTerminator.Create(42, () => false, signaller, _ => { });

        terminator.Dispose();
        terminator.Terminate();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(signaller.Signalled, Is.Empty);
            Assert.That(signaller.Opened!.IsClosed, Is.True);
        }
    }
}

using System.Runtime.Versioning;
using GitWizard.Watch.Linux;
using GitWizardTests.TestSupport;
using Win32Exception = System.ComponentModel.Win32Exception;

namespace GitWizardTests.Watch.Linux;

public class FanotifyInteropTests
{
    const int InvalidArgument = 22;
    const int NoDevice = 19;
    const int NoSystemCall = 38;
    const int NotPermitted = 1;
    const int OperationNotSupported = 95;
    static readonly string ExistingDirectoryPath = TempDirectory.SystemTempPath;

    [Test]
    public void MinimumKernelVersion_IsLinuxFivePointNine()
    {
        Assert.That(FanotifyInterop.MinimumKernelVersion, Is.EqualTo(new Version(5, 9)));
    }

    [Test]
    public void ProbeFlags_MatchLinuxUapiValues()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(FanotifyConstants.ReportDirectoryFileIdAndName, Is.EqualTo(0x00000c00));
            Assert.That(FanotifyConstants.MarkFilesystem, Is.EqualTo(0x00000100));
            Assert.That(FanotifyConstants.ProbeEventMask, Is.EqualTo(0x400003c2));
            Assert.That(FanotifyConstants.CurrentWorkingDirectory, Is.EqualTo(-100));
        }
    }

    [Test]
    public void Probe_WhitespacePath_ThrowsBeforeCallingNativeApi()
    {
        var native = new FakeFanotifyNative(3);
        var probe = new FanotifyProbe(native);

        Assert.Throws<ArgumentException>(() => probe.Probe(" "));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(native.InitializeCallCount, Is.Zero);
            Assert.That(native.MarkCalls, Is.Empty);
            Assert.That(native.ClosedDescriptors, Is.Empty);
        }
    }

    [Test]
    public void Probe_FilePath_ThrowsBeforeNativeCanReportUnsupportedKernel()
    {
        var filePath = Path.GetTempFileName();
        try
        {
            var native = new FakeFanotifyNative(
                3,
                new FanotifyNativeCallResult(-1, InvalidArgument));
            var probe = new FanotifyProbe(native);

            var exception = Assert.Throws<ArgumentException>(() => probe.Probe(filePath));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(exception.ParamName, Is.EqualTo("path"));
                Assert.That(native.InitializeCallCount, Is.Zero);
                Assert.That(native.MarkCalls, Is.Empty);
                Assert.That(native.ClosedDescriptors, Is.Empty);
            }
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Test]
    public void Probe_MissingPath_ThrowsBeforeCallingNativeApi()
    {
        using var missing = new TempDirectory("gw-missing-", create: false);
        var missingPath = missing.Path;
        var native = new FakeFanotifyNative(3);
        var probe = new FanotifyProbe(native);

        var exception = Assert.Throws<ArgumentException>(() => probe.Probe(missingPath));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception.ParamName, Is.EqualTo("path"));
            Assert.That(native.InitializeCallCount, Is.Zero);
            Assert.That(native.MarkCalls, Is.Empty);
            Assert.That(native.ClosedDescriptors, Is.Empty);
        }
    }

    [Test]
    public void Probe_Success_AddsAndRemovesFilesystemMarkAndClosesDescriptor()
    {
        var native = new FakeFanotifyNative(41);
        var probe = new FanotifyProbe(native);

        var result = probe.Probe(ExistingDirectoryPath);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(FanotifyCapabilityStatus.Available));
            Assert.That(result.IsSupported, Is.True);
            Assert.That(result.RequiresElevation, Is.False);
            Assert.That(result.ErrorNumber, Is.Zero);
            Assert.That(native.InitializeFlags, Is.EqualTo(
                FanotifyConstants.CloseOnExecute | FanotifyConstants.ReportDirectoryFileIdAndName));
            Assert.That(native.EventFileFlags, Is.EqualTo(FanotifyConstants.ReadOnly));
            Assert.That(native.MarkCalls, Has.Count.EqualTo(2));
            Assert.That(native.MarkCalls[0].Descriptor, Is.EqualTo(41));
            Assert.That(native.MarkCalls[0].Flags, Is.EqualTo(
                FanotifyConstants.MarkAdd | FanotifyConstants.MarkFilesystem));
            Assert.That(native.MarkCalls[0].Mask, Is.EqualTo(FanotifyConstants.ProbeEventMask));
            Assert.That(native.MarkCalls[0].DirectoryDescriptor, Is.EqualTo(
                FanotifyConstants.CurrentWorkingDirectory));
            Assert.That(native.MarkCalls[0].Path, Is.EqualTo(ExistingDirectoryPath));
            Assert.That(native.MarkCalls[1].Descriptor, Is.EqualTo(41));
            Assert.That(native.MarkCalls[1].Flags, Is.EqualTo(
                FanotifyConstants.MarkRemove | FanotifyConstants.MarkFilesystem));
            Assert.That(native.MarkCalls[1].Mask, Is.EqualTo(FanotifyConstants.ProbeEventMask));
            Assert.That(native.MarkCalls[1].DirectoryDescriptor, Is.EqualTo(
                FanotifyConstants.CurrentWorkingDirectory));
            Assert.That(native.MarkCalls[1].Path, Is.EqualTo(ExistingDirectoryPath));
            Assert.That(native.ClosedDescriptors, Is.EqualTo([41]));
        });
    }

    [Test]
    public void Probe_FileDescriptorZero_IsValidAndClosed()
    {
        var native = new FakeFanotifyNative(0);
        var probe = new FanotifyProbe(native);

        var result = probe.Probe(ExistingDirectoryPath);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Status, Is.EqualTo(FanotifyCapabilityStatus.Available));
            Assert.That(native.MarkCalls, Has.Count.EqualTo(2));
            Assert.That(native.ClosedDescriptors, Is.EqualTo([0]));
        }
    }

    [TestCase(NotPermitted, FanotifyCapabilityStatus.RequiresElevation, true, true)]
    [TestCase(NoSystemCall, FanotifyCapabilityStatus.UnsupportedKernel, false, false)]
    [TestCase(InvalidArgument, FanotifyCapabilityStatus.UnsupportedKernel, false, false)]
    public void Probe_ExpectedInitializeError_ReturnsCapability(
        int errorNumber,
        FanotifyCapabilityStatus expectedStatus,
        bool expectedSupported,
        bool expectedRequiresElevation)
    {
        var native = new FakeFanotifyNative(new FanotifyNativeCallResult(-1, errorNumber));
        var probe = new FanotifyProbe(native);

        var result = probe.Probe(ExistingDirectoryPath);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Status, Is.EqualTo(expectedStatus));
            Assert.That(result.IsSupported, Is.EqualTo(expectedSupported));
            Assert.That(result.RequiresElevation, Is.EqualTo(expectedRequiresElevation));
            Assert.That(result.ErrorNumber, Is.EqualTo(errorNumber));
            Assert.That(native.MarkCalls, Is.Empty);
            Assert.That(native.ClosedDescriptors, Is.Empty);
        }
    }

    [TestCase(NotPermitted, FanotifyCapabilityStatus.RequiresElevation, true, true)]
    [TestCase(NoSystemCall, FanotifyCapabilityStatus.UnsupportedKernel, false, false)]
    [TestCase(InvalidArgument, FanotifyCapabilityStatus.UnsupportedKernel, false, false)]
    [TestCase(NoDevice, FanotifyCapabilityStatus.UnsupportedFileSystem, false, false)]
    [TestCase(OperationNotSupported, FanotifyCapabilityStatus.UnsupportedFileSystem, false, false)]
    public void Probe_ExpectedMarkError_ReturnsCapabilityAndClosesDescriptor(
        int errorNumber,
        FanotifyCapabilityStatus expectedStatus,
        bool expectedSupported,
        bool expectedRequiresElevation)
    {
        var native = new FakeFanotifyNative(7, new FanotifyNativeCallResult(-1, errorNumber));
        var probe = new FanotifyProbe(native);

        var result = probe.Probe(ExistingDirectoryPath);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Status, Is.EqualTo(expectedStatus));
            Assert.That(result.IsSupported, Is.EqualTo(expectedSupported));
            Assert.That(result.RequiresElevation, Is.EqualTo(expectedRequiresElevation));
            Assert.That(result.ErrorNumber, Is.EqualTo(errorNumber));
            Assert.That(native.MarkCalls, Has.Count.EqualTo(1));
            Assert.That(native.ClosedDescriptors, Is.EqualTo([7]));
        }
    }

    [Test]
    public void Probe_UnexpectedInitializeError_ThrowsWin32Exception()
    {
        var native = new FakeFanotifyNative(new FanotifyNativeCallResult(-1, 12));
        var probe = new FanotifyProbe(native);

        var exception = Assert.Throws<Win32Exception>(() => probe.Probe(ExistingDirectoryPath));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception.NativeErrorCode, Is.EqualTo(12));
            Assert.That(native.MarkCalls, Is.Empty);
            Assert.That(native.ClosedDescriptors, Is.Empty);
        }
    }

    [Test]
    public void Probe_UnexpectedAddMarkError_ThrowsAndClosesDescriptor()
    {
        var native = new FakeFanotifyNative(9, new FanotifyNativeCallResult(-1, 28));
        var probe = new FanotifyProbe(native);

        var exception = Assert.Throws<Win32Exception>(() => probe.Probe(ExistingDirectoryPath));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception.NativeErrorCode, Is.EqualTo(28));
            Assert.That(native.MarkCalls, Has.Count.EqualTo(1));
            Assert.That(native.ClosedDescriptors, Is.EqualTo([9]));
        }
    }

    [Test]
    public void Probe_UnexpectedRemoveMarkError_ThrowsAndClosesDescriptor()
    {
        var native = new FakeFanotifyNative(
            11,
            new FanotifyNativeCallResult(0, 0),
            new FanotifyNativeCallResult(-1, 5));
        var probe = new FanotifyProbe(native);

        var exception = Assert.Throws<Win32Exception>(() => probe.Probe(ExistingDirectoryPath));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception.NativeErrorCode, Is.EqualTo(5));
            Assert.That(native.MarkCalls, Has.Count.EqualTo(2));
            Assert.That(native.ClosedDescriptors, Is.EqualTo([11]));
        }
    }

    [Test]
    [Platform("Linux")]
    [SupportedOSPlatform("linux")]
    public void OpenDirectory_ReturnsADescriptorOpenByHandleAcceptsAsAMount()
    {
        // open_by_handle_at fails with EBADF for an O_PATH mount descriptor, so the mount descriptor must not carry it.
        const int openPathFlag = 0x200000;
        var opened = FanotifyNative.Instance.OpenDirectory(TempDirectory.SystemTempPath);
        Assert.That(opened.Value, Is.GreaterThanOrEqualTo(0), $"errno {opened.ErrorNumber}");
        try
        {
            var flagsLine = File.ReadLines($"/proc/self/fdinfo/{opened.Value}")
                .Single(line => line.StartsWith("flags:", StringComparison.Ordinal));
            var flags = Convert.ToInt32(flagsLine["flags:".Length..].Trim(), 8);

            Assert.That(flags & openPathFlag, Is.Zero);
        }
        finally
        {
            FanotifyNative.Instance.Close(opened.Value);
        }
    }

    [Test]
    [Category("RequiresRoot")]
    [Platform("Linux")]
    [SupportedOSPlatform("linux")]
    public void Probe_RealFanotify_MarksAndUnmarksTemporaryFilesystem()
    {
        var result = FanotifyInterop.Probe(TempDirectory.SystemTempPath);
        if (result.RequiresElevation)
        {
            Assert.Inconclusive("Requires root or CAP_SYS_ADMIN");
            return;
        }

        Assert.That(result.Status, Is.EqualTo(FanotifyCapabilityStatus.Available));
    }
}

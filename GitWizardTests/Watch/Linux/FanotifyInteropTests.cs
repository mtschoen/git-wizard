using System.Runtime.Versioning;
using GitWizard.Watch.Linux;
using Win32Exception = System.ComponentModel.Win32Exception;

namespace GitWizardTests.Watch.Linux;

public class FanotifyInteropTests
{
    const int InvalidArgument = 22;
    const int NoDevice = 19;
    const int NoSystemCall = 38;
    const int NotPermitted = 1;
    const int OperationNotSupported = 95;
    static readonly string ExistingDirectoryPath = Path.GetTempPath();

    [Test]
    public void MinimumKernelVersion_IsLinuxFivePointNine()
    {
        Assert.That(FanotifyInterop.MinimumKernelVersion, Is.EqualTo(new Version(5, 9)));
    }

    [Test]
    public void ProbeFlags_MatchLinuxUapiValues()
    {
        Assert.Multiple(() =>
        {
            Assert.That(FanotifyConstants.ReportDirectoryFileIdAndName, Is.EqualTo(0x00000c00));
            Assert.That(FanotifyConstants.MarkFilesystem, Is.EqualTo(0x00000100));
            Assert.That(FanotifyConstants.ProbeEventMask, Is.EqualTo(0x400003c2));
            Assert.That(FanotifyConstants.CurrentWorkingDirectory, Is.EqualTo(-100));
        });
    }

    [Test]
    public void Probe_WhitespacePath_ThrowsBeforeCallingNativeApi()
    {
        var native = new FakeFanotifyNative(3);
        var probe = new FanotifyProbe(native);

        Assert.Throws<ArgumentException>(() => probe.Probe(" "));

        Assert.Multiple(() =>
        {
            Assert.That(native.InitializeCallCount, Is.Zero);
            Assert.That(native.MarkCalls, Is.Empty);
            Assert.That(native.ClosedDescriptors, Is.Empty);
        });
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

            Assert.Multiple(() =>
            {
                Assert.That(exception.ParamName, Is.EqualTo("path"));
                Assert.That(native.InitializeCallCount, Is.Zero);
                Assert.That(native.MarkCalls, Is.Empty);
                Assert.That(native.ClosedDescriptors, Is.Empty);
            });
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Test]
    public void Probe_MissingPath_ThrowsBeforeCallingNativeApi()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var native = new FakeFanotifyNative(3);
        var probe = new FanotifyProbe(native);

        var exception = Assert.Throws<ArgumentException>(() => probe.Probe(missingPath));

        Assert.Multiple(() =>
        {
            Assert.That(exception.ParamName, Is.EqualTo("path"));
            Assert.That(native.InitializeCallCount, Is.Zero);
            Assert.That(native.MarkCalls, Is.Empty);
            Assert.That(native.ClosedDescriptors, Is.Empty);
        });
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
            Assert.That(native.ClosedDescriptors, Is.EqualTo(new[] { 41 }));
        });
    }

    [Test]
    public void Probe_FileDescriptorZero_IsValidAndClosed()
    {
        var native = new FakeFanotifyNative(0);
        var probe = new FanotifyProbe(native);

        var result = probe.Probe(ExistingDirectoryPath);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(FanotifyCapabilityStatus.Available));
            Assert.That(native.MarkCalls, Has.Count.EqualTo(2));
            Assert.That(native.ClosedDescriptors, Is.EqualTo(new[] { 0 }));
        });
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

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(expectedStatus));
            Assert.That(result.IsSupported, Is.EqualTo(expectedSupported));
            Assert.That(result.RequiresElevation, Is.EqualTo(expectedRequiresElevation));
            Assert.That(result.ErrorNumber, Is.EqualTo(errorNumber));
            Assert.That(native.MarkCalls, Is.Empty);
            Assert.That(native.ClosedDescriptors, Is.Empty);
        });
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

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(expectedStatus));
            Assert.That(result.IsSupported, Is.EqualTo(expectedSupported));
            Assert.That(result.RequiresElevation, Is.EqualTo(expectedRequiresElevation));
            Assert.That(result.ErrorNumber, Is.EqualTo(errorNumber));
            Assert.That(native.MarkCalls, Has.Count.EqualTo(1));
            Assert.That(native.ClosedDescriptors, Is.EqualTo(new[] { 7 }));
        });
    }

    [Test]
    public void Probe_UnexpectedInitializeError_ThrowsWin32Exception()
    {
        var native = new FakeFanotifyNative(new FanotifyNativeCallResult(-1, 12));
        var probe = new FanotifyProbe(native);

        var exception = Assert.Throws<Win32Exception>(() => probe.Probe(ExistingDirectoryPath));

        Assert.Multiple(() =>
        {
            Assert.That(exception.NativeErrorCode, Is.EqualTo(12));
            Assert.That(native.MarkCalls, Is.Empty);
            Assert.That(native.ClosedDescriptors, Is.Empty);
        });
    }

    [Test]
    public void Probe_UnexpectedAddMarkError_ThrowsAndClosesDescriptor()
    {
        var native = new FakeFanotifyNative(9, new FanotifyNativeCallResult(-1, 28));
        var probe = new FanotifyProbe(native);

        var exception = Assert.Throws<Win32Exception>(() => probe.Probe(ExistingDirectoryPath));

        Assert.Multiple(() =>
        {
            Assert.That(exception.NativeErrorCode, Is.EqualTo(28));
            Assert.That(native.MarkCalls, Has.Count.EqualTo(1));
            Assert.That(native.ClosedDescriptors, Is.EqualTo(new[] { 9 }));
        });
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

        Assert.Multiple(() =>
        {
            Assert.That(exception.NativeErrorCode, Is.EqualTo(5));
            Assert.That(native.MarkCalls, Has.Count.EqualTo(2));
            Assert.That(native.ClosedDescriptors, Is.EqualTo(new[] { 11 }));
        });
    }

    [Test]
    [Category("RequiresRoot")]
    [Platform("Linux")]
    [SupportedOSPlatform("linux")]
    public void Probe_RealFanotify_MarksAndUnmarksTemporaryFilesystem()
    {
        var result = FanotifyInterop.Probe(Path.GetTempPath());
        if (result.RequiresElevation)
        {
            Assert.Inconclusive("Requires root or CAP_SYS_ADMIN");
            return;
        }

        Assert.That(result.Status, Is.EqualTo(FanotifyCapabilityStatus.Available));
    }
}

internal sealed class FakeFanotifyNative : IFanotifyNative
{
    readonly Queue<FanotifyNativeCallResult> _markResults;
    readonly FanotifyNativeCallResult _initializeResult;

    public FakeFanotifyNative(int descriptor, params FanotifyNativeCallResult[] markResults)
        : this(new FanotifyNativeCallResult(descriptor, 0), markResults)
    {
    }

    public FakeFanotifyNative(
        FanotifyNativeCallResult initializeResult,
        params FanotifyNativeCallResult[] markResults)
    {
        _initializeResult = initializeResult;
        _markResults = new Queue<FanotifyNativeCallResult>(markResults);
    }

    public int InitializeCallCount { get; private set; }
    public uint InitializeFlags { get; private set; }
    public uint EventFileFlags { get; private set; }
    public List<FanotifyMarkCall> MarkCalls { get; } = new();
    public List<int> ClosedDescriptors { get; } = new();

    public FanotifyNativeCallResult Initialize(uint flags, uint eventFileFlags)
    {
        InitializeCallCount++;
        InitializeFlags = flags;
        EventFileFlags = eventFileFlags;
        return _initializeResult;
    }

    public FanotifyNativeCallResult Mark(
        int descriptor,
        uint flags,
        ulong mask,
        int directoryDescriptor,
        string path)
    {
        MarkCalls.Add(new FanotifyMarkCall(descriptor, flags, mask, directoryDescriptor, path));
        return _markResults.Count == 0 ? new FanotifyNativeCallResult(0, 0) : _markResults.Dequeue();
    }

    public int Close(int descriptor)
    {
        ClosedDescriptors.Add(descriptor);
        return 0;
    }
}

internal readonly record struct FanotifyMarkCall(
    int Descriptor,
    uint Flags,
    ulong Mask,
    int DirectoryDescriptor,
    string Path);

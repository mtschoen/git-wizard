using System.Runtime.Versioning;
using GitWizard.Watch;
using GitWizard.Watch.Linux;
using GitWizardTests.TestSupport;

namespace GitWizardTests.Watch.Linux;

// Fanotify source tests are split by concern because the combined fixture exceeds 500 lines.
[TestFixture]
[SupportedOSPlatform("linux")]
public class FanotifyVolumeChangeSourceArmTests
{
    static readonly string ExistingDirectoryPath = TempDirectory.SystemTempPath;

    [Test]
    public async Task Constructor_Default_CreatesInstance()
    {
        await using var source = new FanotifyVolumeChangeSource();
        Assert.That(source, Is.Not.Null);
    }

    [Test]
    public void ArmAndCatchUpAsync_NullVolumes_ThrowsArgumentNullException()
    {
        var native = new FakeFanotifyNative();
        var source = new FanotifyVolumeChangeSource(native);

        Assert.ThrowsAsync<ArgumentNullException>(() =>
            source.ArmAndCatchUpAsync(null!, CancellationToken.None));
    }

    [Test]
    public void ArmAndCatchUpAsync_CancellationRequested_ThrowsOperationCanceledException()
    {
        var native = new FakeFanotifyNative();
        var source = new FanotifyVolumeChangeSource(native);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(() =>
            source.ArmAndCatchUpAsync([ExistingDirectoryPath], cts.Token));
    }

    [Test]
    public async Task ArmAndCatchUpAsync_EmptyVolumes_ReturnsEmptyErrors()
    {
        var native = new FakeFanotifyNative();
        var source = new FanotifyVolumeChangeSource(native);

        var result = await source.ArmAndCatchUpAsync(Array.Empty<string>(), CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Errors, Is.Empty);
            Assert.That(native.InitializeCallCount, Is.Zero);
        }
    }

    [Test]
    public async Task ArmAndCatchUpAsync_InitializeFails_PopulatesErrorsForVolumes()
    {
        var native = new FakeFanotifyNative
        {
            InitializeResult = new FanotifyNativeCallResult(-1, FanotifyError.NotPermitted)
        };
        var source = new FanotifyVolumeChangeSource(native);

        var result = await source.ArmAndCatchUpAsync(["/", "/mnt/data"], CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Errors, Has.Count.EqualTo(2));
            Assert.That(result.Errors["/"], Does.Contain("fanotify_init failed"));
            Assert.That(result.Errors["/mnt/data"], Does.Contain("fanotify_init failed"));
            Assert.That(native.MarkCalls, Is.Empty);
        });
    }

    [Test]
    public async Task ArmAndCatchUpAsync_WhitespaceAndMissingDirectory_RecordsErrors()
    {
        var native = new FakeFanotifyNative
        {
            InitializeResult = new FanotifyNativeCallResult(5, 0)
        };
        var source = new FanotifyVolumeChangeSource(native);
        using var missing = new TempDirectory("gw-missing-", create: false);
        var missingPath = missing.Path;

        var result = await source.ArmAndCatchUpAsync(["  ", missingPath], CancellationToken.None);


        Assert.Multiple(() =>
        {
            Assert.That(result.Errors, Has.Count.EqualTo(2));
            Assert.That(result.Errors["  "], Does.Contain("whitespace"));
            Assert.That(result.Errors[missingPath], Does.Contain("does not exist"));
            Assert.That(native.MarkCalls, Is.Empty);
        });
    }

    [Test]
    public async Task ArmAndCatchUpAsync_MarkFails_RecordsErrorForFailedVolume()
    {
        var native = new FakeFanotifyNative
        {
            InitializeResult = new FanotifyNativeCallResult(5, 0)
        };
        native.MarkResults.Enqueue(new FanotifyNativeCallResult(-1, FanotifyError.OperationNotSupported));
        var source = new FanotifyVolumeChangeSource(native);

        var result = await source.ArmAndCatchUpAsync([ExistingDirectoryPath], CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Errors, Has.Count.EqualTo(1));
            Assert.That(result.Errors[ExistingDirectoryPath], Does.Contain("fanotify_mark failed"));
            Assert.That(native.MarkCalls, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task ArmAndCatchUpAsync_Success_MarksFilesystem()
    {
        var native = new FakeFanotifyNative
        {
            InitializeResult = new FanotifyNativeCallResult(12, 0)
        };
        var source = new FanotifyVolumeChangeSource(native);

        var result = await source.ArmAndCatchUpAsync([ExistingDirectoryPath], CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Errors, Is.Empty);
            Assert.That(native.InitializeFlags, Is.EqualTo(
                FanotifyConstants.CloseOnExecute | FanotifyConstants.ReportDirectoryFileIdAndName));
            Assert.That(native.EventFileFlags, Is.EqualTo(FanotifyConstants.ReadOnly));
            Assert.That(native.MarkCalls, Has.Count.EqualTo(1));
            Assert.That(native.MarkCalls[0].Descriptor, Is.EqualTo(12));
            Assert.That(native.MarkCalls[0].Path, Is.EqualTo(ExistingDirectoryPath));
            Assert.That(native.MarkCalls[0].Flags, Is.EqualTo(
                FanotifyConstants.MarkAdd | FanotifyConstants.MarkFilesystem));
        });
    }

    [Test]
    public async Task ArmAndCatchUpAsync_CalledTwice_ReArmsAndDisposesPreviousDescriptor()
    {
        var native = new FakeFanotifyNative
        {
            InitializeResult = new FanotifyNativeCallResult(101, 0)
        };

        var source = new FanotifyVolumeChangeSource(native);
        await source.ArmAndCatchUpAsync([ExistingDirectoryPath], CancellationToken.None);

        native.InitializeResult = new FanotifyNativeCallResult(102, 0);
        await source.ArmAndCatchUpAsync([ExistingDirectoryPath], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(native.ClosedDescriptors, Does.Contain(101));
            Assert.That(native.MarkCalls, Has.Count.EqualTo(2));
        }
    }

    [Test]
    public async Task ArmPublishesMarkReadinessPerRoot()
    {
        var native = new FakeFanotifyNative
        {
            InitializeResult = new FanotifyNativeCallResult(5, 0)
        };
        native.MarkResults.Enqueue(new FanotifyNativeCallResult(0, 0));
        native.MarkResults.Enqueue(new FanotifyNativeCallResult(-1, 13));
        using var directory = new TempDirectory("gw-fanotify-");
        var tempRoot = directory.Path;
        var okPath = Path.Combine(tempRoot, "ok");
        Directory.CreateDirectory(okPath);
        var brokenPath = Path.Combine(tempRoot, "broken");
        Directory.CreateDirectory(brokenPath);

        var source = new FanotifyVolumeChangeSource(native);
        var snapshots = new List<VolumeReadinessSnapshot>();
        source.ReadinessChanged += snapshots.Add;
        Assert.That(source.Mode, Is.EqualTo(WatchReadinessMode.FilesystemMarks));

        var result = await source.ArmAndCatchUpAsync([okPath, brokenPath], CancellationToken.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(source.Readiness.IsSettled, Is.True);
            Assert.That(source.Readiness.CaughtUpVolumes, Is.EqualTo([okPath]));
        }
        var excluded = source.Readiness.ExcludedVolumes.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(excluded.Volume, Is.EqualTo(brokenPath));
            Assert.That(excluded.ExclusionReason, Is.EqualTo(result.Errors[brokenPath]));
            Assert.That(snapshots, Has.Count.EqualTo(2));
        }
    }
    [Test]
    public void WatchAsync_BeforeArm_ThrowsInvalidOperationException()
    {
        var native = new FakeFanotifyNative();
        var source = new FanotifyVolumeChangeSource(native);

        var enumerator = source.WatchAsync(CancellationToken.None).GetAsyncEnumerator();

        Assert.ThrowsAsync<InvalidOperationException>(async () => await enumerator.MoveNextAsync());
    }

    [Test]
    public async Task WatchAsync_AllVolumesFailedToArm_CompletesImmediately()
    {
        var native = new FakeFanotifyNative
        {
            InitializeResult = new FanotifyNativeCallResult(10, 0)
        };
        native.MarkResults.Enqueue(new FanotifyNativeCallResult(-1, FanotifyError.OperationNotSupported));
        var source = new FanotifyVolumeChangeSource(native);

        await source.ArmAndCatchUpAsync([ExistingDirectoryPath], CancellationToken.None);

        var batches = new List<VolumeChangeBatch>();
        await foreach (var batch in source.WatchAsync(CancellationToken.None))
        {
            batches.Add(batch);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(batches, Is.Empty);
            Assert.That(source.PumpStarted.IsCompleted, Is.False,
                "No usable volumes means PumpEventsAsync never ran; a consumer waiting on PumpStarted must not be told it did.");
        }
    }

}

using System.Runtime.Versioning;
using GitWizard.Watch;
using GitWizard.Watch.Linux;
using GitWizardTests.TestSupport;

namespace GitWizardTests.Watch.Linux;

// Fanotify source tests are split by concern because the combined fixture exceeds 500 lines.
[TestFixture]
[SupportedOSPlatform("linux")]
public class FanotifyVolumeChangeSourceIntegrationTests
{
    [Test]
    [Category("RequiresRoot")]
    [Platform("Linux")]
    [SupportedOSPlatform("linux")]
    public async Task RealFanotify_DetectsFileCreationAndDeletion()
    {
        var probe = FanotifyInterop.Probe(TempDirectory.SystemTempPath);
        if (probe.RequiresElevation)
        {
            Assert.Inconclusive("Requires root or CAP_SYS_ADMIN");
            return;
        }

        var tempDir = Directory.CreateTempSubdirectory("git-wizard-watch-test").FullName;
        try
        {
            using var tmpfs = TemporaryTmpfsMount.TryMount(Path.Combine(tempDir, "tmpfs"), out var mountFailure);
            if (tmpfs is null)
            {
                Assert.Inconclusive($"Mounting a tmpfs for the second filesystem needs root: {mountFailure}");
                return;
            }

            var outerFilesystemId = ReadFilesystemIdentifier(tempDir);
            var tmpfsFilesystemId = ReadFilesystemIdentifier(tmpfs.Path);
            Assert.That(tmpfsFilesystemId, Is.Not.EqualTo(outerFilesystemId),
                "the tmpfs and its parent must be distinct filesystems for fsid matching to be exercised");

            await using var source = new FanotifyVolumeChangeSource();
            using var armCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var armResult = await source.ArmAndCatchUpAsync([tempDir, tmpfs.Path], armCts.Token);
            Assert.That(armResult.Errors, Is.Empty);

            using var watchCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var watchToken = watchCts.Token;
            var outerFile = Path.Combine(tempDir, "test.txt");
            var tmpfsFile = Path.Combine(tmpfs.Path, "test.txt");
            var expected = new HashSet<(string Path, VolumeEntryKind Kind)>
            {
                (outerFile, VolumeEntryKind.Created), (outerFile, VolumeEntryKind.Deleted),
                (tmpfsFile, VolumeEntryKind.Created), (tmpfsFile, VolumeEntryKind.Deleted)
            };

            // fanotify merges a create and a delete of one name into a single event, so each file is
            // deleted only after its creation was observed.
            foreach (var file in new[] { outerFile, tmpfsFile })
            {
                await File.WriteAllTextAsync(file, "hello", watchToken);
            }

            var observed = new HashSet<(string Path, VolumeEntryKind Kind)>();
            var deleted = false;
            try
            {
                await foreach (var batch in source.WatchAsync(watchToken))
                {
                    foreach (var entry in batch.Entries)
                    {
                        observed.Add((entry.FullPath, entry.Kind));
                    }

                    if (!deleted && observed.Contains((outerFile, VolumeEntryKind.Created))
                                 && observed.Contains((tmpfsFile, VolumeEntryKind.Created)))
                    {
                        deleted = true;
                        File.Delete(outerFile);
                        File.Delete(tmpfsFile);
                    }

                    if (expected.IsSubsetOf(observed))
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // The deadline passed; the assertion below names what was and was not observed.
            }

            Assert.That(expected.Except(observed), Is.Empty,
                "creations and deletions on both real filesystems");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [SupportedOSPlatform("linux")]
    static long ReadFilesystemIdentifier(string directory)
    {
        var opened = FanotifyNative.Instance.OpenDirectory(directory);
        Assert.That(opened.Value, Is.GreaterThanOrEqualTo(0), $"open {directory} (errno {opened.ErrorNumber})");
        try
        {
            Assert.That(FanotifyNative.Instance.TryReadFilesystemIdentifier(opened.Value, out var filesystemId), Is.True);
            return filesystemId;
        }
        finally
        {
            FanotifyNative.Instance.Close(opened.Value);
        }
    }

}

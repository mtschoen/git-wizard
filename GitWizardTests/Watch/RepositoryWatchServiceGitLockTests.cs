using System.Collections.Concurrent;
using GitWizard;
using GitWizard.Watch;
using GitWizardTests.TestSupport;
using static GitWizardTests.TestSupport.WatchEventDrain;

namespace GitWizardTests.Watch;

// Covers RepositoryWatchService delivery of the git-directory lock-only noise rule.
[TestFixture]
[NonParallelizable]
public class RepositoryWatchServiceGitLockTests
{
    static RepositoryWatchService CreateService(params VolumeChangeBatch[] batches) =>
        new(new ScriptedVolumeChangeSource(batches),
            trackedRoots: [@"C:\repo\a", @"C:\repo\b"],
            searchRoots: [@"C:\repo"],
            debounce: TimeSpan.FromMilliseconds(10));

    static VolumeChangeEntry Entry(string path, VolumeEntryKind kind = VolumeEntryKind.Modified) => new(path, kind);

    [Test]
    public async Task LockOnlyBatch_EmitsNothing()
    {
        var service = CreateService(new VolumeChangeBatch("C", [
            Entry(@"C:\repo\a\.git\index.lock", VolumeEntryKind.Created),
            Entry(@"C:\repo\a\.git\index.lock", VolumeEntryKind.Deleted)
        ]));

        var events = await DrainAsync(service);

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task LockBatchWithRealChangeInOtherRepository_EmitsOnlyThatRepository()
    {
        var service = CreateService(new VolumeChangeBatch("C", [
            Entry(@"C:\repo\a\.git\index.lock", VolumeEntryKind.Created),
            Entry(@"C:\repo\b\src\file.cs"),
            Entry(@"C:\repo\a\.git\index.lock", VolumeEntryKind.Deleted)
        ]));

        var events = await DrainAsync(service);

        Assert.That(events, Is.EqualTo([new RepositoryChangeEvent(@"C:\repo\b", RepositoryChangeKind.Changed)]));
    }

    [Test]
    public async Task LockThenIndexRewrite_EmitsChanged()
    {
        var service = CreateService(new VolumeChangeBatch("C", [
            Entry(@"C:\repo\a\.git\index.lock", VolumeEntryKind.Created),
            Entry(@"C:\repo\a\.git\index.lock", VolumeEntryKind.Deleted),
            Entry(@"C:\repo\a\.git\index", VolumeEntryKind.Created)
        ]));

        var events = await DrainAsync(service);

        Assert.That(events, Is.EqualTo([new RepositoryChangeEvent(@"C:\repo\a", RepositoryChangeKind.Changed)]));
    }

    [Test]
    public async Task VerboseMode_LogsFirstTriggeringPathOnce()
    {
        var messages = new ConcurrentQueue<string>();
        var previousMethod = GitWizardLog.LogMethod;
        var previousVerbose = GitWizardLog.VerboseMode;
        var previousSilent = GitWizardLog.SilentMode;
        try
        {
            GitWizardLog.LogMethod = message => messages.Enqueue(message ?? string.Empty);
            GitWizardLog.VerboseMode = true;
            GitWizardLog.SilentMode = false;
            var service = CreateService(new VolumeChangeBatch("C", [
                Entry(@"C:\repo\a\.git\index.lock"),
                Entry(@"C:\repo\a\first.txt"),
                Entry(@"C:\repo\a\second.txt")
            ]));

            await DrainAsync(service);
        }
        finally
        {
            GitWizardLog.LogMethod = previousMethod;
            GitWizardLog.VerboseMode = previousVerbose;
            GitWizardLog.SilentMode = previousSilent;
        }

        var triggerLines = messages.Where(message => message.Contains("first triggering path", StringComparison.Ordinal)).ToArray();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(triggerLines, Has.Length.EqualTo(1));
            Assert.That(triggerLines[0], Does.Contain(@"C:\repo\a"));
            Assert.That(triggerLines[0], Does.Contain(@"C:\repo\a\first.txt"));
            Assert.That(triggerLines[0], Does.Not.Contain("second.txt"));
        }
    }
}

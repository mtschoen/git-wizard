using System.Collections.Concurrent;
using GitWizard;
using GitWizardTests.TestSupport;
using MFTLib.Index;
using MFTLibTestExtensions;

namespace GitWizardTests;

[TestFixture]
public class MftIndexDiagnosticsTests
{
    [Test]
    public async Task ReportDoesNotWaitForSink()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var messages = new ConcurrentQueue<string>();
        var relay = new MftIndexDiagnostics(message =>
        {
            entered.TrySetResult();
            // The sink deliberately blocks its worker until the test releases it.
            release.Task.Wait();
            messages.Enqueue(message);
        });
        try
        {
            relay.Report("first");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // Hang guard only: Report must return while the sink is still blocked on the first record.
            await Task.Run(() => relay.Report("second")).WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            release.TrySetResult();
            await relay.DisposeAsync();
        }
        Assert.That(messages.ToArray(), Is.EqualTo(["first", "second"]));
    }

    [Test]
    public void ThrowingSinkFaultsDisposal()
    {
        var relay = new MftIndexDiagnostics(_ => throw new IOException("sink failed"));
        relay.Report("record");
        Assert.ThrowsAsync<IOException>(async () => await relay.DisposeAsync());
    }

    [Test]
    public async Task LibraryLifecycleDeletionReachesFakeSink()
    {
        using var files = new PackedIndexFixture();
        var cache = MftIndexCachePolicy.GetCacheDirectory(files.DirectoryPath);
        Directory.CreateDirectory(cache);
        var retired = SyntheticBlock.CachedPath(cache, new VolumeKey('X'), 1) + ".retired-test";
        await File.WriteAllTextAsync(retired, "retired sentinel");
        var messages = new ConcurrentQueue<string>();
        var relay = new MftIndexDiagnostics(messages.Enqueue);
        await using (await FileIndex.OpenAsync(MFTLibTestExtensions.SyntheticVolumeFacts.Apply(new FileIndexOptions
        {
            Root = (RootKey)new VolumeKey('X'),
            CacheDirectory = cache,
            CacheTag = MftIndexCachePolicy.CacheTag,
            MftSource = SyntheticIndexSource.Create(files.Producer),
            Diagnostics = relay.Report
        }, 1, volume: new VolumeKey('X'), volumeRootDirectory: files.TreePath), CancellationToken.None))
        {
            Assert.That(File.Exists(retired), Is.False);
        }

        await relay.DisposeAsync();
        Assert.That(messages.Any(message => message.Contains(retired, StringComparison.Ordinal) &&
            message.Contains("sweeping a stale", StringComparison.Ordinal)), Is.True);
    }

    [Test, NonParallelizable]
    public async Task SessionRoutesLifecycleToGitWizardLog()
    {
        await using var broker = new BlockBrokerFixture();
        var cache = MftIndexCachePolicy.GetCacheDirectory(broker.CacheDirectory);
        Directory.CreateDirectory(cache);
        var retired = SyntheticBlock.CachedPath(cache, new VolumeKey('X'), 1) + ".retired-test";
        await File.WriteAllTextAsync(retired, "retired sentinel");
        var messages = new ConcurrentQueue<string>();
        var previousMethod = GitWizardLog.LogMethod;
        var previousSilent = GitWizardLog.SilentMode;
        // Logging also writes a file under the local-files home; keep that file in an owned home and
        // close it afterwards so no later child process finds the shared home's log held open.
        using var sandbox = new LocalFilesSandbox();
        try
        {
            GitWizardLog.LogMethod = message => messages.Enqueue(message ?? string.Empty);
            GitWizardLog.SilentMode = false;
            var session = await MftIndexSession.OpenAsync([broker.Drive('X')], broker.CreateSession(),
                cache, null, CancellationToken.None, configureOptions: broker.ConfigureOptions);
            await session.DisposeAsync();
            Assert.That(messages.Any(message => message.Contains(retired, StringComparison.Ordinal) &&
                message.Contains("Info:", StringComparison.Ordinal)), Is.True);
        }
        finally
        {
            GitWizardLog.LogMethod = previousMethod;
            GitWizardLog.SilentMode = previousSilent;
            GitWizardLog.CloseCurrentLogFile();
        }
    }
}

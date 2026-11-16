using System.Collections.Concurrent;
using GitWizard;
using GitWizardTests.TestSupport;

namespace GitWizardTests;

/// <summary>
/// The log writer must not capture the caller's synchronization context (git-wizard#605): a
/// context that is never pumped, such as a UI context at shutdown, would otherwise strand the
/// writer's continuations and block the draining close forever. NonParallelizable because the
/// file sink is global static state.
/// </summary>
[NonParallelizable]
// Log writer context tests exercise starting and draining the writer under an unpumped context.
public class GitWizardLogSynchronizationContextTests
{
    Action<string?> _originalLogMethod = Console.WriteLine;
    bool _originalSilentMode;

    [SetUp]
    public void SetUp()
    {
        _originalLogMethod = GitWizardLog.LogMethod;
        _originalSilentMode = GitWizardLog.SilentMode;
        GitWizardLog.CloseCurrentLogFile();
        GitWizardLog.SilentMode = false;
        GitWizardLog.LogMethod = _ => { };
    }

    [TearDown]
    public void TearDown()
    {
        GitWizardLog.CloseCurrentLogFile();
        GitWizardLog.SilentMode = _originalSilentMode;
        GitWizardLog.LogMethod = _originalLogMethod;
    }

    [Test]
    public async Task CloseCurrentLogFile_WithUnpumpedCallerContext_ReturnsAndPostsNothing()
    {
        var marker = $"unpumped-context-{Guid.NewGuid():N}";
        var context = new QueueingSynchronizationContext();
        Exception? failure = null;

        // The helper thread plays the caller whose context is never pumped. The first close
        // restarts the writer under that context; the second drains the writer started there.
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                GitWizardLog.CloseCurrentLogFile();
                GitWizardLog.Log(marker);
                GitWizardLog.CloseCurrentLogFile();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(null);
            }
        })
        {
            IsBackground = true
        };
        thread.Start();

        var finished = thread.Join(TestTimeouts.HangGuard);
        if (!finished)
        {
            // Release the stranded writer, and run later posts inline so the fixture cleanup
            // cannot hang either, then fail.
            context.RunInlineFromNowOn();
            thread.Join(TestTimeouts.HangGuard);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(finished, Is.True,
                "CloseCurrentLogFile did not return: the writer continued on the caller's context.");
            Assert.That(failure, Is.Null);
            Assert.That(context.PostedCount, Is.Zero, "The writer must not post to the caller's context.");
        }

        var files = Directory.GetFiles(GitWizardApi.GetLogFolderPath(), "GitWizardLog_*.log");
        var contents = string.Join(Environment.NewLine,
            await Task.WhenAll(files.Select(path => File.ReadAllTextAsync(path))));
        Assert.That(contents, Does.Contain(marker));
    }

    /// <summary>A context that queues every posted callback and never runs it on its own.</summary>
    sealed class QueueingSynchronizationContext : SynchronizationContext
    {
        readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();
        int _postedCount;
        volatile bool _runInline;

        public int PostedCount => Volatile.Read(ref _postedCount);

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _postedCount);
            if (_runInline)
            {
                d(state);
                return;
            }

            _queue.Enqueue((d, state));
        }

        public void RunInlineFromNowOn()
        {
            _runInline = true;
            while (_queue.TryDequeue(out var item))
            {
                item.Callback(item.State);
            }
        }
    }
}

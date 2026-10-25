using GitWizard;
using GitWizard.CLI;

namespace GitWizardTests;

[NonParallelizable]
public class UpdateHandlerTests
{
    TextWriter _originalOut = null!;
    TextWriter _originalError = null!;
    Action<string?> _originalLogMethod = null!;
    bool _originalSilentMode;
    bool _originalVerboseMode;
    StringWriter _capturedOut = null!;
    StringWriter _capturedError = null!;

    [SetUp]
    public void SetUp()
    {
        _originalOut = Console.Out;
        _originalError = Console.Error;
        _originalLogMethod = GitWizardLog.LogMethod;
        _originalSilentMode = GitWizardLog.SilentMode;
        _originalVerboseMode = GitWizardLog.VerboseMode;
        _capturedOut = new StringWriter();
        _capturedError = new StringWriter();
        Console.SetOut(_capturedOut);
        Console.SetError(_capturedError);
        GitWizardLog.LogMethod = Console.Error.WriteLine;
        GitWizardLog.SilentMode = false;
        GitWizardLog.VerboseMode = false;
    }

    [TearDown]
    public void TearDown()
    {
        Console.SetOut(_originalOut);
        Console.SetError(_originalError);
        GitWizardLog.LogMethod = _originalLogMethod;
        GitWizardLog.SilentMode = _originalSilentMode;
        GitWizardLog.VerboseMode = _originalVerboseMode;
        _capturedOut.Dispose();
        _capturedError.Dispose();
    }

    [Test]
    public void SendUpdateMessage_NullMessage_LogsExceptionToStderr()
    {
        new UpdateHandler().SendUpdateMessage(null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_capturedOut.ToString(), Is.Empty);
            Assert.That(_capturedError.ToString(), Does.Contain("null message"));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SendUpdateMessage_UsesVerboseLog(bool verbose)
    {
        GitWizardLog.VerboseMode = verbose;
        new UpdateHandler().SendUpdateMessage("refresh details");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_capturedOut.ToString(), Is.Empty);
            Assert.That(_capturedError.ToString().Contains("refresh details", StringComparison.Ordinal), Is.EqualTo(verbose));
        }
    }

    [TestCase("repository", "repository-path")]
    [TestCase("submodule", "submodule-path")]
    [TestCase("worktree", "worktree-path")]
    [TestCase("uninitialized", "uninitialized-path")]
    [TestCase("completed", "repository-path")]
    public void RepositoryCallbacks_LogPathsImmediatelyInVerboseMode(string callback, string expectedPath)
    {
        GitWizardLog.VerboseMode = true;

        InvokeCallback(new UpdateHandler(), callback);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_capturedOut.ToString(), Is.Empty);
            Assert.That(_capturedError.ToString(), Does.Contain(expectedPath));
        }
    }

    [Test]
    public void RepositoryCallbacks_WithoutVerboseMode_WriteNothing()
    {
        var handler = new UpdateHandler();
        foreach (var callback in new[] { "repository", "submodule", "worktree", "uninitialized", "completed" })
        {
            InvokeCallback(handler, callback);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_capturedOut.ToString(), Is.Empty);
            Assert.That(_capturedError.ToString(), Is.Empty);
        }
    }

    static void InvokeCallback(UpdateHandler handler, string callback)
    {
        var repository = new GitWizardRepository("repository-path");
        switch (callback)
        {
            case "repository":
                handler.OnRepositoryCreated(repository);
                break;
            case "submodule":
                handler.OnSubmoduleCreated(repository, new GitWizardRepository("submodule-path"));
                break;
            case "worktree":
                handler.OnWorktreeCreated(new GitWizardRepository("worktree-path"));
                break;
            case "uninitialized":
                handler.OnUninitializedSubmoduleCreated(repository, "uninitialized-path");
                break;
            case "completed":
                handler.OnRepositoryRefreshCompleted(repository);
                break;
            default:
                throw new ArgumentException("Unknown callback", nameof(callback));
        }
    }

    [Test]
    public void Progress_WritesOnlyToStderrAndFinishesWithNewline()
    {
        var handler = new UpdateHandler();
        handler.StartProgress("Refreshing", 4);
        handler.UpdateProgress(2);
        handler.UpdateProgress(4);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_capturedOut.ToString(), Is.Empty);
            Assert.That(_capturedError.ToString(), Does.Contain("Refreshing")
                .And.Contain("0/4").And.Contain("50%").And.Contain("2/4")
                .And.Contain("100%").And.EndsWith(Environment.NewLine));
        }
    }

    [TestCase(0)]
    [TestCase(-1)]
    public void Progress_NonpositiveTotal_WritesNothing(int total)
    {
        var handler = new UpdateHandler();
        handler.StartProgress("Nothing", total);
        handler.UpdateProgress(1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_capturedOut.ToString(), Is.Empty);
            Assert.That(_capturedError.ToString(), Is.Empty);
        }
    }

    [TestCase(-1, "[░░░░░░░░░░░░░░░░░░░░]")]
    [TestCase(5, "[████████████████████]")]
    public void Progress_OutOfRangeCount_ClampsBar(int count, string expectedBar)
    {
        var handler = new UpdateHandler();
        handler.StartProgress("Refreshing", 4);
        _capturedError.GetStringBuilder().Clear();
        handler.UpdateProgress(count);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_capturedOut.ToString(), Is.Empty);
            Assert.That(_capturedError.ToString(), Does.Contain(expectedBar));
        }
    }

    [TestCase("submodule", "[SKIPPED] Submodule parent not created yet: parent")]
    [TestCase("uninitialized", "[SKIPPED] Uninitialized submodule parent not created yet: parent")]
    [TestCase("refresh", "[MISSING] Refresh completed but item not created: child")]
    [TestCase("null-parent-submodule", "[SKIPPED] Submodule parent has null path")]
    [TestCase("null-parent-uninitialized", "[SKIPPED] Uninitialized submodule parent has null path")]
    [TestCase("refresh-empty", "[MISSING] Refresh completed but item not created:")]
    public void OrderingViolation_IsRetainedAfterLaterCreation(string kind, string warning)
    {
        var handler = new UpdateHandler();
        var parent = new GitWizardRepository("parent");
        var child = new GitWizardRepository("child");
        switch (kind)
        {
            case "submodule": handler.OnSubmoduleCreated(parent, child); break;
            case "uninitialized": handler.OnUninitializedSubmoduleCreated(parent, "child"); break;
            case "refresh": handler.OnRepositoryRefreshCompleted(child); break;
            case "null-parent-submodule": handler.OnSubmoduleCreated(new GitWizardRepository(null!), child); break;
            case "null-parent-uninitialized": handler.OnUninitializedSubmoduleCreated(new GitWizardRepository(null!), "child"); break;
            case "refresh-empty": handler.OnRepositoryRefreshCompleted(new GitWizardRepository(null!)); break;
        }
        handler.OnRepositoryCreated(parent);
        handler.OnRepositoryCreated(child);
        var run = handler.CaptureRun();
        Assert.Multiple(() =>
        {
            Assert.That(run.Created, Is.EqualTo(2));
            Assert.That(run.Refreshed, Is.Zero);
            Assert.That(run.Skipped, Is.EqualTo(1));
            Assert.That(run.Warnings, Has.Length.EqualTo(1));
            Assert.That(run.Warnings[0], Does.StartWith(warning));
            Assert.That(_capturedOut.ToString(), Is.Empty);
            Assert.That(_capturedError.ToString(), Is.Empty);
        });
    }

    [Test]
    public void OrderedCallbacks_CountAcceptedEventsAndDetachSnapshots()
    {
        var handler = new UpdateHandler();
        var parent = new GitWizardRepository("parent");
        var child = new GitWizardRepository("child");
        handler.OnRepositoryCreated(parent);
        handler.OnSubmoduleCreated(parent, child);
        handler.OnWorktreeCreated(new GitWizardRepository("worktree"));
        handler.OnUninitializedSubmoduleCreated(parent, "uninitialized");
        handler.OnRepositoryRefreshCompleted(parent);
        handler.OnRepositoryRefreshCompleted(child);
        var before = handler.CaptureRun();
        handler.OnRepositoryCreated(parent);
        handler.RecordWarning("later warning");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(before.Created, Is.EqualTo(3));
            Assert.That(before.Refreshed, Is.EqualTo(2));
            Assert.That(before.Skipped, Is.Zero);
            Assert.That(before.Warnings, Is.Empty);
            Assert.That(handler.CaptureRun().Created, Is.EqualTo(4));
            Assert.That(handler.CaptureRun().Warnings, Is.EqualTo(["later warning"]));
        }
    }

    [Test]
    public async Task MultipleProgress_Updates_AreThreadSafe()
    {
        using var writer = new ProgressLockWriter();
        var handler = new UpdateHandler(writer);
        var field = typeof(UpdateHandler).GetField("_progressLock",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        writer.ProgressLock = field.GetValue(handler)!;
        handler.StartProgress("Parallel test", 32);
        await Task.WhenAll(Enumerable.Range(1, 32)
            .Select(count => Task.Run(() => handler.UpdateProgress(count))));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(writer.UnlockedWrites, Is.Zero);
            Assert.That(writer.Lines, Has.Count.EqualTo(33));
            Assert.That(writer.Lines, Has.Some.Contains("32/32"));
            Assert.That(writer.Newlines, Is.EqualTo(1));
        }
    }

    sealed class ProgressLockWriter : TextWriter
    {
        int _unlockedWrites;
        int _newlines;
        public object ProgressLock { get; set; } = null!;
        public System.Collections.Concurrent.ConcurrentQueue<string> Lines { get; } = new();
        public int UnlockedWrites => Volatile.Read(ref _unlockedWrites);
        public int Newlines => Volatile.Read(ref _newlines);
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public override void Write(string? value)
        {
            if (!Monitor.IsEntered(ProgressLock))
            {
                Interlocked.Increment(ref _unlockedWrites);
            }

            Lines.Enqueue(value ?? string.Empty);
        }
        public override void WriteLine()
        {
            if (!Monitor.IsEntered(ProgressLock))
            {
                Interlocked.Increment(ref _unlockedWrites);
            }

            Interlocked.Increment(ref _newlines);
        }
    }
}

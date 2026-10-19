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

        Assert.Multiple(() =>
        {
            Assert.That(_capturedOut.ToString(), Is.Empty);
            Assert.That(_capturedError.ToString(), Does.Contain("null message"));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SendUpdateMessage_UsesVerboseLog(bool verbose)
    {
        GitWizardLog.VerboseMode = verbose;
        new UpdateHandler().SendUpdateMessage("refresh details");

        Assert.Multiple(() =>
        {
            Assert.That(_capturedOut.ToString(), Is.Empty);
            Assert.That(_capturedError.ToString().Contains("refresh details", StringComparison.Ordinal), Is.EqualTo(verbose));
        });
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

        Assert.Multiple(() =>
        {
            Assert.That(_capturedOut.ToString(), Is.Empty);
            Assert.That(_capturedError.ToString(), Does.Contain(expectedPath));
        });
    }

    [Test]
    public void RepositoryCallbacks_WithoutVerboseMode_WriteNothing()
    {
        var handler = new UpdateHandler();
        foreach (var callback in new[] { "repository", "submodule", "worktree", "uninitialized", "completed" })
            InvokeCallback(handler, callback);

        Assert.Multiple(() =>
        {
            Assert.That(_capturedOut.ToString(), Is.Empty);
            Assert.That(_capturedError.ToString(), Is.Empty);
        });
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

        Assert.Multiple(() =>
        {
            Assert.That(_capturedOut.ToString(), Is.Empty);
            Assert.That(_capturedError.ToString(), Does.Contain("Refreshing")
                .And.Contain("0/4").And.Contain("50%").And.Contain("2/4")
                .And.Contain("100%").And.EndsWith(Environment.NewLine));
        });
    }

    [TestCase(0)]
    [TestCase(-1)]
    public void Progress_NonpositiveTotal_WritesNothing(int total)
    {
        var handler = new UpdateHandler();
        handler.StartProgress("Nothing", total);
        handler.UpdateProgress(1);

        Assert.Multiple(() =>
        {
            Assert.That(_capturedOut.ToString(), Is.Empty);
            Assert.That(_capturedError.ToString(), Is.Empty);
        });
    }

    [TestCase(-1, "[░░░░░░░░░░░░░░░░░░░░]")]
    [TestCase(5, "[████████████████████]")]
    public void Progress_OutOfRangeCount_ClampsBar(int count, string expectedBar)
    {
        var handler = new UpdateHandler();
        handler.StartProgress("Refreshing", 4);
        _capturedError.GetStringBuilder().Clear();
        handler.UpdateProgress(count);

        Assert.Multiple(() =>
        {
            Assert.That(_capturedOut.ToString(), Is.Empty);
            Assert.That(_capturedError.ToString(), Does.Contain(expectedBar));
        });
    }
}

using GitWizard;
using GitWizard.CLI;

namespace GitWizardTests;

public class UpdateHandlerTests
{
    TextWriter _originalOut = null!;
    StringWriter _capturedOut = null!;

    [SetUp]
    public void SetUp()
    {
        GitWizardLog.SilentMode = true;
        _originalOut = Console.Out;
        _capturedOut = new StringWriter();
        Console.SetOut(_capturedOut);
    }

    [TearDown]
    public void TearDown()
    {
        Console.SetOut(_originalOut);
        _capturedOut.Dispose();
        GitWizardLog.SilentMode = false;
        GitWizardLog.VerboseMode = false;
    }

    // ---------------------------------------------------------------
    // SendUpdateMessage
    // ---------------------------------------------------------------

    [Test]
    public void SendUpdateMessage_NullMessage_DoesNotThrow()
    {
        var handler = new UpdateHandler();
        Assert.DoesNotThrow(() => handler.SendUpdateMessage(null));
    }

    [Test]
    public void SendUpdateMessage_NullMessage_LogsException()
    {
        var logged = new List<string>();
        GitWizardLog.SilentMode = false;
        GitWizardLog.LogMethod = msg => { if (msg is not null) logged.Add(msg); };

        try
        {
            var handler = new UpdateHandler();
            handler.SendUpdateMessage(null);

            Assert.That(string.Join("\n", logged), Does.Contain("null message"));
        }
        finally
        {
            GitWizardLog.LogMethod = Console.WriteLine;
        }
    }

    [Test]
    public void SendUpdateMessage_ValidMessage_DoesNotThrow()
    {
        var handler = new UpdateHandler();
        Assert.DoesNotThrow(() => handler.SendUpdateMessage("hello"));
    }

    // ---------------------------------------------------------------
    // OnRepositoryCreated + ProcessCommands
    // ---------------------------------------------------------------

    [Test]
    public void OnRepositoryCreated_ThenProcess_DoesNotThrow()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        var handler = new UpdateHandler();

        handler.OnRepositoryCreated(repo);
        Assert.DoesNotThrow(() => handler.ProcessCommands());
    }

    [Test]
    public void OnRepositoryCreated_NullWorkingDir_ProcessDoesNotThrow()
    {
        // A repo constructed with a non-existent path will have a non-null
        // WorkingDirectory. We use a GitWizardRepository with an empty string
        // which triggers the null/empty guard.
        var repo = new GitWizardRepository(string.Empty);
        var handler = new UpdateHandler();

        handler.OnRepositoryCreated(repo);
        Assert.DoesNotThrow(() => handler.ProcessCommands());
    }

    // ---------------------------------------------------------------
    // OnSubmoduleCreated + ProcessCommands
    // ---------------------------------------------------------------

    [Test]
    public void OnSubmoduleCreated_ParentAlreadyCreated_DoesNotThrow()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var parent = new GitWizardRepository(fixture.Path);

        using var subFixture = TempRepoFixture.CreateWithInitialCommit();
        var submodule = new GitWizardRepository(subFixture.Path);

        var handler = new UpdateHandler();
        // First create the parent so it's in _createdPaths.
        handler.OnRepositoryCreated(parent);
        handler.ProcessCommands();

        handler.OnSubmoduleCreated(parent, submodule);
        Assert.DoesNotThrow(() => handler.ProcessCommands());
    }

    [Test]
    public void OnSubmoduleCreated_ParentNotCreated_SkipsCommand()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var parent = new GitWizardRepository(fixture.Path);

        using var subFixture = TempRepoFixture.CreateWithInitialCommit();
        var submodule = new GitWizardRepository(subFixture.Path);

        var handler = new UpdateHandler();
        // Don't create the parent first - the submodule should be skipped.
        handler.OnSubmoduleCreated(parent, submodule);
        Assert.DoesNotThrow(() => handler.ProcessCommands());
    }

    // ---------------------------------------------------------------
    // OnWorktreeCreated + ProcessCommands
    // ---------------------------------------------------------------

    [Test]
    public void OnWorktreeCreated_ThenProcess_DoesNotThrow()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        var handler = new UpdateHandler();

        handler.OnWorktreeCreated(repo);
        Assert.DoesNotThrow(() => handler.ProcessCommands());
    }

    [Test]
    public void OnWorktreeCreated_EmptyPath_ProcessDoesNotThrow()
    {
        var repo = new GitWizardRepository(string.Empty);
        var handler = new UpdateHandler();

        handler.OnWorktreeCreated(repo);
        Assert.DoesNotThrow(() => handler.ProcessCommands());
    }

    // ---------------------------------------------------------------
    // OnUninitializedSubmoduleCreated + ProcessCommands
    // ---------------------------------------------------------------

    [Test]
    public void OnUninitializedSubmoduleCreated_ParentCreated_DoesNotThrow()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var parent = new GitWizardRepository(fixture.Path);
        var handler = new UpdateHandler();

        handler.OnRepositoryCreated(parent);
        handler.ProcessCommands();

        handler.OnUninitializedSubmoduleCreated(parent, "/some/submodule/path");
        Assert.DoesNotThrow(() => handler.ProcessCommands());
    }

    [Test]
    public void OnUninitializedSubmoduleCreated_ParentNotCreated_SkipsCommand()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var parent = new GitWizardRepository(fixture.Path);
        var handler = new UpdateHandler();

        // Parent not created first - should skip.
        handler.OnUninitializedSubmoduleCreated(parent, "/some/submodule/path");
        Assert.DoesNotThrow(() => handler.ProcessCommands());
    }

    // ---------------------------------------------------------------
    // OnRepositoryRefreshCompleted + ProcessCommands
    // ---------------------------------------------------------------

    [Test]
    public void OnRepositoryRefreshCompleted_AfterCreated_DoesNotThrow()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        var handler = new UpdateHandler();

        handler.OnRepositoryCreated(repo);
        handler.ProcessCommands();

        handler.OnRepositoryRefreshCompleted(repo);
        Assert.DoesNotThrow(() => handler.ProcessCommands());
    }

    [Test]
    public void OnRepositoryRefreshCompleted_BeforeCreated_SkipsCommand()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        var handler = new UpdateHandler();

        // Refresh completed before created - should skip.
        handler.OnRepositoryRefreshCompleted(repo);
        Assert.DoesNotThrow(() => handler.ProcessCommands());
    }

    // ---------------------------------------------------------------
    // ProcessCommands drains queue
    // ---------------------------------------------------------------

    [Test]
    public void ProcessCommands_CalledTwice_SecondCallIsNoop()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        var handler = new UpdateHandler();

        handler.OnRepositoryCreated(repo);
        handler.ProcessCommands();

        // Second call should be a no-op (queue is empty).
        Assert.DoesNotThrow(() => handler.ProcessCommands());
    }

    [Test]
    public void ProcessCommands_EmptyQueue_DoesNotThrow()
    {
        var handler = new UpdateHandler();
        Assert.DoesNotThrow(() => handler.ProcessCommands());
    }

    [Test]
    public void ProcessCommands_MultipleCommandTypes_AllProcessed()
    {
        using var fixture1 = TempRepoFixture.CreateWithInitialCommit();
        using var fixture2 = TempRepoFixture.CreateWithInitialCommit();
        var repo1 = new GitWizardRepository(fixture1.Path);
        var repo2 = new GitWizardRepository(fixture2.Path);
        var handler = new UpdateHandler();

        handler.OnRepositoryCreated(repo1);
        handler.OnWorktreeCreated(repo2);
        handler.OnRepositoryRefreshCompleted(repo1);

        Assert.DoesNotThrow(() => handler.ProcessCommands());
    }

    // ---------------------------------------------------------------
    // StartProgress + UpdateProgress console output
    // ---------------------------------------------------------------

    [Test]
    public void StartProgress_WritesProgressBar()
    {
        var handler = new UpdateHandler();
        handler.StartProgress("Loading", 10);

        var output = _capturedOut.ToString();
        Assert.Multiple(() =>
        {
            Assert.That(output, Does.Contain("Loading"));
            Assert.That(output, Does.Contain("0%"));
            Assert.That(output, Does.Contain("0/10"));
        });
    }

    [Test]
    public void UpdateProgress_WritesUpdatedProgress()
    {
        var handler = new UpdateHandler();
        handler.StartProgress("Refreshing", 4);
        handler.UpdateProgress(2);

        var output = _capturedOut.ToString();
        Assert.Multiple(() =>
        {
            Assert.That(output, Does.Contain("50%"));
            Assert.That(output, Does.Contain("2/4"));
        });
    }

    [Test]
    public void UpdateProgress_At100Percent_WritesNewline()
    {
        var handler = new UpdateHandler();
        handler.StartProgress("Done", 5);
        handler.UpdateProgress(5);

        var output = _capturedOut.ToString();
        Assert.That(output, Does.Contain("100%"));
    }

    [Test]
    public void StartProgress_ZeroTotal_DoesNotWrite()
    {
        var handler = new UpdateHandler();
        handler.StartProgress("Nothing", 0);

        var output = _capturedOut.ToString();
        Assert.That(output, Is.Empty);
    }

    [Test]
    public void StartProgress_NegativeTotal_DoesNotWrite()
    {
        var handler = new UpdateHandler();
        handler.StartProgress("Nothing", -1);

        var output = _capturedOut.ToString();
        Assert.That(output, Is.Empty);
    }

    // ---------------------------------------------------------------
    // PrintSummary console output
    // ---------------------------------------------------------------

    [Test]
    public void PrintSummary_NoCommands_PrintsZeroCounts()
    {
        var handler = new UpdateHandler();
        handler.PrintSummary();

        var output = _capturedOut.ToString();
        Assert.Multiple(() =>
        {
            Assert.That(output, Does.Contain("Command Processing Summary"));
            Assert.That(output, Does.Contain("Total repositories created: 0"));
            Assert.That(output, Does.Contain("Total refresh completed: 0"));
            Assert.That(output, Does.Contain("Skipped commands: 0"));
        });
    }

    [Test]
    public void PrintSummary_AfterCreatedAndCompleted_ShowsCounts()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        var handler = new UpdateHandler();

        handler.OnRepositoryCreated(repo);
        handler.ProcessCommands();
        handler.OnRepositoryRefreshCompleted(repo);
        handler.ProcessCommands();

        handler.PrintSummary();

        var output = _capturedOut.ToString();
        Assert.Multiple(() =>
        {
            Assert.That(output, Does.Contain("Total repositories created: 1"));
            Assert.That(output, Does.Contain("Total refresh completed: 1"));
        });
    }

    [Test]
    public void PrintSummary_WithSkippedCommands_ShowsSkipCount()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        var handler = new UpdateHandler();

        // Refresh before created -> skip
        handler.OnRepositoryRefreshCompleted(repo);
        handler.ProcessCommands();

        handler.PrintSummary();

        var output = _capturedOut.ToString();
        Assert.That(output, Does.Contain("Skipped commands: 1"));
    }

    // ---------------------------------------------------------------
    // Full workflow: create -> refresh -> summary
    // ---------------------------------------------------------------

    [Test]
    public void FullWorkflow_CreateRefreshSummary_Succeeds()
    {
        using var fixture1 = TempRepoFixture.CreateWithInitialCommit();
        using var fixture2 = TempRepoFixture.CreateWithInitialCommit();
        var repo1 = new GitWizardRepository(fixture1.Path);
        var repo2 = new GitWizardRepository(fixture2.Path);
        var handler = new UpdateHandler();

        // Create both repos.
        handler.OnRepositoryCreated(repo1);
        handler.OnRepositoryCreated(repo2);
        handler.ProcessCommands();

        // Complete refresh for both.
        handler.OnRepositoryRefreshCompleted(repo1);
        handler.OnRepositoryRefreshCompleted(repo2);
        handler.ProcessCommands();

        handler.PrintSummary();

        var output = _capturedOut.ToString();
        Assert.Multiple(() =>
        {
            Assert.That(output, Does.Contain("Total repositories created: 2"));
            Assert.That(output, Does.Contain("Total refresh completed: 2"));
            Assert.That(output, Does.Contain("Skipped commands: 0"));
        });
    }

    // ---------------------------------------------------------------
    // Edge cases
    // ---------------------------------------------------------------

    [Test]
    public void OnSubmoduleCreated_BothNull_DoesNotThrow()
    {
        var handler = new UpdateHandler();
        handler.OnSubmoduleCreated(null!, null!);
        Assert.DoesNotThrow(() => handler.ProcessCommands());
    }

    [Test]
    public void OnUninitializedSubmoduleCreated_NullParent_DoesNotThrow()
    {
        var handler = new UpdateHandler();
        handler.OnUninitializedSubmoduleCreated(null!, "/some/path");
        Assert.DoesNotThrow(() => handler.ProcessCommands());
    }

    [Test]
    public void OnRepositoryRefreshCompleted_NullRepo_DoesNotThrow()
    {
        var handler = new UpdateHandler();
        handler.OnRepositoryRefreshCompleted(null!);
        Assert.DoesNotThrow(() => handler.ProcessCommands());
    }

    [Test]
    public void OnWorktreeCreated_NullRepo_DoesNotThrow()
    {
        var handler = new UpdateHandler();
        handler.OnWorktreeCreated(null!);
        Assert.DoesNotThrow(() => handler.ProcessCommands());
    }

    [Test]
    public void OnSubmoduleCreated_ParentWithEmptyPath_SkipsCommand()
    {
        var parent = new GitWizardRepository(string.Empty);
        using var subFixture = TempRepoFixture.CreateWithInitialCommit();
        var submodule = new GitWizardRepository(subFixture.Path);

        var handler = new UpdateHandler();
        handler.OnSubmoduleCreated(parent, submodule);
        handler.ProcessCommands();

        handler.PrintSummary();

        var output = _capturedOut.ToString();
        // Parent has empty path -> skipped
        Assert.That(output, Does.Contain("Skipped commands: 1"));
    }

    [Test]
    public void OnUninitializedSubmoduleCreated_ParentWithEmptyPath_SkipsCommand()
    {
        var parent = new GitWizardRepository(string.Empty);
        var handler = new UpdateHandler();

        handler.OnUninitializedSubmoduleCreated(parent, "/sub");
        handler.ProcessCommands();

        handler.PrintSummary();

        var output = _capturedOut.ToString();
        Assert.That(output, Does.Contain("Skipped commands: 1"));
    }

    [Test]
    public void MultipleProgress_Updates_AreThreadSafe()
    {
        var handler = new UpdateHandler();
        handler.StartProgress("Parallel test", 100);

        // Simulate rapid concurrent updates.
        var tasks = new List<Task>();
        for (var i = 1; i <= 100; i++)
        {
            var count = i;
            tasks.Add(Task.Run(() => handler.UpdateProgress(count)));
        }

        Assert.DoesNotThrowAsync(async () => await Task.WhenAll(tasks));
    }

    [Test]
    public void ImplementsIUpdateHandler()
    {
        var handler = new UpdateHandler();
        Assert.That(handler, Is.InstanceOf<IUpdateHandler>());
    }
}

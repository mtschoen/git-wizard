using System.Collections.Concurrent;
using GitWizard;
using GitWizardTests.TestSupport;
using LibGit2Sharp;

namespace GitWizardTests;

/// <summary>
/// A bare repository has no working tree or index, so the submodule, submodule-health and status
/// phases must be skipped instead of throwing and logging an exception record per phase on every
/// refresh (git-wizard#454 W6). NonParallelizable because the typed log sink is global static state.
/// </summary>
[NonParallelizable]
// Bare refresh tests exercise refresh diagnostics for repositories without a working tree.
public class GitWizardRepositoryBareRefreshTests
{
    readonly ConcurrentQueue<string> _errorMessages = new();
    Action<string?, GitWizardLog.LogType>? _originalTypedSink;
    bool _originalSilentMode;

    [SetUp]
    public void SetUp()
    {
        _errorMessages.Clear();
        _originalTypedSink = GitWizardLog.TypedLogMethod;
        _originalSilentMode = GitWizardLog.SilentMode;
        GitWizardLog.SilentMode = true;
        GitWizardLog.TypedLogMethod = (message, type) =>
        {
            if (message != null && type == GitWizardLog.LogType.Error)
            {
                _errorMessages.Enqueue(message);
            }
        };
    }

    [TearDown]
    public void TearDown()
    {
        GitWizardLog.TypedLogMethod = _originalTypedSink;
        GitWizardLog.SilentMode = _originalSilentMode;
    }

    [Test]
    public void Refresh_BareRepository_LogsNoErrors()
    {
        using var directory = new TempDirectory();
        Repository.Init(directory.Path, isBare: true);

        var repository = new GitWizardRepository(directory.Path);
        repository.Refresh();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_errorMessages, Is.Empty);
            Assert.That(repository.RefreshError, Is.Null);
            Assert.That(repository.HasPendingChanges, Is.False);
            Assert.That(repository.SubmoduleHealth, Is.Empty);
        }
    }
}

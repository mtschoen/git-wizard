using GitWizard;
using GitWizard.CLI;

namespace GitWizardTests;

public partial class ProgramHelperTests
{
    #region EnsureUsableSearchPaths tests

    [Test]
    public void EnsureUsableSearchPaths_AllInvalid_ExitsOneAndLogsSummary()
    {
        var config = new GitWizardConfiguration
        {
            SearchPaths = { "/nonexistent/path/one", "/nonexistent/path/two", "/nonexistent/path/three" }
        };

        var loggedMessages = new List<string>();
        GitWizardLog.LogMethod = msg => { if (msg != null) loggedMessages.Add(msg); };
        var exitCode = -1;

        try
        {
            var result = Program.EnsureUsableSearchPaths(config, code => exitCode = code);
            Assert.Multiple(() =>
            {
                Assert.That(result, Is.False);
                Assert.That(exitCode, Is.EqualTo(1));
                Assert.That(loggedMessages.Any(m => m.Contains("0 of 3 configured search paths are usable")), Is.True);
            });
        }
        finally
        {
            GitWizardLog.LogMethod = Console.WriteLine;
        }
    }

    [Test]
    public void EnsureUsableSearchPaths_EmptySearchPaths_ExitsOneAndLogsSummary()
    {
        var config = new GitWizardConfiguration();
        config.SearchPaths.Clear();

        var loggedMessages = new List<string>();
        GitWizardLog.LogMethod = msg => { if (msg != null) loggedMessages.Add(msg); };
        var exitCode = -1;

        try
        {
            var result = Program.EnsureUsableSearchPaths(config, code => exitCode = code);
            Assert.Multiple(() =>
            {
                Assert.That(result, Is.False);
                Assert.That(exitCode, Is.EqualTo(1));
                Assert.That(loggedMessages.Any(m => m.Contains("0 of 0 configured search paths are usable")), Is.True);
            });
        }
        finally
        {
            GitWizardLog.LogMethod = Console.WriteLine;
        }
    }

    [Test]
    public void EnsureUsableSearchPaths_WithUsablePath_ReturnsTrueWithoutExit()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var config = new GitWizardConfiguration
        {
            SearchPaths = { fixture.Path, "/nonexistent/path/two" }
        };

        var exitCalled = false;
        var result = Program.EnsureUsableSearchPaths(config, _ => exitCalled = true);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.True);
            Assert.That(exitCalled, Is.False);
        });
    }

    #endregion
}

using System.Reflection;
using GitWizard;
using GitWizardUI.ViewModels;
using GitWizardUI.ViewModels.Services;

namespace GitWizardTests;

[TestFixture]
public class CoverageSafetyBoostTests
{
    [SetUp]
    public void SetUp()
    {
        GitWizardLog.SilentMode = true;
    }

    [Test]
    public void FetchAllRemotes_ValidAndInvalid_ReturnsExpected()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();

        var method = typeof(GitWizardRepository).GetMethod("FetchAllRemotes",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        // 1. Success path (or at least valid repo path). Running git fetch --all in a local repo with no remotes exits 0.
        var resultValid = (bool)method.Invoke(null, new object[] { fixture.Path })!;
        Assert.That(resultValid, Is.True);

        // 2. Exception path (invalid directory)
        var resultInvalid = (bool)method.Invoke(null, new object[] { "/invalid-dir-that-does-not-exist" })!;
        Assert.That(resultInvalid, Is.False);
    }

    [Test]
    public void RefreshWorktrees_UnknownState_LogsError()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var worktreePath = fixture.AddWorktree("wt-corrupt");

        // Delete the worktree's files and recreate an empty directory so it is not a valid git repository
        if (Directory.Exists(worktreePath))
        {
            Directory.Delete(worktreePath, recursive: true);
            Directory.CreateDirectory(worktreePath);
        }

        var repo = new GitWizardRepository(fixture.Path);

        // Refreshing should execute RefreshWorktrees, hit the invalid worktree path,
        // and safely log it as an unknown state rather than crashing.
        Assert.DoesNotThrow(() => repo.Refresh());
    }

    [Test]
    public void AddToGroups_LiveUpdates_DriveGrouping()
    {
        var vm = new MainViewModel(new StubUiDispatcher(), new StubUserDialogs(), new StubClipboardService());
        vm.ToggleGroupMode(GroupMode.Drive);

        var repo1 = new GitWizardRepository(Path.Combine(Path.GetTempPath(), "drive1"));
        var repo2 = new GitWizardRepository(Path.Combine(Path.GetTempPath(), "drive2"));

        var addMethod = typeof(MainViewModel).GetMethod("AddRepository",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        // Add repository 1 (creates a new group header and inserts child)
        addMethod.Invoke(vm, new object[] { repo1 });
        Assert.That(vm.Repositories, Is.Not.Empty);

        var header = vm.Repositories[0];
        Assert.That(header.IsGroupHeader, Is.True);
        Assert.That(header.Children, Has.Count.EqualTo(1));

        // Add repository 2 (adds to the same group header 'C:\')
        addMethod.Invoke(vm, new object[] { repo2 });
        Assert.That(vm.Repositories, Has.Count.EqualTo(1));
        Assert.That(header.Children, Has.Count.EqualTo(2));
    }

    [Test]
    public void AddToGroups_LiveUpdates_RemoteUrlGroupingAndPromotion()
    {
        var vm = new MainViewModel(new StubUiDispatcher(), new StubUserDialogs(), new StubClipboardService());
        vm.ToggleGroupMode(GroupMode.RemoteUrl);

        var repo1 = new GitWizardRepository(Path.Combine(Path.GetTempPath(), "repo1"));
        repo1.RemoteUrls.Add("git@github.com:mtschoen/git-wizard.git");

        var repo2 = new GitWizardRepository(Path.Combine(Path.GetTempPath(), "repo2"));
        repo2.RemoteUrls.Add("https://github.com/mtschoen/git-wizard.git");

        var addMethod = typeof(MainViewModel).GetMethod("AddRepository",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        // Add first repo: since RemoteUrl grouping has minGroupSize = 2, it should go to pending groups and not be visible yet
        addMethod.Invoke(vm, new object[] { repo1 });
        Assert.That(vm.Repositories, Is.Empty);

        // Add second repo with the same normalized remote URL: should promote the group to visible Repositories
        addMethod.Invoke(vm, new object[] { repo2 });
        Assert.That(vm.Repositories, Has.Count.EqualTo(1)); // The group header
        Assert.That(vm.Repositories[0].IsGroupHeader, Is.True);
        Assert.That(vm.Repositories[0].Children, Has.Count.EqualTo(2));
    }

    [Test]
    public void MergeIntoFile_CorruptJsonFile_StartsFromEmptyReport()
    {
        var tempRoot = TestUtilities.RedirectLocalFilesToTemp();
        try
        {
            var savePath = Path.Combine(tempRoot, "corrupt.json");
            File.WriteAllText(savePath, "not json {{{");

            var config = new GitWizardConfiguration();
            var paths = new List<string>();

            // Should catch the parse exception, log it, and complete successfully starting from empty
            var result = GitWizardReport.MergeIntoFile(savePath, config, paths);
            Assert.That(result, Is.Not.Null);
            Assert.That(result.Repositories, Is.Empty);
        }
        finally
        {
            TestUtilities.ClearLocalFilesRedirect(tempRoot);
        }
    }

    [Test]
    public void WriteAtomic_ThrowsException_HandledGracefully()
    {
        var badPath = Path.Combine(Path.GetTempPath(), "bad_path_dir/");
        var report = new GitWizardReport();
        // Should catch the exception, log it, and not throw
        Assert.DoesNotThrow(() => report.SaveAtomic(badPath));
    }

    [Test]
    public void CleanLogFolder_DeletesOldFiles()
    {
        var tempRoot = TestUtilities.RedirectLocalFilesToTemp();
        try
        {
            var logFolder = GitWizardApi.GetLogFolderPath();
            Directory.CreateDirectory(logFolder);

            var oldFile = Path.Combine(logFolder, "old.log");
            File.WriteAllText(oldFile, "old content");
            File.SetCreationTimeUtc(oldFile, DateTime.UtcNow.AddDays(-31));

            var newFile = Path.Combine(logFolder, "new.log");
            File.WriteAllText(newFile, "new content");

            var method = typeof(GitWizardLog).GetMethod("CleanLogFolder",
                BindingFlags.NonPublic | BindingFlags.Static)!;

            method.Invoke(null, null);

            // Give the background thread up to 1 second to execute
            var start = DateTime.UtcNow;
            while (File.Exists(oldFile) && (DateTime.UtcNow - start).TotalSeconds < 1.0)
            {
                Thread.Sleep(50);
            }

            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(oldFile), Is.False);
                Assert.That(File.Exists(newFile), Is.True);
            });
        }
        finally
        {
            TestUtilities.ClearLocalFilesRedirect(tempRoot);
        }
    }

    [Test]
    public void LogException_WithInnerException_LogsRecursively()
    {
        var inner = new InvalidOperationException("inner error");
        var outer = new InvalidOperationException("outer error", inner);

        var loggedMessages = new List<string>();
        GitWizardLog.LogMethod = msg => { if (msg != null) loggedMessages.Add(msg); };
        GitWizardLog.SilentMode = false;
        try
        {
            GitWizardLog.LogException(outer);
            Assert.That(loggedMessages.Any(m => m.Contains("inner error")), Is.True);
            Assert.That(loggedMessages.Any(m => m.Contains("Inner exception:")), Is.True);
        }
        finally
        {
            GitWizardLog.LogMethod = Console.WriteLine;
            GitWizardLog.SilentMode = true;
        }
    }
}

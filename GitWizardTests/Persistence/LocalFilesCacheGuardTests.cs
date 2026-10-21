using GitWizard;
using GitWizardUI.ViewModels;

namespace GitWizardTests;

[TestFixture, NonParallelizable]
public class LocalFilesCacheGuardTests
{
    string? _tempHome;
    bool _previousSilent;
    bool _previousVerbose;

    [SetUp]
    public void SetUp()
    {
        _previousSilent = GitWizardLog.SilentMode;
        _previousVerbose = GitWizardLog.VerboseMode;
        GitWizardLog.SilentMode = true;
        TestUtilities.ResetStaticCaches();
        _tempHome = TestUtilities.RedirectLocalFilesToTemp();
    }

    [TearDown]
    public void TearDown()
    {
        TestUtilities.ResetStaticCaches();
        TestUtilities.ClearLocalFilesRedirect(_tempHome);
        GitWizardLog.SilentMode = _previousSilent;
        GitWizardLog.VerboseMode = _previousVerbose;
    }

    [TestCase(false)]
    [TestCase(true)]
    public void DeleteAllRefusesBeforeChangingAnyFiles(bool populated)
    {
        GitWizardApi.EnsureLocalFolderExists();
        var root = GitWizardApi.GetLocalFilesPath();
        var cache = MftIndexCachePolicy.GetCacheRoot(root);
        Directory.CreateDirectory(cache);
        var configuration = Path.Combine(root, "config.json");
        var list = GitWizardApi.GetCachedRepositoryListPath();
        File.WriteAllText(configuration, "configuration sentinel");
        File.WriteAllText(list, "repository sentinel");
        var block = Path.Combine(cache, "block-sentinel.mlix");
        if (populated)
            File.WriteAllText(block, "block sentinel");
        var before = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes);
        Assert.That(GitWizardApi.TryDeleteAllLocalFiles(out var refusal), Is.False);
        Assert.That(refusal, Does.Contain("mft-index"));
        Assert.That(Directory.Exists(cache), Is.True);
        Assert.That(Directory.GetFiles(root, "*", SearchOption.AllDirectories),
            Is.EquivalentTo(before.Keys));
        foreach (var pair in before)
            Assert.That(File.ReadAllBytes(pair.Key), Is.EqualTo(pair.Value));
    }

    [Test]
    public void ClearCacheLeavesPersistentBlocksAndConfiguration()
    {
        GitWizardApi.EnsureLocalFolderExists();
        var root = GitWizardApi.GetLocalFilesPath();
        var cache = MftIndexCachePolicy.GetCacheRoot(root);
        Directory.CreateDirectory(cache);
        var block = Path.Combine(cache, "sentinel.mlix");
        var configuration = Path.Combine(root, "config.json");
        File.WriteAllText(block, "block sentinel");
        File.WriteAllText(configuration, "configuration sentinel");
        File.WriteAllText(GitWizardApi.GetCachedRepositoryListPath(), "repository sentinel");
        File.WriteAllText(GitWizardReport.GetCachedReportPath(), "{}");
        GitWizardApi.ClearCache();
        Assert.That(File.ReadAllText(block), Is.EqualTo("block sentinel"));
        Assert.That(File.ReadAllText(configuration), Is.EqualTo("configuration sentinel"));
        Assert.That(File.Exists(GitWizardApi.GetCachedRepositoryListPath()), Is.False);
        Assert.That(File.Exists(GitWizardReport.GetCachedReportPath()), Is.False);
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public void ClearCache_RemovesExistingFiles_AndDoesNotThrowWhenAbsent(bool createRepoList, bool createReport)
    {
        GitWizardApi.EnsureLocalFolderExists();
        var repoListPath = GitWizardApi.GetCachedRepositoryListPath();
        var reportPath = GitWizardReport.GetCachedReportPath();
        if (createRepoList)
            File.WriteAllText(repoListPath, "path1\npath2");
        if (createReport)
            File.WriteAllText(reportPath, "{}");

        Assert.DoesNotThrow(() => GitWizardApi.ClearCache());

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(repoListPath), Is.False);
            Assert.That(File.Exists(reportPath), Is.False);
        });
    }

    [Test]
    public void DeleteAbsentRootSucceeds()
    {
        var root = GitWizardApi.GetLocalFilesPath();
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
        Assert.That(GitWizardApi.TryDeleteAllLocalFiles(out var refusal), Is.True);
        Assert.That(refusal, Is.Null);
    }

    [Test]
    public void DeleteOrdinaryNestedFilesSucceeds()
    {
        var root = GitWizardApi.GetLocalFilesPath();
        var nested = Path.Combine(root, "ordinary");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "sentinel.txt"), "ordinary file");
        File.WriteAllText(Path.Combine(root, "top-level.txt"), "ordinary file");
        Assert.That(GitWizardApi.TryDeleteAllLocalFiles(out var refusal), Is.True);
        Assert.That(refusal, Is.Null);
        Assert.That(Directory.Exists(root), Is.False);
    }

    [Test]
    public async Task ViewModelShowsRefusalInsteadOfSuccess()
    {
        var cache = MftIndexCachePolicy.GetCacheRoot(GitWizardApi.GetLocalFilesPath());
        Directory.CreateDirectory(cache);
        var sentinel = Path.Combine(cache, "sentinel.mlix");
        await File.WriteAllTextAsync(sentinel, "retained");
        var dialogs = new StubUserDialogs();
        await using var viewModel = new MainViewModel(new StubUiDispatcher(), dialogs, new StubClipboardService());
        await viewModel.DeleteAllLocalFilesAsync();
        Assert.That(dialogs.AlertCalls, Has.Count.EqualTo(1));
        Assert.That(dialogs.AlertCalls.Single().Title, Is.EqualTo("Files Not Deleted"));
        Assert.That(dialogs.AlertCalls.Single().Message, Does.Contain("mft-index"));
        Assert.That(await File.ReadAllTextAsync(sentinel), Is.EqualTo("retained"));
    }

    [Test]
    public async Task ViewModelReportsSuccessWithoutCache()
    {
        await File.WriteAllTextAsync(Path.Combine(GitWizardApi.GetLocalFilesPath(), "sentinel.txt"), "ordinary file");
        var dialogs = new StubUserDialogs();
        await using var viewModel = new MainViewModel(new StubUiDispatcher(), dialogs, new StubClipboardService());
        await viewModel.DeleteAllLocalFilesAsync();
        Assert.That(dialogs.AlertCalls.Single().Title, Is.EqualTo("Files Deleted"));
        Assert.That(Directory.Exists(GitWizardApi.GetLocalFilesPath()), Is.False);
    }
}

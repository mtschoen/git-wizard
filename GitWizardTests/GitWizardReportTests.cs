using GitWizard;

namespace GitWizardTests;

public class GitWizardReportTests
{
    [Test]
    public void Report_HasSchemaVersion()
    {
        var report = new GitWizardReport();
        Assert.That(report.SchemaVersion, Is.EqualTo("1.1"));
    }

    [Test]
    public void Report_SchemaVersionSerializesToJson()
    {
        var report = new GitWizardReport();
        var json = System.Text.Json.JsonSerializer.Serialize(report);
        Assert.That(json, Does.Contain("\"SchemaVersion\":\"1.1\""));
    }

    [Test]
    public void RefreshConcurrencyTest()
    {
        GitWizardLog.SilentMode = true;
        var configuration = GitWizardConfiguration.CreateDefaultConfiguration();
        var repositoryPaths = new SortedSet<string>();
        var report = new GitWizardReport(configuration);
        report.GetRepositoryPaths(repositoryPaths);
        Parallel.For(0, 10, _ => { report.Refresh(repositoryPaths); });
    }

    [Test]
    public void Refresh_DeletedRepo_TracksDeletedPath()
    {
        GitWizardLog.SilentMode = true;
        using var tempRepo = TempRepoFixture.CreateWithInitialCommit();

        var configuration = GitWizardConfiguration.CreateDefaultConfiguration();
        var report = new GitWizardReport(configuration);

        var paths = new SortedSet<string> { tempRepo.Path };
        report.Refresh(paths);
        Assert.That(report.DeletedPaths.Count, Is.EqualTo(0));
        Assert.That(report.Repositories.ContainsKey(tempRepo.Path), Is.True);

        // Delete the repo directory
        tempRepo.Dispose();

        // Refresh with the deleted path
        report.Repositories.Clear();
        report.Refresh(paths);

        Assert.That(report.DeletedPaths.Count, Is.EqualTo(1));
        Assert.That(report.DeletedPaths.Contains(tempRepo.Path), Is.True);
        Assert.That(report.Repositories.ContainsKey(tempRepo.Path), Is.False);
    }

    [Test]
    public void Refresh_MixedDeletedAndValid_Repos()
    {
        GitWizardLog.SilentMode = true;
        using var validRepo = TempRepoFixture.CreateWithInitialCommit();
        var deletedPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nonexistent-repo-" + Guid.NewGuid());

        var configuration = GitWizardConfiguration.CreateDefaultConfiguration();
        var report = new GitWizardReport(configuration);

        var paths = new SortedSet<string> { validRepo.Path, deletedPath };
        report.Refresh(paths);

        Assert.That(report.DeletedPaths.Count, Is.EqualTo(1));
        Assert.That(report.DeletedPaths.Contains(deletedPath), Is.True);
        Assert.That(report.Repositories.ContainsKey(validRepo.Path), Is.True);
    }

    [Test]
    public void Refresh_NoDeletedRepos_EmptyDeletedPaths()
    {
        GitWizardLog.SilentMode = true;
        using var validRepo = TempRepoFixture.CreateWithInitialCommit();

        var configuration = GitWizardConfiguration.CreateDefaultConfiguration();
        var report = new GitWizardReport(configuration);

        var paths = new SortedSet<string> { validRepo.Path };
        report.Refresh(paths);

        Assert.That(report.DeletedPaths.Count, Is.EqualTo(0));
    }

    [Test]
    public void Refresh_DeletedRepos_RemovedFromReportDictionary()
    {
        GitWizardLog.SilentMode = true;
        using var tempRepo = TempRepoFixture.CreateWithInitialCommit();

        var configuration = GitWizardConfiguration.CreateDefaultConfiguration();
        var report = new GitWizardReport(configuration);

        var paths = new SortedSet<string> { tempRepo.Path };
        report.Refresh(paths);
        Assert.That(report.Repositories.ContainsKey(tempRepo.Path), Is.True);

        // Delete the repo
        tempRepo.Dispose();

        // Refresh again
        report.Refresh(paths);
        Assert.That(report.Repositories.ContainsKey(tempRepo.Path), Is.False);
    }
}

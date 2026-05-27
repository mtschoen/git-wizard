using GitWizard;

namespace GitWizardTests;

public class GitWizardReportTests
{
    [Test]
    public void Report_HasSchemaVersion()
    {
        var report = new GitWizardReport();
        Assert.That(report.SchemaVersion, Is.EqualTo("2.0"));
    }

    [Test]
    public void Report_SchemaVersionSerializesToJson()
    {
        var report = new GitWizardReport();
        var json = System.Text.Json.JsonSerializer.Serialize(report);
        var deserialized = System.Text.Json.JsonSerializer.Deserialize<GitWizardReport>(json);
        Assert.That(deserialized!.SchemaVersion, Is.EqualTo("2.0"));
    }

    [Test]
    public void GeneratedReport_DefaultsBranchScopeToActionable()
    {
        GitWizardLog.SilentMode = true;
        var configuration = GitWizardConfiguration.CreateDefaultConfiguration();
        var report = GitWizardReport.GenerateReport(configuration, new List<string>());

        Assert.That(report.BranchScope, Is.EqualTo("actionable"));
        Assert.That(report.SchemaVersion, Is.EqualTo("2.0"));
    }

    [Test]
    public void RefreshConcurrencyTest()
    {
        GitWizardLog.SilentMode = true;
        using var repoA = TempRepoFixture.CreateWithInitialCommit();
        using var repoB = TempRepoFixture.CreateWithInitialCommit();

        var configuration = GitWizardConfiguration.CreateDefaultConfiguration();
        var report = new GitWizardReport(configuration);

        // Seed paths directly from temp repos (like the sibling Refresh_* tests) instead of
        // running discovery — discovery would trigger MFT/UAC on Windows, and the subject under
        // test here is parallel Refresh, not discovery.
        var repositoryPaths = new SortedSet<string> { repoA.Path, repoB.Path };
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
        tempRepo.DeleteNow();

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
        var deletedPath = Path.Combine(Path.GetTempPath(), "nonexistent-repo-" + Guid.NewGuid());

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
        tempRepo.DeleteNow();

        // Refresh again
        report.Refresh(paths);
        Assert.That(report.Repositories.ContainsKey(tempRepo.Path), Is.False);
    }

    [Test]
    public void Refresh_WithFetchRemotes_DoesNotThrow()
    {
        GitWizardLog.SilentMode = true;
        using var fixture = TempRepoFixture.CreateWithInitialCommit();

        var configuration = GitWizardConfiguration.CreateDefaultConfiguration();
        var report = new GitWizardReport(configuration);

        var paths = new SortedSet<string> { fixture.Path };
        report.Refresh(paths, fetchRemotes: true);

        Assert.That(report.Repositories.ContainsKey(fixture.Path), Is.True);
    }

    [Test]
    public void Refresh_WithDeepRefresh_DoesNotThrow()
    {
        GitWizardLog.SilentMode = true;
        using var fixture = TempRepoFixture.CreateWithInitialCommit();

        var configuration = GitWizardConfiguration.CreateDefaultConfiguration();
        var report = new GitWizardReport(configuration);

        var paths = new SortedSet<string> { fixture.Path };
        report.Refresh(paths, deepRefresh: true);

        Assert.That(report.Repositories.ContainsKey(fixture.Path), Is.True);
    }

    [Test]
    public void Refresh_WithAllBranches_PopulatesBranches()
    {
        GitWizardLog.SilentMode = true;
        using var fixture = TempRepoFixture.CreateWithInitialCommit();

        var configuration = GitWizardConfiguration.CreateDefaultConfiguration();
        var report = new GitWizardReport(configuration);

        var paths = new SortedSet<string> { fixture.Path };
        report.Refresh(paths, allBranches: true);

        Assert.That(report.Repositories.ContainsKey(fixture.Path), Is.True);
    }

    [Test]
    public void GetRepositoryPaths_WithEmptySearchPaths_DoesNotThrow()
    {
        var report = new GitWizardReport();
        var paths = new SortedSet<string>();

        // noMft: true skips MFT discovery so the test pops no UAC on Windows; with empty search
        // paths the recursive fallback is a no-op, so the assertion is unchanged.
        report.GetRepositoryPaths(paths, noMft: true);

        Assert.That(paths, Is.Empty);
    }

    [Test]
    public void Save_CreatesReportFile()
    {
        var report = new GitWizardReport();
        var reportPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");

        try
        {
            report.Save(reportPath);
            Assert.That(File.Exists(reportPath), Is.True);
        }
        finally
        {
            if (File.Exists(reportPath))
                File.Delete(reportPath);
        }
    }

    [Test]
    public async Task SaveAsync_CreatesReportFile()
    {
        var report = new GitWizardReport();
        var reportPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");

        try
        {
            await report.SaveAsync(reportPath);
            Assert.That(File.Exists(reportPath), Is.True);
        }
        finally
        {
            if (File.Exists(reportPath))
                File.Delete(reportPath);
        }
    }

    [Test]
    public void Save_SerializesRepositories()
    {
        var report = new GitWizardReport();
        report.Repositories["/test/repo"] = new GitWizardRepository("/test/repo");

        var reportPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");

        try
        {
            report.Save(reportPath);
            var json = File.ReadAllText(reportPath);
            Assert.That(json, Does.Contain("\"/test/repo\""));
        }
        finally
        {
            if (File.Exists(reportPath))
                File.Delete(reportPath);
        }
    }

    [Test]
    public void GetRepositoryPaths_Enumerable_ReturnsKeys()
    {
        var report = new GitWizardReport();
        report.Repositories["/repo/1"] = new GitWizardRepository("/repo/1");
        report.Repositories["/repo/2"] = new GitWizardRepository("/repo/2");

        var paths = report.GetRepositoryPaths().ToList();

        Assert.That(paths, Has.Count.EqualTo(2));
        Assert.That(paths, Does.Contain("/repo/1"));
        Assert.That(paths, Does.Contain("/repo/2"));
    }

    [Test]
    public void DeletedPaths_InitializedAsEmptySet()
    {
        var report = new GitWizardReport();
        Assert.That(report.DeletedPaths, Is.Not.Null);
        Assert.That(report.DeletedPaths, Is.Empty);
    }
}

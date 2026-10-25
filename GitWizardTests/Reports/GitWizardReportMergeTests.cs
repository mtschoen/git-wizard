using System.Text.Json;
using System.Text.Json.Nodes;
using GitWizard;
using GitWizardTests.TestSupport;

namespace GitWizardTests;

/// <summary>
/// Tests for the single-repo merge refresh used by the CLI <c>-merge</c> flag
/// (issue #42): read the existing report at the save path, upsert the supplied
/// repos, leave all other entries intact, stamp the schema version, write
/// atomically.
/// </summary>
public class GitWizardReportMergeTests
{
    [Test]
    public void Merge_InsertsNewEntry_WhenFileDoesNotExist()
    {
        GitWizardLog.SilentMode = true;
        using var repo = TempRepoFixture.CreateWithInitialCommit();
        using var dir = new TempDirectory("gw-merge-");
        var savePath = Path.Combine(dir.Path, "report.json");
        var configuration = GitWizardConfiguration.CreateDefaultConfiguration();

        var merged = GitWizardReport.MergeIntoFile(savePath, configuration,
            new List<string> { repo.Path });

        Assert.That(File.Exists(savePath), Is.True);
        Assert.That(merged.Repositories.ContainsKey(repo.Path), Is.True);

        var onDisk = JsonSerializer.Deserialize<GitWizardReport>(File.ReadAllText(savePath));
        Assert.That(onDisk!.Repositories.ContainsKey(repo.Path), Is.True);
    }

    [Test]
    public void Merge_PreservesExistingEntries()
    {
        GitWizardLog.SilentMode = true;
        using var newRepo = TempRepoFixture.CreateWithInitialCommit();
        using var dir = new TempDirectory("gw-merge-");
        var savePath = Path.Combine(dir.Path, "report.json");
        var configuration = GitWizardConfiguration.CreateDefaultConfiguration();

        // Seed an existing report containing an unrelated entry that the merge
        // must leave intact.
        const string existingPath = "/some/other/repo";
        var existing = new GitWizardReport();
        existing.Repositories[existingPath] = new GitWizardRepository(existingPath)
        {
            RefreshError = "sentinel-marker"
        };
        existing.Save(savePath);

        var merged = GitWizardReport.MergeIntoFile(savePath, configuration,
            new List<string> { newRepo.Path });

        Assert.That(merged.Repositories.ContainsKey(existingPath), Is.True,
                "merge must preserve the pre-existing entry");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(merged.Repositories[existingPath].RefreshError, Is.EqualTo("sentinel-marker"));
            Assert.That(merged.Repositories.ContainsKey(newRepo.Path), Is.True,
                "merge must insert the new entry");
        }

        var onDisk = JsonSerializer.Deserialize<GitWizardReport>(File.ReadAllText(savePath));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(onDisk!.Repositories.ContainsKey(existingPath), Is.True);
            Assert.That(onDisk.Repositories.ContainsKey(newRepo.Path), Is.True);
        }
    }

    [Test]
    public void Merge_UpdatesExistingTargetEntry()
    {
        GitWizardLog.SilentMode = true;
        using var repo = TempRepoFixture.CreateWithInitialCommit();
        using var dir = new TempDirectory("gw-merge-");
        var savePath = Path.Combine(dir.Path, "report.json");
        var configuration = GitWizardConfiguration.CreateDefaultConfiguration();

        // Seed a stale entry for the target repo path that should be replaced by
        // a fresh refresh.
        var existing = new GitWizardReport();
        existing.Repositories[repo.Path] = new GitWizardRepository(repo.Path)
        {
            RefreshError = "stale-placeholder-error"
        };
        existing.Save(savePath);

        var merged = GitWizardReport.MergeIntoFile(savePath, configuration,
            new List<string> { repo.Path });

        Assert.That(merged.Repositories.ContainsKey(repo.Path), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(merged.Repositories[repo.Path].RefreshError,
                Is.Not.EqualTo("stale-placeholder-error"),
                "merge must refresh (replace) the target entry, not keep the stale one");
            // A freshly-refreshed valid repo has its CurrentBranch populated.
            Assert.That(merged.Repositories[repo.Path].CurrentBranch, Is.Not.Null,
                "the target entry should reflect a real refresh");
        }
    }

    [Test]
    public void Merge_StampsCurrentSchemaVersion()
    {
        GitWizardLog.SilentMode = true;
        using var repo = TempRepoFixture.CreateWithInitialCommit();
        using var dir = new TempDirectory("gw-merge-");
        var savePath = Path.Combine(dir.Path, "report.json");
        var configuration = GitWizardConfiguration.CreateDefaultConfiguration();

        // Seed an existing report carrying a stale schema version.
        var existing = new GitWizardReport { SchemaVersion = "1.0" };
        // Save() restamps to current, so write raw JSON with the stale version to
        // exercise the merge's own stamping.
        File.WriteAllText(savePath, JsonSerializer.Serialize(existing));

        var merged = GitWizardReport.MergeIntoFile(savePath, configuration,
            new List<string> { repo.Path });

        Assert.That(merged.SchemaVersion, Is.EqualTo(GitWizardReport.CurrentSchemaVersion));

        var onDisk = JsonSerializer.Deserialize<GitWizardReport>(File.ReadAllText(savePath));
        Assert.That(onDisk!.SchemaVersion, Is.EqualTo(GitWizardReport.CurrentSchemaVersion));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Merge_CorruptExistingFile_StartsFresh_AndStillWritesTarget(bool hasTargetRepo)
    {
        GitWizardLog.SilentMode = true;
        using var repo = TempRepoFixture.CreateWithInitialCommit();
        using var dir = new TempDirectory("gw-merge-");
        var savePath = Path.Combine(dir.Path, "report.json");
        var configuration = GitWizardConfiguration.CreateDefaultConfiguration();

        // Existing file is not valid report JSON: merge should fall back to an empty
        // report rather than abort, and still write the refreshed target.
        File.WriteAllText(savePath, "{ this is not valid json ]");

        var targetPaths = hasTargetRepo ? new List<string> { repo.Path } : new List<string>();
        var merged = GitWizardReport.MergeIntoFile(savePath, configuration, targetPaths);

        if (hasTargetRepo)
        {
            Assert.That(merged.Repositories.ContainsKey(repo.Path), Is.True);
            var onDisk = JsonSerializer.Deserialize<GitWizardReport>(File.ReadAllText(savePath));
            Assert.That(onDisk!.Repositories.ContainsKey(repo.Path), Is.True);
        }
        else
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(merged.Repositories, Is.Empty);
                Assert.That(File.Exists(savePath), Is.True);
            }
        }
    }

    [Test]
    public void Merge_ExistingFileWithNoRepositories_AddsRepositoriesNode()
    {
        GitWizardLog.SilentMode = true;
        using var dir = new TempDirectory("gw-merge-");
        var savePath = Path.Combine(dir.Path, "report.json");
        var configuration = GitWizardConfiguration.CreateDefaultConfiguration();
        var existing = new JsonObject { ["SchemaVersion"] = "1.0" };
        File.WriteAllText(savePath, existing.ToJsonString());

        GitWizardReport.MergeIntoFile(savePath, configuration, new List<string>());
        var onDisk = JsonNode.Parse(File.ReadAllText(savePath))!.AsObject();
        Assert.That(onDisk.ContainsKey("Repositories"), Is.True);
        Assert.That(onDisk["Repositories"], Is.InstanceOf<JsonObject>());
    }

    [Test]
    public void Merge_CreatesParentDirectory_WhenMissing()
    {
        GitWizardLog.SilentMode = true;
        using var repo = TempRepoFixture.CreateWithInitialCommit();
        using var subDir = new TempDirectory("gw-merge-dir-");
        var savePath = Path.Combine(subDir.Path, "nested", "report.json");
        var configuration = GitWizardConfiguration.CreateDefaultConfiguration();

        var merged = GitWizardReport.MergeIntoFile(savePath, configuration,
            new List<string> { repo.Path });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.Exists(savePath), Is.True,
                    "the atomic writer must create missing parent directories");
            Assert.That(merged.Repositories.ContainsKey(repo.Path), Is.True);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Merge_EmptyPaths_DoesNotThrow_AndStampsSchema(bool existingFile)
    {
        GitWizardLog.SilentMode = true;
        using var dir = new TempDirectory("gw-merge-");
        var savePath = Path.Combine(dir.Path, "report.json");
        var configuration = GitWizardConfiguration.CreateDefaultConfiguration();

        const string existingPath = "/keep/me";
        if (existingFile)
        {
            var existing = new GitWizardReport();
            existing.Repositories[existingPath] = new GitWizardRepository(existingPath);
            existing.Save(savePath);
        }

        var merged = GitWizardReport.MergeIntoFile(savePath, configuration,
            new List<string>());

        if (existingFile)
        {
            Assert.That(merged.Repositories.ContainsKey(existingPath), Is.True);
        }

        Assert.That(merged.SchemaVersion, Is.EqualTo(GitWizardReport.CurrentSchemaVersion));

        var onDisk = JsonSerializer.Deserialize<GitWizardReport>(File.ReadAllText(savePath));
        Assert.That(onDisk!.SchemaVersion, Is.EqualTo(GitWizardReport.CurrentSchemaVersion));
    }

    [Test]
    public void Merge_PreservesPrivateSetFields_OnUntouchedEntries()
    {
        // Regression for the #42 fidelity flaw: the original merge deserialized the whole report
        // into GitWizardReport and re-serialized it, which dropped `private set` fields (e.g.
        // CurrentBranch) on UNTOUCHED entries - System.Text.Json won't populate private setters on
        // read, so they round-tripped to null and were omitted on write. The DOM-level merge keeps
        // untouched entries as their original JsonNode, so such fields must survive verbatim.
        GitWizardLog.SilentMode = true;
        using var repo = TempRepoFixture.CreateWithInitialCommit();
        using var dir = new TempDirectory("gw-merge-");
        var savePath = Path.Combine(dir.Path, "report.json");
        var configuration = GitWizardConfiguration.CreateDefaultConfiguration();

        // Seed a raw report whose untouched entry carries CurrentBranch (a private-set field).
        // Writing raw JSON (not via GitWizardRepository, whose CurrentBranch can't be set in an
        // object initializer) is exactly the shape a real prior full-scan report has on disk.
        const string preservedPath = "/preserved/other-repo";
        var seed = new JsonObject
        {
            ["SchemaVersion"] = "2.0",
            ["Repositories"] = new JsonObject
            {
                [preservedPath] = new JsonObject
                {
                    ["Path"] = preservedPath,
                    ["CurrentBranch"] = "preserved-branch",
                },
            },
        };
        File.WriteAllText(savePath, seed.ToJsonString());

        GitWizardReport.MergeIntoFile(savePath, configuration,
            new List<string> { repo.Path });

        // Assert against the ON-DISK json (the #42 contract surface), not the returned object.
        var root = JsonNode.Parse(File.ReadAllText(savePath))!.AsObject();
        var repositories = root["Repositories"]!.AsObject();
        Assert.That(repositories.ContainsKey(preservedPath), Is.True,
                "the untouched entry must remain after the merge");
        using (Assert.EnterMultipleScope())
        {
            Assert.That((string?)repositories[preservedPath]!["CurrentBranch"],
                Is.EqualTo("preserved-branch"),
                "a private-set field on the untouched entry must survive the merge verbatim");
            // And the refreshed target still landed.
            Assert.That(repositories.ContainsKey(repo.Path), Is.True,
                "the refreshed target entry must be present");
        }
    }

    [Test]
    public void Merge_LargeExistingFile_ReadsCompletelyAndMerges()
    {
        GitWizardLog.SilentMode = true;
        using var repo = TempRepoFixture.CreateWithInitialCommit();
        using var dir = new TempDirectory("gw-merge-");
        var savePath = Path.Combine(dir.Path, "report.json");
        var configuration = GitWizardConfiguration.CreateDefaultConfiguration();

        // Seed a large report (> 100KB) with many repository entries
        var repositoriesNode = new JsonObject();
        for (var i = 0; i < 500; i++)
        {
            var path = $"/large/test/repository/path/number/{i:D4}";
            repositoriesNode[path] = new JsonObject
            {
                ["Path"] = path,
                ["WorkingDirectory"] = path,
                ["RemoteUrls"] = new JsonArray("https://example.com/repo/" + i),
                ["CurrentBranch"] = "main",
                ["Branches"] = new JsonArray(
                    new JsonObject { ["Name"] = "main", ["IsCurrent"] = true, ["IsTracking"] = true }
                )
            };
        }

        var seed = new JsonObject
        {
            ["SchemaVersion"] = GitWizardReport.CurrentSchemaVersion,
            ["Repositories"] = repositoriesNode
        };
        File.WriteAllText(savePath, seed.ToJsonString());

        var merged = GitWizardReport.MergeIntoFile(savePath, configuration,
            new List<string> { repo.Path });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(merged.Repositories.ContainsKey(repo.Path), Is.True);
            Assert.That(merged.Repositories, Has.Count.EqualTo(501));
        }

        var onDisk = JsonNode.Parse(File.ReadAllText(savePath))!.AsObject();
        var onDiskRepos = onDisk["Repositories"]!.AsObject();
        Assert.That(onDiskRepos, Has.Count.EqualTo(501));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(onDiskRepos.ContainsKey(repo.Path), Is.True);
            Assert.That(onDiskRepos.ContainsKey("/large/test/repository/path/number/0250"), Is.True);
        }
    }
}

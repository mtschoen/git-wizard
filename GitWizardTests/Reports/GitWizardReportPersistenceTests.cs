using System.Text.Json.Nodes;
using GitWizard;
using GitWizardTests.TestSupport;

namespace GitWizardTests;

[TestFixture, NonParallelizable]
public class GitWizardReportPersistenceTests
{
    LocalFilesSandbox? _sandbox;
    string? _tempRoot;
    bool _previousSilent;
    bool _previousVerbose;

    [SetUp]
    public void SetUp()
    {
        _previousSilent = GitWizardLog.SilentMode;
        _previousVerbose = GitWizardLog.VerboseMode;
        GitWizardLog.SilentMode = true;
        _sandbox = new LocalFilesSandbox();
        _tempRoot = _sandbox.Path;
    }

    [TearDown]
    public void TearDown()
    {
        _sandbox?.Dispose();
        _sandbox = null;
        GitWizardLog.SilentMode = _previousSilent;
        GitWizardLog.VerboseMode = _previousVerbose;
    }

    static void SaveSync(GitWizardReport report, string path) => report.Save(path);
    static GitWizardReport? LoadSync() => GitWizardReport.GetCachedReport();

    [Test]
    public void GetCachedReportPath_ReturnsCorrectPath()
    {
        var localPath = GitWizardApi.GetLocalFilesPath();
        var cachedPath = GitWizardReport.GetCachedReportPath();
        Assert.That(cachedPath, Does.EndWith(Path.Combine(localPath, "report.json")));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task SaveAndLoadCachedReport_RoundTrip(bool async)
    {
        var report = new GitWizardReport { SchemaVersion = "0.0" };
        report.Repositories["/repo/one"] = new GitWizardRepository("/repo/one");
        report.Repositories["/repo/two"] = new GitWizardRepository("/repo/two");

        var path = GitWizardReport.GetCachedReportPath();
        if (async)
        {
            await report.SaveAsync(path);
        }
        else
        {
            SaveSync(report, path);
        }

        TestUtilities.ResetStaticCaches();
        var loaded = async
            ? await GitWizardReport.GetCachedReportAsync()
            : LoadSync();

        Assert.That(loaded, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(loaded!.SchemaVersion, Is.EqualTo(GitWizardReport.CurrentSchemaVersion));
            Assert.That(loaded.Repositories.Keys, Is.EqualTo(["/repo/one", "/repo/two"]));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task GetCachedReport_WhenNoFile_ReturnsNull(bool async)
    {
        var result = async
            ? await GitWizardReport.GetCachedReportAsync()
            : LoadSync();
        Assert.That(result, Is.Null);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task GetCachedReport_InvalidJson_ReturnsNull(bool async)
    {
        GitWizardApi.EnsureLocalFolderExists();
        var path = GitWizardReport.GetCachedReportPath();
        await File.WriteAllTextAsync(path, "not valid json {{{");
        TestUtilities.ResetStaticCaches();

        var result = async
            ? await GitWizardReport.GetCachedReportAsync()
            : LoadSync();
        Assert.That(result, Is.Null);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task GetCachedReport_SecondCall_ReturnsCachedInstance(bool async)
    {
        var report = new GitWizardReport();
        var path = GitWizardReport.GetCachedReportPath();
        if (async)
        {
            await report.SaveAsync(path);
        }
        else
        {
            SaveSync(report, path);
        }

        var first = async
            ? await GitWizardReport.GetCachedReportAsync()
            : LoadSync();
        Assert.That(first, Is.Not.Null);

        var second = async
            ? await GitWizardReport.GetCachedReportAsync()
            : LoadSync();
        Assert.That(second, Is.SameAs(first));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Save_CreatesDirectoryIfNeeded(bool async)
    {
        var report = new GitWizardReport();
        var nested = Path.Combine(_tempRoot!, "sub", "dir", async ? "async_report.json" : "report.json");
        if (async)
        {
            await report.SaveAsync(nested);
        }
        else
        {
            SaveSync(report, nested);
        }

        Assert.That(File.Exists(nested), Is.True);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Save_StampsSchemaVersion_AndWritesValidJson(bool async)
    {
        var report = new GitWizardReport { SchemaVersion = "old" };
        report.Repositories["/repo/one"] = new GitWizardRepository("/repo/one");
        var path = Path.Combine(_tempRoot!, async ? "version_async.json" : "version_sync.json");
        if (async)
        {
            await report.SaveAsync(path);
        }
        else
        {
            SaveSync(report, path);
        }

        var json = await File.ReadAllTextAsync(path);
        Assert.That(json, Does.Contain(GitWizardReport.CurrentSchemaVersion));
        var deserialized = System.Text.Json.JsonSerializer.Deserialize<GitWizardReport>(json);
        Assert.That(deserialized!.SchemaVersion, Is.EqualTo(GitWizardReport.CurrentSchemaVersion));
        Assert.That(deserialized.Repositories.ContainsKey("/repo/one"), Is.True);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Save_WhenPathIsADirectory_LogsAndSwallows(bool async)
    {
        var dir = Path.Combine(_tempRoot!, async ? "gw-save-dir-async" : "gw-save-dir-sync");
        Directory.CreateDirectory(dir);
        var report = new GitWizardReport();
        if (async)
        {
            await report.SaveAsync(dir);
        }
        else
        {
            SaveSync(report, dir);
        }

        Assert.That(Directory.Exists(dir), Is.True);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SaveAtomic_WritesReportAtomically_AndPreservesDirectoryInvariants(bool overwrite)
    {
        var dir = Path.Combine(_tempRoot!, "atomic_" + (overwrite ? "overwrite" : "new"), "sub");
        var path = Path.Combine(dir, "report.json");

        if (overwrite)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, "{\"old\": true}");
        }

        var report = new GitWizardReport { SchemaVersion = "0.0" };
        report.Repositories["/new/repo"] = new GitWizardRepository("/new/repo");
        report.SaveAtomic(path);

        Assert.That(File.Exists(path), Is.True);
        var jsonText = File.ReadAllText(path);
        var root = JsonNode.Parse(jsonText)!.AsObject();

        Assert.Multiple(() =>
        {
            Assert.That((string?)root["SchemaVersion"], Is.EqualTo(GitWizardReport.CurrentSchemaVersion));
            var repos = root["Repositories"]!.AsObject();
            Assert.That(repos.ContainsKey("/new/repo"), Is.True);
            Assert.That(root.ContainsKey("old"), Is.False);
            var files = Directory.GetFiles(dir);
            Assert.That(files, Has.Length.EqualTo(1));
            Assert.That(files[0], Is.EqualTo(path));
        });
    }

    [Test]
    public void SaveAtomic_WhenTargetIsDirectory_SwallowsExceptionAndLeavesNoTempFiles()
    {
        using var directory = new TempDirectory();
        var root = directory.Path;
        var target = Path.Combine(root, "directory-target");
        Directory.CreateDirectory(target);
        var report = new GitWizardReport();
        Assert.DoesNotThrow(() => report.SaveAtomic(target));
        Assert.That(Directory.Exists(target), Is.True);
        Assert.That(Directory.GetFiles(root, "*.tmp"), Is.Empty);
    }
}

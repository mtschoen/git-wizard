using System.Text.Json.Nodes;
using GitWizard;
using GitWizardTests.TestSupport;

namespace GitWizardTests;

/// <summary>
/// Tests for the asynchronous report persistence paths: <c>SaveAtomicAsync</c>,
/// <c>ReadJsonObjectFromFileAsync</c> and <c>MergeIntoFileAsync</c>. They mirror the synchronous
/// contract (atomic temp-file-then-move write, merge keeps untouched entries) and add
/// cancellation behaviour.
/// </summary>
[TestFixture, NonParallelizable]
public class GitWizardReportAsyncPersistenceTests
{
    const string OldContent = "{\"old\": true}";
    bool _previousSilent;

    [SetUp]
    public void SetUp()
    {
        _previousSilent = GitWizardLog.SilentMode;
        GitWizardLog.SilentMode = true;
    }

    [TearDown]
    public void TearDown()
    {
        GitWizardLog.SilentMode = _previousSilent;
    }

    [Test]
    public async Task SaveAtomicAsync_RoundTripsReport_AndLeavesNoTempFiles()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "nested", "report.json");
        var report = new GitWizardReport { SchemaVersion = "0.0" };
        report.Repositories["/new/repo"] = new GitWizardRepository("/new/repo");

        await report.SaveAtomicAsync(path);

        var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        using (Assert.EnterMultipleScope())
        {
            Assert.That((string?)root["SchemaVersion"], Is.EqualTo(GitWizardReport.CurrentSchemaVersion));
            Assert.That(root["Repositories"]!.AsObject().ContainsKey("/new/repo"), Is.True);
            Assert.That(Directory.GetFiles(Path.GetDirectoryName(path)!), Is.EqualTo(new[] { path }));
        }
    }

    [Test]
    public async Task SaveAtomicAsync_ReplacesExistingFile()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "report.json");
        await File.WriteAllTextAsync(path, OldContent);
        var report = new GitWizardReport { SchemaVersion = "0.0" };
        report.Repositories["/new/repo"] = new GitWizardRepository("/new/repo");

        await report.SaveAtomicAsync(path);

        var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(root.ContainsKey("old"), Is.False);
            Assert.That(root["Repositories"]!.AsObject().ContainsKey("/new/repo"), Is.True);
            Assert.That(Directory.GetFiles(directory.Path), Has.Length.EqualTo(1));
        }
    }

    [Test]
    public async Task SaveAtomicAsync_WhenTargetIsDirectory_SwallowsExceptionAndLeavesNoTempFiles()
    {
        using var directory = new TempDirectory();
        var target = Path.Combine(directory.Path, "directory-target");
        Directory.CreateDirectory(target);

        await new GitWizardReport().SaveAtomicAsync(target);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Directory.Exists(target), Is.True);
            Assert.That(Directory.GetFiles(directory.Path, "*.tmp"), Is.Empty);
        }
    }

    [Test]
    public async Task SaveAtomicAsync_WhenAlreadyCancelled_ThrowsAndKeepsExistingFile()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "report.json");
        await File.WriteAllTextAsync(path, OldContent);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        Assert.ThrowsAsync<OperationCanceledException>(
            async () => await new GitWizardReport().SaveAtomicAsync(path, cancellation.Token));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await File.ReadAllTextAsync(path), Is.EqualTo(OldContent));
            Assert.That(Directory.GetFiles(directory.Path), Has.Length.EqualTo(1));
        }
    }

    [Test]
    public async Task ReadJsonObjectFromFileAsync_ReadsObject()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "report.json");
        await File.WriteAllTextAsync(path, "{\"SchemaVersion\": \"1.0\"}");

        var root = await GitWizardReport.ReadJsonObjectFromFileAsync(path);

        Assert.That((string?)root?["SchemaVersion"], Is.EqualTo("1.0"));
    }

    [Test]
    public async Task ReadJsonObjectFromFileAsync_ReturnsNull_WhenMissingOrMalformed()
    {
        using var directory = new TempDirectory();
        var malformed = Path.Combine(directory.Path, "bad.json");
        await File.WriteAllTextAsync(malformed, "{ not json");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await GitWizardReport.ReadJsonObjectFromFileAsync(Path.Combine(directory.Path, "missing.json")), Is.Null);
            Assert.That(await GitWizardReport.ReadJsonObjectFromFileAsync(malformed), Is.Null);
        }
    }

    [Test]
    public async Task ReadJsonObjectFromFileAsync_WhenAlreadyCancelled_Throws()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "report.json");
        await File.WriteAllTextAsync(path, "{}");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        Assert.ThrowsAsync<OperationCanceledException>(
            async () => await GitWizardReport.ReadJsonObjectFromFileAsync(path, cancellation.Token));
    }

    [Test]
    public async Task MergeIntoFileAsync_KeepsUntouchedEntries_AndStampsSchemaVersion()
    {
        using var repository = TempRepoFixture.CreateWithInitialCommit();
        using var directory = new TempDirectory("gw-merge-");
        var savePath = Path.Combine(directory.Path, "report.json");
        var existing = new JsonObject
        {
            ["SchemaVersion"] = "0.0",
            ["Repositories"] = new JsonObject { ["/untouched"] = new JsonObject { ["Marker"] = "kept" } },
        };
        await File.WriteAllTextAsync(savePath, existing.ToJsonString());
        var configuration = GitWizardConfiguration.CreateDefaultConfiguration();

        var merged = await GitWizardReport.MergeIntoFileAsync(savePath, configuration, [repository.Path]);

        var onDisk = JsonNode.Parse(await File.ReadAllTextAsync(savePath))!.AsObject();
        var repositories = onDisk["Repositories"]!.AsObject();
        var key = repository.Path;
        using (Assert.EnterMultipleScope())
        {
            Assert.That((string?)repositories["/untouched"]!["Marker"], Is.EqualTo("kept"));
            Assert.That(repositories.ContainsKey(key), Is.True);
            Assert.That((string?)onDisk["SchemaVersion"], Is.EqualTo(GitWizardReport.CurrentSchemaVersion));
            Assert.That(merged.Repositories.ContainsKey(key), Is.True);
            Assert.That(Directory.GetFiles(directory.Path), Has.Length.EqualTo(1));
        }
    }

    [Test]
    public async Task MergeIntoFileAsync_CreatesFile_WhenMissing()
    {
        using var directory = new TempDirectory("gw-merge-");
        var savePath = Path.Combine(directory.Path, "sub", "report.json");

        await GitWizardReport.MergeIntoFileAsync(savePath, GitWizardConfiguration.CreateDefaultConfiguration(), []);

        var onDisk = JsonNode.Parse(await File.ReadAllTextAsync(savePath))!.AsObject();
        Assert.That(onDisk["Repositories"], Is.InstanceOf<JsonObject>());
    }

    [Test]
    public async Task MergeIntoFileAsync_WhenAlreadyCancelled_ThrowsAndKeepsExistingFile()
    {
        using var directory = new TempDirectory("gw-merge-");
        var savePath = Path.Combine(directory.Path, "report.json");
        await File.WriteAllTextAsync(savePath, OldContent);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        Assert.ThrowsAsync<OperationCanceledException>(async () => await GitWizardReport.MergeIntoFileAsync(
            savePath, GitWizardConfiguration.CreateDefaultConfiguration(), [], cancellationToken: cancellation.Token));

        Assert.That(await File.ReadAllTextAsync(savePath), Is.EqualTo(OldContent));
    }
}

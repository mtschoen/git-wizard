using System.Text;
using System.Text.Json.Nodes;
using GitWizard;
using GitWizardTests.TestSupport;

namespace GitWizardTests;

/// <summary>
/// Tests for the asynchronous report persistence paths: <c>SaveAtomicAsync</c>,
/// <c>ReadJsonObjectFromFileAsync</c>, <c>MergeIntoFileAsync</c> and the shared
/// <c>WriteAtomicAsync</c> atomic write. They mirror the synchronous contract
/// (atomic temp-file-then-move write, merge keeps untouched entries) and add
/// cancellation behaviour.
/// </summary>
[TestFixture, NonParallelizable]
public class GitWizardReportAsyncPersistenceTests
{
    const string OldContent = "{\"old\": true}";
    const string NewContent = "{\"new\": true}";
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
        string[] expectedFiles = [path];
        using (Assert.EnterMultipleScope())
        {
            Assert.That((string?)root["SchemaVersion"], Is.EqualTo(GitWizardReport.CurrentSchemaVersion));
            Assert.That(root["Repositories"]!.AsObject().ContainsKey("/new/repo"), Is.True);
            Assert.That(Directory.GetFiles(Path.GetDirectoryName(path)!), Is.EqualTo(expectedFiles));
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
    public async Task WriteAtomicAsync_WhenCancelledWhileWriting_ThrowsAndKeepsExistingFile()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "report.json");
        await File.WriteAllTextAsync(path, OldContent);
        using var cancellation = new CancellationTokenSource();
        string? tempPath = null;

        // A controlled writer is the only deterministic boundary here: the first write proves the
        // temp file exists, then cancellation lands before the second one can be issued.
        async Task WritePartialContent(Stream stream, CancellationToken token)
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes("{\"new\": "), token);
            tempPath = ((FileStream)stream).Name;
            await cancellation.CancelAsync();
            await stream.WriteAsync(Encoding.UTF8.GetBytes("true}"), token);
        }

        // A cancelled overlapped write surfaces as TaskCanceledException, so the contract asserted
        // here is "a cancellation propagated", not one exact BCL exception type.
        Assert.CatchAsync<OperationCanceledException>(
            async () => await GitWizardReport.WriteAtomicAsync(path, WritePartialContent, cancellation.Token));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tempPath, Is.Not.Null);
            Assert.That(File.Exists(tempPath), Is.False);
            Assert.That(await File.ReadAllTextAsync(path), Is.EqualTo(OldContent));
            Assert.That(Directory.GetFiles(directory.Path), Has.Length.EqualTo(1));
        }
    }

    [Test]
    public async Task WriteAtomicAsync_WhenCancelledBeforeReplacement_ThrowsAndKeepsExistingFile()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "report.json");
        await File.WriteAllTextAsync(path, OldContent);
        using var cancellation = new CancellationTokenSource();
        string? tempPath = null;

        // The content write completes and only then is the token cancelled, so this exercises the
        // pre-rename check: without it the finished temp file would replace the destination.
        async Task WriteFullContent(Stream stream, CancellationToken token)
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes(NewContent), token);
            tempPath = ((FileStream)stream).Name;
            await cancellation.CancelAsync();
        }

        Assert.ThrowsAsync<OperationCanceledException>(
            async () => await GitWizardReport.WriteAtomicAsync(path, WriteFullContent, cancellation.Token));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tempPath, Is.Not.Null);
            Assert.That(File.Exists(tempPath), Is.False);
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

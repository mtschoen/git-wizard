using System.Text.Json;
using GitWizard;
using LibGit2Sharp;

namespace GitWizardTests;

[TestFixture]
public class TagInventoryReportTests
{
    [Test]
    public void IsTagMatch_PatternWildcards_BehavesCorrectly()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(TagInventoryReport.IsTagMatch("v1.0.0", "*"), Is.True);
            Assert.That(TagInventoryReport.IsTagMatch("archive/2026-01", "archive/*"), Is.True);
            Assert.That(TagInventoryReport.IsTagMatch("archive/foo/bar", "archive/*"), Is.True);
            Assert.That(TagInventoryReport.IsTagMatch("release/1.0", "archive/*"), Is.False);
            Assert.That(TagInventoryReport.IsTagMatch("v1.0", "v1.?"), Is.True);
            Assert.That(TagInventoryReport.IsTagMatch("v1.00", "v1.?"), Is.False);
            Assert.That(TagInventoryReport.IsTagMatch("v1.0.0", "v1.0.0"), Is.True);
            Assert.That(TagInventoryReport.IsTagMatch("v1.0.1", "v1.0.0"), Is.False);
        }
    }

    [Test]
    public void Scan_WithMatchingAndNonMatchingTags_FiltersCorrectly()
    {
        using var repo = TempRepoFixture.CreateWithInitialCommit();
        repo.AddTag("archive/old-release");
        repo.AddTag("v1.0.0");

        var report = TagInventoryReport.Scan([repo.Path], matchPattern: "archive/*", remoteTagFetcher: _ => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>()));

        Assert.That(report.Repositories, Has.Count.EqualTo(1));
        var repoItem = report.Repositories[0];
        Assert.That(repoItem.Tags, Has.Count.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(repoItem.Tags[0].Name, Is.EqualTo("archive/old-release"));
            Assert.That(repoItem.Tags[0].CommitSha, Is.Not.Empty);
            Assert.That(repoItem.Tags[0].ExistsOnRemote, Is.False);
        }
    }

    [Test]
    public void Scan_OmitsRepositoryWithNoMatchingTags()
    {
        using var repo = TempRepoFixture.CreateWithInitialCommit();
        repo.AddTag("v1.0.0");

        var report = TagInventoryReport.Scan([repo.Path], matchPattern: "archive/*", remoteTagFetcher: _ => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>()));

        Assert.That(report.Repositories, Is.Empty);
    }

    [Test]
    public void Scan_ChecksRemoteTagPresence()
    {
        using var repo = TempRepoFixture.CreateWithInitialCommit();
        repo.AddOriginRemoteAndPush();
        repo.AddTag("archive/reaped-on-remote");
        repo.AddTag("archive/still-on-remote");

        // Mock remote tag fetcher returning only still-on-remote
        Task<IReadOnlySet<string>> MockFetcher(string url)
        {
            IReadOnlySet<string> remoteTags = new HashSet<string>(StringComparer.Ordinal)
            {
                "archive/still-on-remote"
            };
            return Task.FromResult(remoteTags);
        }

        var report = TagInventoryReport.Scan([repo.Path], matchPattern: "archive/*", remoteTagFetcher: MockFetcher);

        Assert.That(report.Repositories, Has.Count.EqualTo(1));
        var repoItem = report.Repositories[0];
        Assert.That(repoItem.Tags, Has.Count.EqualTo(2));

        var reapedTag = repoItem.Tags.First(t => t.Name == "archive/reaped-on-remote");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reapedTag.ExistsOnRemote, Is.False);
            Assert.That(reapedTag.Remotes, Is.Empty);
        }

        var presentTag = repoItem.Tags.First(t => t.Name == "archive/still-on-remote");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(presentTag.ExistsOnRemote, Is.True);
            Assert.That(presentTag.Remotes, Does.Contain("origin"));
        }
    }

    [Test]
    public void Scan_SerializesToJsonWithSchemaVersion()
    {
        using var repo = TempRepoFixture.CreateWithInitialCommit();
        repo.AddTag("v1.0.0");

        var report = TagInventoryReport.Scan([repo.Path], matchPattern: "*", remoteTagFetcher: _ => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>()));

        var json = JsonSerializer.Serialize(report);
        Assert.That(json, Does.Contain("\"SchemaVersion\":\"1.0\""));
        Assert.That(json, Does.Contain("\"MatchPattern\":\"*\""));
        Assert.That(json, Does.Contain("v1.0.0"));
    }

    [Test]
    public async Task FetchRemoteTagsAsync_RealBareOrigin_ParsesLightweightAndAnnotatedTags()
    {
        using var repo = TempRepoFixture.CreateWithInitialCommit();
        repo.AddOriginRemoteAndPush();
        repo.AddTagAndPush("v1.0.0");
        repo.AddAnnotatedTagAndPush("v2.0.0", "Release 2.0");

        string originUrl;
        using (var libgitRepo = new Repository(repo.Path))
        {
            originUrl = libgitRepo.Network.Remotes["origin"].Url;
        }

        var tags = await TagInventoryReport.FetchRemoteTagsAsync(originUrl);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tags, Does.Contain("v1.0.0"));
            Assert.That(tags, Does.Contain("v2.0.0"));
            Assert.That(tags, Does.Not.Contain("v2.0.0^{}"));
        }
    }

    [Test]
    public async Task FetchRemoteTagsAsync_UnreachableRemote_ReturnsEmptyGracefully()
    {
        var tags = await TagInventoryReport.FetchRemoteTagsAsync("file:///nonexistent/path/that/does/not/exist");
        Assert.That(tags, Is.Empty);
    }

    [Test]
    public void Scan_RealOriginWithoutMock_ResolvesRemotePresence()
    {
        using var repo = TempRepoFixture.CreateWithInitialCommit();
        repo.AddOriginRemoteAndPush();
        repo.AddTagAndPush("archive/still-on-remote");
        repo.AddTag("archive/reaped-on-remote");

        var report = TagInventoryReport.Scan([repo.Path], matchPattern: "archive/*");

        Assert.That(report.Repositories, Has.Count.EqualTo(1));
        var repoItem = report.Repositories[0];
        Assert.That(repoItem.Tags, Has.Count.EqualTo(2));

        var reapedTag = repoItem.Tags.First(t => t.Name == "archive/reaped-on-remote");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reapedTag.ExistsOnRemote, Is.False);
            Assert.That(reapedTag.Remotes, Is.Empty);
        }

        var presentTag = repoItem.Tags.First(t => t.Name == "archive/still-on-remote");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(presentTag.ExistsOnRemote, Is.True);
            Assert.That(presentTag.Remotes, Does.Contain("origin"));
        }
    }
}

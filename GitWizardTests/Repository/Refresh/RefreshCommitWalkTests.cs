using System.Collections.Concurrent;
using GitWizard;
using GitWizardTests.TestSupport;
using LibGit2Sharp;

namespace GitWizardTests;

// The author-email cache and the recent-commit summary share one revwalk from HEAD.
// NonParallelizable because the typed log sink is global static state.
[NonParallelizable]
public class RefreshCommitWalkTests
{
    static readonly DateTimeOffset FirstCommitTime = new(2024, 1, 1, 12, 0, 0, TimeSpan.Zero);

    readonly ConcurrentQueue<string> _errorMessages = new();
    Action<string?, GitWizardLog.LogType>? _originalTypedSink;
    bool _originalSilentMode;

    [SetUp]
    public void SetUp()
    {
        _errorMessages.Clear();
        _originalTypedSink = GitWizardLog.TypedLogMethod;
        _originalSilentMode = GitWizardLog.SilentMode;
        GitWizardLog.SilentMode = true;
        GitWizardLog.TypedLogMethod = (message, type) =>
        {
            if (message != null && type == GitWizardLog.LogType.Error)
            {
                _errorMessages.Enqueue(message);
            }
        };
    }

    [TearDown]
    public void TearDown()
    {
        GitWizardLog.TypedLogMethod = _originalTypedSink;
        GitWizardLog.SilentMode = _originalSilentMode;
    }

    [Test]
    public void FusedWalk_FillsBothResultsFromOneHistory()
    {
        using var directory = new TempDirectory("gw-walk-");
        Repository.Init(directory.Path);
        var shas = CommitLinearHistory(directory.Path, 15, index => $"user{index % 4}@example.com");

        var repository = new GitWizardRepository(directory.Path);
        using (var handle = new Repository(directory.Path))
        {
            repository.RefreshAuthorEmailsAndRecentCommits(handle);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repository.AuthorEmails, Is.EquivalentTo(
                ["user0@example.com", "user1@example.com", "user2@example.com", "user3@example.com"]));
            Assert.That(repository.RecentCommits, Is.Not.Null);
            Assert.That(repository.RecentCommits!, Has.Count.EqualTo(10));
        }

        for (var position = 0; position < 10; position++)
        {
            var commitIndex = 14 - position;
            var recent = repository.RecentCommits![position];
            using (Assert.EnterMultipleScope())
            {
                Assert.That(recent.Hash, Is.EqualTo(shas[commitIndex][..7]));
                Assert.That(recent.Message, Is.EqualTo($"commit {commitIndex}"));
                Assert.That(recent.AuthorEmail, Is.EqualTo($"user{commitIndex % 4}@example.com"));
                Assert.That(recent.Date, Is.EqualTo(FirstCommitTime.AddMinutes(commitIndex)));
            }
        }
    }

    [TestCase(new[] { 0, 2, 1, 3 }, new[] { 3, 2, 1, 0 },
        TestName = "FusedWalk_Issue476_NonMonotonicHistoryFollowsParentLinks")]
    [TestCase(new[] { 12, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 }, new[] { 11, 10, 9, 8, 7, 6, 5, 4, 3, 2 },
        TestName = "FusedWalk_Issue476_NonMonotonicHistoryKeepsAncestryPrefixAtTenCommitLimit")]
    public void FusedWalk_NonMonotonicHistorySelectsAncestryPrefix(int[] commitDayOffsets, int[] expectedRecentIndices)
    {
        using var directory = new TempDirectory("gw-walk-");
        Repository.Init(directory.Path);
        var hashes = CommitLinearHistory(directory.Path, commitDayOffsets.Length,
            index => $"author{index}@example.com", index => FirstCommitTime.AddDays(commitDayOffsets[index]));

        var repository = new GitWizardRepository(directory.Path);
        using (var handle = new Repository(directory.Path))
        {
            repository.RefreshAuthorEmailsAndRecentCommits(handle);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repository.RecentCommits!.Select(commit => commit.Hash),
                Is.EqualTo(expectedRecentIndices.Select(index => hashes[index][..7])));
            Assert.That(repository.AuthorEmails, Is.EquivalentTo(
                Enumerable.Range(0, commitDayOffsets.Length).Select(index => $"author{index}@example.com")));
        }
    }

    [Test]
    public void FusedWalk_AuthorEmailsAreACaseInsensitiveSet()
    {
        using var directory = new TempDirectory("gw-walk-");
        Repository.Init(directory.Path);
        CommitLinearHistory(directory.Path, 3, index => index == 1 ? "Mixed@Example.com" : "mixed@example.com");

        var repository = new GitWizardRepository(directory.Path);
        using (var handle = new Repository(directory.Path))
        {
            repository.RefreshAuthorEmailsAndRecentCommits(handle);
        }

        Assert.That(repository.AuthorEmails, Has.Count.EqualTo(1));
    }

    [Test]
    public void FusedWalk_LongHistoryKeepsNewestTwoHundredAuthorsAndTenCommits()
    {
        using var directory = new TempDirectory("gw-walk-");
        Repository.Init(directory.Path);
        var shas = CommitLinearHistory(directory.Path, 250, index => $"author{index}@example.com");

        var repository = new GitWizardRepository(directory.Path);
        using (var handle = new Repository(directory.Path))
        {
            repository.RefreshAuthorEmailsAndRecentCommits(handle);
        }

        var expectedEmails = Enumerable.Range(50, 200).Select(index => $"author{index}@example.com");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(repository.AuthorEmails, Is.EquivalentTo(expectedEmails));
            Assert.That(repository.RecentCommits!.Select(commit => commit.Hash),
                Is.EqualTo(Enumerable.Range(240, 10).Reverse().Select(index => shas[index][..7])));
        }
    }

    [Test]
    public void Refresh_UnbornHead_LeavesBothResultsEmpty()
    {
        using var directory = new TempDirectory("gw-walk-");
        Repository.Init(directory.Path);

        var repository = new GitWizardRepository(directory.Path);
        Assert.DoesNotThrow(() => repository.Refresh());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repository.AuthorEmails ?? [], Is.Empty);
            Assert.That(repository.RecentCommits, Is.Empty);
        }
    }

    [Test]
    public void FusedWalk_UnusableRepositoryHandle_LeavesAuthorsUnsetAndRecentCommitsEmpty()
    {
        var repository = new GitWizardRepository("/fake");

        Assert.DoesNotThrow(() => repository.RefreshAuthorEmailsAndRecentCommits(null!));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repository.AuthorEmails, Is.Null);
            Assert.That(repository.RecentCommits, Is.Not.Null.And.Empty);
        }
    }

    [TestCase(6, 3)]
    [TestCase(15, 10)]
    public void FusedWalk_MissingParent_RetainsAuthorsPublishesPrefixAndLogsOnce(
        int historyCount, int expectedPrefixCount)
    {
        using var directory = new TempDirectory("gw-walk-");
        Repository.Init(directory.Path);
        var priorHashes = CommitLinearHistory(directory.Path, 2, index => $"prior{index}@example.com");
        var repository = new GitWizardRepository(directory.Path);
        using (var handle = new Repository(directory.Path))
        {
            repository.RefreshAuthorEmailsAndRecentCommits(handle);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repository.AuthorEmails, Is.EquivalentTo(["prior0@example.com", "prior1@example.com"]));
            Assert.That(repository.RecentCommits!.Select(commit => commit.Hash),
                Is.EqualTo(priorHashes.AsEnumerable().Reverse().Select(hash => hash[..7])));
            Assert.That(_errorMessages, Is.Empty);
        }

        var hashes = CommitLinearHistory(directory.Path, historyCount, index => $"current{index}@example.com");
        var missingHash = hashes[1];
        var objectPath = Path.Combine(directory.Path, ".git", "objects", missingHash[..2], missingHash[2..]);
        File.SetAttributes(objectPath, File.GetAttributes(objectPath) & ~FileAttributes.ReadOnly);
        File.Delete(objectPath);

        // Reopen after deletion so libgit2 cannot serve the missing object from this handle's cache.
        using (var handle = new Repository(directory.Path))
        {
            repository.RefreshAuthorEmailsAndRecentCommits(handle);
        }

        // The walker loads a commit's parent before yielding it, so commit 2 is not produced.
        var expectedCommits = Enumerable.Range(historyCount - expectedPrefixCount, expectedPrefixCount)
            .Reverse().Select(index => (hashes[index][..7], $"commit {index}",
                $"current{index}@example.com", FirstCommitTime.AddMinutes(index)));
        var contexts = _errorMessages.Where(message => message.EndsWith("Exception follows:", StringComparison.Ordinal))
            .ToArray();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(repository.AuthorEmails, Is.EquivalentTo(["prior0@example.com", "prior1@example.com"]));
            Assert.That(repository.RecentCommits!.Select(commit =>
                (commit.Hash, commit.Message, commit.AuthorEmail, commit.Date)), Is.EqualTo(expectedCommits));
            Assert.That(contexts, Has.Length.EqualTo(1));
            Assert.That(contexts, Has.All.Contains("author emails").And.Contains("recent commits").And.Contains(directory.Path));
            Assert.That(_errorMessages, Has.Some.Contains(missingHash));
        }
    }

    static List<string> CommitLinearHistory(string path, int count, Func<int, string> emailForIndex,
        Func<int, DateTimeOffset>? timeForIndex = null)
    {
        var shas = new List<string>(count);
        using var repository = new Repository(path);
        for (var index = 0; index < count; index++)
        {
            var commitTime = timeForIndex?.Invoke(index) ?? FirstCommitTime.AddMinutes(index);
            var signature = new Signature("Test", emailForIndex(index), commitTime);
            var commit = repository.Commit($"commit {index}", signature, signature,
                new CommitOptions { AllowEmptyCommit = true });
            shas.Add(commit.Sha);
        }

        return shas;
    }
}

using GitWizard;
using GitWizard.Watch;
using GitWizardTests.TestSupport;
using MFTLibTestExtensions;

namespace GitWizardTests;

/// <summary>
///     A journal create that reuses a deleted record's row slot supersedes every handle minted for the
///     earlier file: its columns throw InvalidOperationException. Discovery must treat such an entry as gone.
/// </summary>
[TestFixture]
public class IndexDiscoverySupersededEntryTests
{

    [Test]
    public async Task SupersededEntryIsSkippedNotThrown()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repository");
        var scripted = new ScriptedWatchSource();
        await using var index = await files.OpenMftIndexAsync(scripted, 'X');
        await index.StartWatchingAsync(['X'], CancellationToken.None);
        await scripted.WaitForStartAsync('X', 1, CancellationToken.None).WaitAsync(TestTimeouts.HangGuard);
        var marker = GitWizardApi.SelectGitEntries(index, includeGitFiles: false).Single();
        var directory = marker.Parent!.Value;
        Assert.That(directory.Name, Is.EqualTo("repository"));

        await scripted.WatchFor('X').PublishBatchAsync([
            Record(marker.RecordKey.RecordNumber, directory.RecordKey.RecordNumber, ".git", SyntheticJournalReason.FileDelete, 1, 1001),
            Record(directory.RecordKey.RecordNumber, 5, "repository", SyntheticJournalReason.FileDelete, 1, 1002),
            Record(directory.RecordKey.RecordNumber, 5, "replacement", SyntheticJournalReason.FileCreate, 2, 1003)
        ], new SyntheticJournalCursor(PackedIndexFixture.JournalIdentifier, 1003));

        Assert.That(directory.IsDeleted, Is.True, "The retained handle is superseded by the replacement directory.");
        Assert.Throws<InvalidOperationException>(() => _ = directory.IsDirectory);
        var scope = new GitWizardApi.CandidateScope(
            WatchPath.Parse(files.TreePath), false, false, new CandidateAdmissionPolicy([], true, false),
            WatchPath.Parse(files.TreePath));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(GitWizardApi.TryReadIsDirectory(directory, out _), Is.False);
            Assert.That(GitWizardApi.TryGetScopedParent(directory, scope, out _, out _), Is.False);
            Assert.That(GitWizardApi.SelectGitEntries(index, includeGitFiles: false), Is.Empty);
        }
    }

    static SyntheticJournalRecord Record(
        ulong recordNumber, ulong parentNumber, string name, SyntheticJournalReason reason, ushort sequence, long usn) =>
        new()
        {
            RecordNumber = recordNumber,
            ParentRecordNumber = parentNumber,
            SequenceNumber = sequence,
            FileName = name,
            Reason = reason | SyntheticJournalReason.Close,
            FileAttributes = FileAttributes.Directory,
            Timestamp = PackedIndexFixture.CompletionTimestamp,
            Usn = usn
        };
}

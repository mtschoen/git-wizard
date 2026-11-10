using System.Collections.Concurrent;
using GitWizard;
using GitWizardTests.TestSupport;
using MFTLib.Index;
using MFTLibTestExtensions;

namespace GitWizardTests.Discovery;

[TestFixture]
[Platform("Win")]
[NonParallelizable]
public class RepositorySizeCalculatorLimitTests
{
    [Test, NonParallelizable]
    public void UnknownSizeFileLimit_Issue485_DefaultAllowsMeasuredStores()
    {
        // Issue 485: six pnpm and uv stores with 1018 to 5249 unknown-size files walked at 1000.
        Assert.That(RepositorySizeCalculator.UnknownSizeFileLimit, Is.EqualTo(10000));
    }

    [TestCase(2, 1, 70, "1 completed by reading 2 unknown-size file(s) from disk); 0 walked.",
        TestName = "UnknownSize_Issue485_ExactlyAtTheLimit_CompletesFromTheIndex")]
    [TestCase(3, 0, 0, "1 walked (1 more unknown-size files than the 2 read limit (3 file(s)))",
        TestName = "UnknownSize_Issue485_AboveTheLimit_LeavesTheRepositoryToWalk")]
    [NonParallelizable]
    public async Task UnknownSize_LimitBoundary(int unknownFileCount, int expectedCount, long expectedSize,
        string expectedLog)
    {
        using var files = new PackedIndexFixture();
        var repository = Path.Combine(files.TreePath, "repo");
        var rows = new List<SyntheticRow>
        {
            DirectoryRow(5, "root", 5),
            DirectoryRow(6, "repo", 5),
            new(7, "known.bin", 6) { Size = 50 }
        };
        rows.AddRange(Enumerable.Range(0, unknownFileCount).Select(position =>
            new SyntheticRow((uint)(8 + position), $"unknown-{position}.bin", 6) { Size = null }));
        var producer = new SyntheticMftProducer(_ => rows)
        {
            JournalCursor = new SyntheticJournalCursor(PackedIndexFixture.JournalIdentifier, PackedIndexFixture.NextUsn),
            ScanTimestamp = PackedIndexFixture.CompletionTimestamp
        };
        await using var index = await files.OpenMftIndexAsync(SyntheticIndexSource.Create(producer), 'X',
            RootKey.FromDirectory(files.TreePath));
        using var sandbox = new LocalFilesSandbox();
        var messages = new ConcurrentQueue<string>();
        var previousLogMethod = GitWizardLog.LogMethod;
        var previousSilentMode = GitWizardLog.SilentMode;
        var previousReader = RepositorySizeCalculator.ReadFileLength;
        var previousLimit = RepositorySizeCalculator.UnknownSizeFileLimit;
        try
        {
            GitWizardLog.LogMethod = message => messages.Enqueue(message ?? string.Empty);
            GitWizardLog.SilentMode = false;
            RepositorySizeCalculator.ReadFileLength = _ => 10;
            RepositorySizeCalculator.UnknownSizeFileLimit = 2;

            var sizes = RepositorySizeCalculator.Compute(new FileIndexSet([index]), [repository],
                CancellationToken.None);

            var log = string.Join(Environment.NewLine, messages);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(sizes, Has.Count.EqualTo(expectedCount));
                Assert.That(sizes.GetValueOrDefault(repository), Is.EqualTo(expectedSize));
                Assert.That(log, Does.Contain(expectedLog));
            }
        }
        finally
        {
            RepositorySizeCalculator.UnknownSizeFileLimit = previousLimit;
            RepositorySizeCalculator.ReadFileLength = previousReader;
            GitWizardLog.LogMethod = previousLogMethod;
            GitWizardLog.SilentMode = previousSilentMode;
            GitWizardLog.CloseCurrentLogFile();
        }
    }

    static SyntheticRow DirectoryRow(uint row, string name, uint parent) => new(row, name, parent)
    {
        IsDirectory = true,
        Attributes = FileAttributes.Directory,
        LastWriteTime = PackedIndexFixture.CompletionTimestamp,
        SequenceNumber = 1
    };
}

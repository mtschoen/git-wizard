using MFTLib.Index;
using MFTLibTestExtensions;

namespace GitWizardTests.TestSupport;

static class PackedRepositoryRows
{
    /// <summary>Opens drive X over a block holding the root row (row 5) plus <paramref name="rows"/>.</summary>
    public static Task<FileIndex> OpenRowsAsync(PackedIndexFixture files, params SyntheticRow[] rows) =>
        FileIndex.OpenAsync(new FileIndexOptions
        {
            Root = SyntheticIndexedRoot.Create(new VolumeKey('X'), files.TreePath, 1),
            CacheMode = CacheMode.Disabled,
            MftSource = SyntheticIndexSource.Create(new SyntheticMftProducer(_ => [Directory(5, "root", 5), .. rows])
            {
                ScanTimestamp = PackedIndexFixture.CompletionTimestamp
            })
        }, CancellationToken.None);

    public static SyntheticRow Directory(uint row, string name, uint parent) =>
        new(row, name, parent) { IsDirectory = true };

    public static SyntheticRow File(uint row, string name, uint parent) => new(row, name, parent);
}

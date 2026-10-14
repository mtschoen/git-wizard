using GitWizard;
using MFTLib.Index;
using MFTLibTestExtensions;

namespace GitWizardTests.TestSupport;

internal sealed class IndexCacheFixture : IDisposable
{
    readonly PackedIndexFixture _files = new();
    public string Home { get; }
    public string Cache { get; }
    public string OwnedPath => SyntheticBlock.CachedPath(Cache, 'X', 1);
    public string FreePath => SyntheticBlock.CachedPath(Cache, 'Y', 1);

    public IndexCacheFixture(string? home = null)
    {
        Home = home ?? _files.DirectoryPath;
        Cache = MftIndexCachePolicy.GetCacheDirectory(Home);
        Directory.CreateDirectory(Cache);
        File.WriteAllText(FreePath, "invalid but deletable canonical block");
        File.WriteAllText(Path.Combine(Cache, "unrelated.txt"), "keep");
    }

    public Task<FileIndex> OpenOwnerAsync() => FileIndex.OpenAsync(new FileIndexOptions
    {
        Drives = [new IndexedDrive('X', _files.TreePath, 1, ProducerPolicy.Enumeration)],
        CacheDirectory = Cache
    }, CancellationToken.None);

    public void Dispose() => _files.Dispose();
}

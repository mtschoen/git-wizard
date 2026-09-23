using GitWizard;
using MFTLib.Index;

namespace GitWizardTests.TestSupport;

internal sealed class IndexCacheFixture : IDisposable
{
    readonly PackedIndexFixture _files = new();
    public string Home { get; }
    public string Cache { get; }
    public string OwnedPath => Path.Combine(Cache, CacheDirectory.BlockFileName('X', 1));
    public string FreePath => Path.Combine(Cache, CacheDirectory.BlockFileName('Y', 1));

    public IndexCacheFixture(string? home = null)
    {
        Home = home ?? _files.DirectoryPath;
        Cache = MftIndexCachePolicy.Current.GetCacheDirectory(Home);
        Directory.CreateDirectory(Cache);
        File.WriteAllText(FreePath, "invalid but deletable canonical block");
        File.WriteAllText(Path.Combine(Cache, "unrelated.txt"), "keep");
    }

    public Task<FileIndex> OpenOwnerAsync() => FileIndex.OpenAsync(new FileIndexOptions
    {
        Drives = [new IndexedDrive('X', _files.TreePath, 1)],
        CacheDirectory = Cache,
        NoCache = false,
        ProducerPolicy = ProducerPolicy.Enumeration
    }, CancellationToken.None);

    public void Dispose() => _files.Dispose();
}

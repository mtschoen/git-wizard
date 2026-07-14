using GitWizard;
using MFTLib;

namespace GitWizardTests;

/// <summary>
/// Covers the not-elevated MFT discovery path after its migration onto MFTLib's
/// journal broker: <see cref="GitWizardApi.TryFindAllRepositoriesUsingMftAsync"/> spawns
/// one elevated broker, takes a cold MFT scan, and pulls repository roots out of the
/// returned <see cref="ScanRecord"/>s - replacing the old <c>--elevated-mft</c> temp-file
/// roundtrip. The broker is injected as a scan seam so the whole flow is exercised without
/// real elevation; the filesystem checks are real, so these tests are Windows-only.
/// </summary>
public class BrokerDiscoveryTests
{
    sealed class FakeElevationProvider : IElevationProvider
    {
        public bool Elevated;
        public bool IsElevated() => Elevated;
        public bool CanSelfElevate() => true;
        public bool TryRunElevated(string arguments, int timeoutMs = 60000) => false;
    }

    [SetUp]
    public void SetUp() => GitWizardLog.SilentMode = true;

    static string CreateRepoTree(out string repoA, out string repoB)
    {
        var root = Path.Combine(Path.GetTempPath(), $"gw-broker-{Guid.NewGuid():N}");
        repoA = Path.Combine(root, "repoA");
        repoB = Path.Combine(root, "nested", "repoB");
        Directory.CreateDirectory(Path.Combine(repoA, ".git"));
        Directory.CreateDirectory(Path.Combine(repoB, ".git"));
        return root;
    }

    static ScanRecord GitDirRecord(string repoPath) =>
        new(0, 0, 0, 0, 0, IsDirectory: true, Name: ".git", Path: Path.Combine(repoPath, ".git"));

    [Test]
    [Platform("Win")]
    public async Task TryFindAllRepositoriesUsingMftAsync_NotElevated_FindsGitReposFromBrokerScan()
    {
        var root = CreateRepoTree(out var repoA, out var repoB);
        try
        {
            var configuration = new GitWizardConfiguration();
            configuration.SearchPaths.Add(root);
            var paths = new SortedSet<string>();

            var scanned = new List<ScanRecord> { GitDirRecord(repoA), GitDirRecord(repoB) };

            var result = await GitWizardApi.TryFindAllRepositoriesUsingMftAsync(
                configuration, paths,
                elevation: new FakeElevationProvider { Elevated = false },
                scanProvider: _ => Task.FromResult<IReadOnlyList<ScanRecord>>(scanned));

            Assert.That(result, Is.True);
            Assert.That(paths, Has.Count.EqualTo(2));
            // NormalizePath lower-cases, so match case-insensitively.
            Assert.That(paths, Has.Some.EndsWith("repoA").IgnoreCase);
            Assert.That(paths, Has.Some.EndsWith("repoB").IgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    [Platform("Win")]
    public async Task TryFindAllRepositoriesUsingMftAsync_NotElevated_BrokerDeclined_ReturnsFalse()
    {
        var root = CreateRepoTree(out _, out _);
        try
        {
            var configuration = new GitWizardConfiguration();
            configuration.SearchPaths.Add(root);
            var paths = new SortedSet<string>();

            // A declined UAC / failed spawn surfaces as InvalidOperationException from the broker;
            // discovery must swallow it and report failure so the caller falls back to a scan.
            var result = await GitWizardApi.TryFindAllRepositoriesUsingMftAsync(
                configuration, paths,
                elevation: new FakeElevationProvider { Elevated = false },
                scanProvider: _ => throw new InvalidOperationException("UAC declined"));

            Assert.That(result, Is.False);
            Assert.That(paths, Is.Empty);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task TryFindAllRepositoriesUsingMftAsync_NoMft_ReturnsFalseWithoutScanning()
    {
        var configuration = new GitWizardConfiguration();
        configuration.SearchPaths.Add(Path.GetTempPath());
        var paths = new SortedSet<string>();
        var scanned = false;

        var result = await GitWizardApi.TryFindAllRepositoriesUsingMftAsync(
            configuration, paths, noMft: true,
            elevation: new FakeElevationProvider { Elevated = false },
            scanProvider: _ => { scanned = true; return Task.FromResult<IReadOnlyList<ScanRecord>>([]); });

        Assert.That(result, Is.False);
        Assert.That(scanned, Is.False);
    }
}

using GitWizard;
using GitWizardTests.TestSupport;

namespace GitWizardTests.Discovery;

[TestFixture]
[NonParallelizable]
public class ConfiguredSearchPathsOnlyTests
{
    [TestCase(true)]
    [TestCase(false)]
    public async Task InventoryCrawlsExactlyTheConfiguredSearchPaths(bool homeIsSet)
    {
        using var searchRoot = new TempDirectory("gw-search-root-");
        using var profile = new TempDirectory("gw-fake-profile-");
        Directory.CreateDirectory(Path.Combine(profile.Path, ".pr-crew", "repos"));
        var crawledSearchPaths = new List<string>();
        var defaults = RepositoryDiscoveryDependencies.CreateDefault();
        var coordinator = new RepositoryDiscoveryCoordinator(new RepositoryDiscoveryDependencies
        {
            IsWindows = false,
            IsLinux = false,
            CrawlRootAsync = (request, _, _) =>
            {
                crawledSearchPaths.Clear();
                crawledSearchPaths.AddRange(request.Configuration.SearchPaths);
                return Task.FromResult(new RecursiveDiscovery([], [], new Dictionary<string, string>()));
            },
            ReadListAsync = _ => Task.FromResult<string[]?>(null),
            WriteListAsync = (_, _) => Task.CompletedTask,
            CandidateExists = defaults.CandidateExists,
            CreateLinuxSource = defaults.CreateLinuxSource
        });
        var configuration = new GitWizardConfiguration { SearchPaths = [searchRoot.Path] };

        using (UseProfile(profile.Path))
        using (homeIsSet ? new LocalFilesSandbox() : (IDisposable)ClearedHome())
        {
            await using var result = await GitWizardApi.DiscoverAllCloneInventoryAsync(configuration, null, true, coordinator);
        }

        Assert.That(crawledSearchPaths, Is.EqualTo([searchRoot.Path]));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void DefaultConfigurationNamesNoCloneFolderEvenWhenOneExists(bool homeIsSet)
    {
        using var profile = new TempDirectory("gw-fake-profile-");
        Directory.CreateDirectory(Path.Combine(profile.Path, ".pr-crew", "repos"));

        GitWizardConfiguration configuration;
        using (UseProfile(profile.Path))
        using (homeIsSet ? new LocalFilesSandbox() : (IDisposable)ClearedHome())
        {
            configuration = GitWizardConfiguration.CreateDefaultConfiguration();
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(configuration.SearchPaths, Has.Count.EqualTo(1));
            Assert.That(configuration.SearchPaths, Has.None.Contain("pr-crew"));
        }
    }

    // On Linux the user profile is read from HOME, so the directory under the fake profile is what a
    // built-in lookup would find; on Windows the profile comes from the OS and the real one is used.
    static EnvironmentScope UseProfile(string profile) => new EnvironmentScope("HOME", profile);

    static EnvironmentScope ClearedHome() => new EnvironmentScope("GITWIZARD_HOME", null);

    sealed class EnvironmentScope : IDisposable
    {
        readonly string _name;
        readonly string? _previous;

        public EnvironmentScope(string name, string? value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }
}

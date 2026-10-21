using GitWizard;
using GitWizardUI.ViewModels;

namespace GitWizardTests;

// Refresh tests are split by concern because the combined fixture exceeds 500 lines.
// Covers MainViewModel.Refresh.cs (RefreshAsync, HardRefreshAsync).
public class MainViewModelRefreshLifecycleTests
{
    [SetUp]
    public void SetUp() => GitWizardLog.SilentMode = true;

    static MainViewModel CreateViewModel() =>
        new(new StubUiDispatcher(), new StubUserDialogs(), new StubClipboardService());


    [Test]
    public async Task HardRefreshAsync_ReturnsImmediately_WhenAlreadyRefreshing()
    {
        var vm = CreateViewModel();
        vm.IsRefreshing = true;

        // HardRefreshAsync must return without side effects.
        await vm.HardRefreshAsync();

        Assert.That(vm.IsRefreshing, Is.True,
            "HardRefreshAsync must be a no-op when already refreshing.");
    }

    [Test]
    public async Task HardRefreshAsync_UnusableDiscovery_PreservesCachedList()
    {
        var home = TestUtilities.RedirectLocalFilesToTemp();
        TestUtilities.ResetStaticCaches();
        try
        {
            var searchRoot = Directory.CreateDirectory(Path.Combine(home, "search")).FullName;
            var cachedRepo = GitWizardApi.NormalizePath(Path.Combine(searchRoot, "cached-repo"));
            await GitWizardApi.SaveCachedRepositoryPathsAsync([cachedRepo]);
            var configuration = await GitWizardConfiguration.GetGlobalConfigurationAsync();
            configuration.SearchPaths.Add(searchRoot);

            var coordinator = new RepositoryDiscoveryCoordinator(new RepositoryDiscoveryDependencies
            {
                IsWindows = true,
                IsLinux = false,
                ClassifyRoot = root => new RootClassification(root, RootFileSystem.Recursive, null, null),
                CrawlRootAsync = (_, root, _) => Task.FromResult(new RecursiveDiscovery(
                    [], [root], new Dictionary<string, string> { [root] = "access denied" })),
                OpenIndexAsync = (_, _, _, _) => throw new AssertionException("Recursive roots must not open an index."),
                ReadListAsync = token => GitWizardApi.GetCachedRepositoryPathsAsync(token),
                WriteListAsync = (paths, token) => GitWizardApi.SaveCachedRepositoryPathsAsync(paths, token),
                CandidateExists = RepositoryCandidateProbe.Exists,
                CreateLinuxSource = () => throw new PlatformNotSupportedException()
            });

            var vm = CreateViewModel();
            vm.RefreshDiscoveryCoordinator = coordinator;

            await vm.HardRefreshAsync();

            var cached = await GitWizardApi.GetCachedRepositoryPathsAsync();
            Assert.Multiple(() =>
            {
                Assert.That(vm.IsRefreshing, Is.False);
                Assert.That(cached, Is.Not.Null, "An unusable rebuild must preserve the cached list.");
                Assert.That(cached, Does.Contain(cachedRepo).IgnoreCase);
                Assert.That(vm.HeaderText, Does.Contain("discovery incomplete").IgnoreCase,
                    "The incomplete outcome must be visible, not silently swallowed.");
            });
        }
        finally
        {
            TestUtilities.ClearLocalFilesRedirect(home);
            TestUtilities.ResetStaticCaches();
        }
    }

    [Test]
    public async Task RefreshAsync_ReturnsImmediately_WhenAlreadyRefreshing()
    {
        var vm = CreateViewModel();
        vm.IsRefreshing = true;

        // Should return immediately, not start a second refresh.
        await vm.RefreshAsync();

        Assert.That(vm.IsRefreshing, Is.True,
            "RefreshAsync must be a no-op when already refreshing.");
    }
}

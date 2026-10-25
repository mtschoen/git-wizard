using GitWizard;
using GitWizardTests.TestSupport;
using GitWizardUI.ViewModels;

namespace GitWizardTests;

// Refresh tests are split by concern because the combined fixture exceeds 500 lines.
// Covers RefreshCoordinator.cs (RefreshAsync, HardRefreshAsync) and StatusHeaderViewModel.cs.
public class MainViewModelRefreshLifecycleTests
{
    [SetUp]
    public void SetUp() => GitWizardLog.SilentMode = true;

    static MainViewModel CreateViewModel() => TestViewModels.Create();


    [Test]
    public async Task HardRefreshAsync_ReturnsImmediately_WhenAlreadyRefreshing()
    {
        var vm = CreateViewModel();
        vm.Refresh.IsRefreshing = true;

        // HardRefreshAsync must return without side effects.
        await vm.Refresh.HardRefreshAsync();

        Assert.That(vm.Refresh.IsRefreshing, Is.True,
            "HardRefreshAsync must be a no-op when already refreshing.");
    }

    [Test]
    public async Task HardRefreshAsync_UnusableDiscovery_PreservesCachedList()
    {
        using var sandbox = new LocalFilesSandbox();
        var home = sandbox.Path;
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
            ReadListAsync = GitWizardApi.GetCachedRepositoryPathsAsync,
            WriteListAsync = GitWizardApi.SaveCachedRepositoryPathsAsync,
            CandidateExists = RepositoryCandidateProbe.Exists,
            CreateLinuxSource = () => throw new PlatformNotSupportedException()
        });

        var vm = CreateViewModel();
        vm.Refresh.DiscoveryCoordinator = coordinator;

        await vm.Refresh.HardRefreshAsync();

        var cached = await GitWizardApi.GetCachedRepositoryPathsAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(vm.Refresh.IsRefreshing, Is.False);
            Assert.That(cached, Is.Not.Null, "An unusable rebuild must preserve the cached list.");
            Assert.That(cached, Does.Contain(cachedRepo).IgnoreCase);
            Assert.That(vm.StatusHeader.HeaderText, Does.Contain("discovery incomplete").IgnoreCase,
                "The incomplete outcome must be visible, not silently swallowed.");
        }
    }

    [Test]
    public async Task RefreshAsync_ReturnsImmediately_WhenAlreadyRefreshing()
    {
        var vm = CreateViewModel();
        vm.Refresh.IsRefreshing = true;

        // Should return immediately, not start a second refresh.
        Func<bool, bool, Task> refresh = vm.Refresh.RefreshAsync;
        await refresh(false, false);

        Assert.That(vm.Refresh.IsRefreshing, Is.True,
            "RefreshAsync must be a no-op when already refreshing.");
    }
}

using System.Collections.Concurrent;
using GitWizard;
using GitWizardTests.TestSupport;
using GitWizardUI.ViewModels;
using MFTLib.Index;

namespace GitWizardTests;

// Refresh tests are split by concern because the combined fixture exceeds 500 lines.
// Covers RefreshCoordinator.cs (RefreshAsync, HardRefreshAsync) and StatusHeaderViewModel.cs.
[NonParallelizable]
public class MainViewModelRefreshLifecycleTests
{
    Action<string?> _originalLogMethod = null!;
    Action<string?, GitWizardLog.LogType>? _originalTypedLogMethod;
    bool _originalSilentMode;

    [SetUp]
    public void SetUp()
    {
        _originalLogMethod = GitWizardLog.LogMethod;
        _originalTypedLogMethod = GitWizardLog.TypedLogMethod;
        _originalSilentMode = GitWizardLog.SilentMode;
        GitWizardLog.SilentMode = true;
    }

    [TearDown]
    public void TearDown()
    {
        GitWizardLog.LogMethod = _originalLogMethod;
        GitWizardLog.TypedLogMethod = _originalTypedLogMethod;
        GitWizardLog.SilentMode = _originalSilentMode;
    }

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
            IsWindows = false,
            IsLinux = true,
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

    [TestCase(CachedBlockDeletionOutcome.Deleted)]
    [TestCase(CachedBlockDeletionOutcome.InUse)]
    [TestCase(CachedBlockDeletionOutcome.Failed)]
    public async Task RefreshAsync_CachedPaths_ReclaimsOnlyLegacyBlocks(CachedBlockDeletionOutcome outcome)
    {
        using var sandbox = new LocalFilesSandbox();
        using var cache = new IndexCacheFixture(sandbox.Path);
        cache.SeedLegacyAndUnknownLayout();
        using var repository = TempRepoFixture.CreateWithInitialCommit();
        var repositoryPath = GitWizardApi.NormalizePath(repository.Path);
        await GitWizardApi.SaveCachedRepositoryPathsAsync([repositoryPath]);
        var configuration = await GitWizardConfiguration.GetGlobalConfigurationAsync();
        configuration.SearchPaths.Clear();
        configuration.SearchPaths.Add(repository.Path);
        configuration.IncludeTempDirectories = true;
        var messages = new ConcurrentQueue<string>();
        GitWizardLog.TypedLogMethod = null;
        GitWizardLog.LogMethod = message => messages.Enqueue(message ?? string.Empty);
        GitWizardLog.SilentMode = false;
        using var legacyLock = cache.ApplyLegacyOutcome(outcome);
        try
        {
            var dispatcher = new QueuedUiDispatcher();
            await using var viewModel = TestViewModels.Create(dispatcher);
            viewModel.Refresh.DiscoveryCoordinator = IndexCacheFixture.CreateCachedOnlyCoordinator();

            var refreshTask = viewModel.Refresh.RefreshAsync();
            await AsyncCondition.WaitUntilAsync(() =>
            {
                dispatcher.Drain();
                return refreshTask.IsCompleted;
            }, TestTimeouts.HangGuard, "refresh completion");
            await refreshTask;
            dispatcher.Drain();

            cache.AssertAfterAutomaticMaintenance(outcome, string.Join(Environment.NewLine, messages));
            var errorLogText = string.Join(Environment.NewLine, viewModel.ErrorLog.Entries.Select(entry => entry.Message));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(viewModel.Refresh.IsRefreshing, Is.False);
                Assert.That(viewModel.RepositoryList.AllRepositories.Select(row => row.WorkingDirectory),
                    Does.Contain(repositoryPath).IgnoreCase);
                if (OperatingSystem.IsWindows() && outcome != CachedBlockDeletionOutcome.Deleted)
                {
                    Assert.That(viewModel.ErrorLog.Count, Is.EqualTo(1));
                    Assert.That(errorLogText, Does.Contain("Legacy index cache cleanup incomplete")
                        .And.Contain(outcome.ToString())
                        .And.Contain(cache.LegacyPath));
                    if (outcome == CachedBlockDeletionOutcome.Failed)
                    {
                        Assert.That(errorLogText, Does.Contain("Failed - "));
                    }
                }
                else
                {
                    Assert.That(viewModel.ErrorLog.Entries, Is.Empty);
                }
            }
        }
        finally
        {
            cache.RestoreLegacyAttributes();
        }
    }
}

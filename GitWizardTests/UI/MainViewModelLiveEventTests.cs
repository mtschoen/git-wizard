using System.Runtime.CompilerServices;
using GitWizard;
using GitWizard.Watch;
using GitWizardTests.TestSupport;
using GitWizardUI.ViewModels;

namespace GitWizardTests.UI;

// Live watch tests are split by concern because the combined fixture exceeds 500 lines.
// Covers MainViewModel.Live.cs (ApplyLiveEvent), RepositoryListViewModel.Rows.cs, and RepositoryListViewModel.CachedRows.cs.
public class MainViewModelLiveEventTests
{
    [SetUp]
    public void SetUp() => GitWizardLog.SilentMode = true;

    static MainViewModel NewViewModel()
        => TestViewModels.Create();

    static GitWizardRepository Repo(string workingDirectory, params string[] remotes)
    {
        var repository = new GitWizardRepository(workingDirectory);
        foreach (var url in remotes)
            repository.RemoteUrls.Add(url);
        return repository;
    }

    static Task WaitUntilAsync(Func<bool> condition, [CallerArgumentExpression(nameof(condition))] string? expression = null) =>
        AsyncCondition.WaitUntilAsync(condition, TestTimeouts.ShortHangGuard, expression);


    [Test]
    public async Task ApplyLiveEvent_Changed_RefreshesAndUpdatesTargetNode()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repository = new GitWizardRepository(fixture.Path);
        repository.Refresh();
        var viewModel = NewViewModel();
        viewModel.RepositoryList.AddRepository(repository, streaming: false);

        Assert.That(viewModel.RepositoryList.Repositories.Single().DisplayText, Does.Not.Contain("*"));

        fixture.AddUntrackedFile("untracked.txt");
        await viewModel.ApplyLiveEventAsync(new RepositoryChangeEvent(fixture.Path, RepositoryChangeKind.Changed));

        var node = viewModel.RepositoryList.Repositories.Single();
        Assert.Multiple(() =>
        {
            Assert.That(node.Repository.HasPendingChanges, Is.True);
            Assert.That(node.DisplayText, Does.Contain("*"));
        });
    }

    [Test]
    public async Task ApplyLiveEvent_Changed_UnknownRepoRoot_NoOp()
    {
        var viewModel = NewViewModel();
        viewModel.RepositoryList.AddRepository(Repo("/a"), streaming: false);

        await viewModel.ApplyLiveEventAsync(new RepositoryChangeEvent("/unknown", RepositoryChangeKind.Changed));
        Assert.That(viewModel.RepositoryList.Repositories, Has.Count.EqualTo(1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ApplyLiveEvent_Created_AddsNewRepositoryNode(bool streaming)
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        await using var viewModel = TestViewModels.Create(timeProvider: new ControlledDelayClock());
        var laterPath = fixture.Path + "-zulu";
        viewModel.RepositoryList.AddRepository(Repo(laterPath), streaming: false);
        viewModel.Refresh.IsRefreshing = streaming;
        viewModel.Refresh.IsScanning = true;
        var inserts = new PositionalInsertRecorder(viewModel.RepositoryList.Repositories);

        await viewModel.ApplyLiveEventAsync(new RepositoryChangeEvent(fixture.Path, RepositoryChangeKind.Created));

        var expectedPaths = streaming
            ? new[] { laterPath, fixture.Path }
            : new[] { fixture.Path, laterPath };
        Assert.Multiple(() =>
        {
            Assert.That(viewModel.RepositoryList.Repositories.Select(node => node.WorkingDirectory), Is.EqualTo(expectedPaths));
            Assert.That(inserts.Count, Is.EqualTo(streaming ? 0 : 1));
            Assert.That(viewModel.Refresh.IsScanning, Is.True);
        });
    }

    [Test]
    public async Task ApplyLiveEvent_Created_VanishedRepo_IsDroppedQuietly()
    {
        var viewModel = NewViewModel();
        using var missing = new TempDirectory("gw-vanished-", create: false);
        var vanished = missing.Path;

        await viewModel.ApplyLiveEventAsync(new RepositoryChangeEvent(vanished, RepositoryChangeKind.Created));


        Assert.That(viewModel.RepositoryList.Repositories, Is.Empty);
    }

    [Test]
    public async Task ApplyLiveEvent_DeletedUncorrelated_RemovesNodeAfterCorrelationWindow()
    {
        var viewModel = NewViewModel();
        viewModel.RepositoryList.AddRepository(Repo("/a"), streaming: false);

        await viewModel.ApplyLiveEventAsync(new RepositoryChangeEvent("/a", RepositoryChangeKind.Deleted));

        // Buffered, not removed immediately - a matching Created could still turn this into a rename.
        Assert.That(viewModel.RepositoryList.Repositories, Has.Count.EqualTo(1));

        await WaitUntilAsync(() => viewModel.RepositoryList.Repositories.Count == 0);
    }

    [Test]
    public async Task ApplyLiveEvent_Deleted_UnknownRepoRoot_NoOp()
    {
        var viewModel = NewViewModel();
        viewModel.RepositoryList.AddRepository(Repo("/a"), streaming: false);

        await viewModel.ApplyLiveEventAsync(new RepositoryChangeEvent("/unknown", RepositoryChangeKind.Deleted));
        Assert.That(viewModel.RepositoryList.Repositories, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task ApplyLiveEvent_DeletedThenCreatedWithMatchingRemote_CorrelatesAsRename()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddOriginRemoteAndPush();
        var refreshed = new GitWizardRepository(fixture.Path);
        refreshed.Refresh();
        var remoteUrl = refreshed.RemoteUrls.Single();

        var viewModel = NewViewModel();
        viewModel.RepositoryList.AddRepository(Repo("/old/path", remoteUrl), streaming: false);

        await viewModel.ApplyLiveEventAsync(new RepositoryChangeEvent("/old/path", RepositoryChangeKind.Deleted));
        await viewModel.ApplyLiveEventAsync(new RepositoryChangeEvent(fixture.Path, RepositoryChangeKind.Created));

        var paths = viewModel.RepositoryList.Repositories.Select(node => node.WorkingDirectory).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(paths, Does.Not.Contain("/old/path"));
            Assert.That(paths, Does.Contain(fixture.Path));
            Assert.That(viewModel.RepositoryList.Repositories, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task ApplyLiveEvent_Deleted_SecondDeleteOverwritesAndCancelsPreviousExpiry()
    {
        var viewModel = NewViewModel();
        viewModel.RepositoryList.AddRepository(Repo("/a"), streaming: false);

        await viewModel.ApplyLiveEventAsync(new RepositoryChangeEvent("/a", RepositoryChangeKind.Deleted));
        await viewModel.ApplyLiveEventAsync(new RepositoryChangeEvent("/a", RepositoryChangeKind.Deleted));

        Assert.That(viewModel.RepositoryList.Repositories, Has.Count.EqualTo(1));
        await WaitUntilAsync(() => viewModel.RepositoryList.Repositories.Count == 0);
    }

    [Test]
    public async Task ApplyLiveEvent_DeletedThenCreatedSamePath_CancelsExpiryAndRetainsNode()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var viewModel = NewViewModel();
        viewModel.RepositoryList.AddRepository(new GitWizardRepository(fixture.Path), streaming: false);
        viewModel.RepositoryList.AddRepository(Repo("/marker"), streaming: false);

        await viewModel.ApplyLiveEventAsync(new RepositoryChangeEvent(fixture.Path, RepositoryChangeKind.Deleted));
        await viewModel.ApplyLiveEventAsync(new RepositoryChangeEvent(fixture.Path, RepositoryChangeKind.Created));
        await viewModel.ApplyLiveEventAsync(new RepositoryChangeEvent("/marker", RepositoryChangeKind.Deleted));

        // When the marker repository's correlation window expires and it is removed,
        // the earlier correlation window for the recreated repository has also elapsed.
        await WaitUntilAsync(() => viewModel.RepositoryList.Repositories.All(node => node.WorkingDirectory != "/marker"));

        Assert.That(viewModel.RepositoryList.Repositories, Has.Count.EqualTo(1));
        Assert.That(viewModel.RepositoryList.Repositories.Single().WorkingDirectory, Is.EqualTo(fixture.Path));
    }

    [Test]
    public async Task ApplyLiveEvent_DeletedThenCreatedThenDeletedSamePath_RemovesNode()
    {
        var dispatcher = new QueuedUiDispatcher();
        var viewModel = TestViewModels.Create(dispatcher: dispatcher);
        viewModel.RepositoryList.AddRepository(Repo("/a"), streaming: false);

        await viewModel.ApplyLiveEventAsync(new RepositoryChangeEvent("/a", RepositoryChangeKind.Deleted));
        var createdTask = viewModel.ApplyLiveEventAsync(new RepositoryChangeEvent("/a", RepositoryChangeKind.Created));
        await viewModel.ApplyLiveEventAsync(new RepositoryChangeEvent("/a", RepositoryChangeKind.Deleted));
        await createdTask;

        dispatcher.Drain();
        await WaitUntilAsync(() =>
        {
            dispatcher.Drain();
            return viewModel.RepositoryList.Repositories.Count == 0;
        });

        Assert.That(viewModel.RepositoryList.Repositories, Is.Empty);
    }

    [Test]
    public async Task ApplyLiveEvent_DeletedThenCreatedThenDeletedSamePath_Direct_RemovesNode()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var viewModel = NewViewModel();
        viewModel.RepositoryList.AddRepository(new GitWizardRepository(fixture.Path), streaming: false);

        await viewModel.ApplyLiveEventAsync(new RepositoryChangeEvent(fixture.Path, RepositoryChangeKind.Deleted));
        var createdTask = viewModel.ApplyLiveEventAsync(new RepositoryChangeEvent(fixture.Path, RepositoryChangeKind.Created));
        await viewModel.ApplyLiveEventAsync(new RepositoryChangeEvent(fixture.Path, RepositoryChangeKind.Deleted));
        await createdTask;

        await WaitUntilAsync(() => viewModel.RepositoryList.Repositories.Count == 0);
        Assert.That(viewModel.RepositoryList.Repositories, Is.Empty);
    }

    [Test]
    public async Task ApplyLiveEvent_CreatedApplicationAfterNewerDeleteExpires_DoesNotRestoreNode()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var dispatcher = new QueuedUiDispatcher();
        var viewModel = TestViewModels.Create(dispatcher: dispatcher);
        viewModel.RepositoryList.AddRepository(Repo(fixture.Path), streaming: false);

        await viewModel.ApplyLiveEventAsync(new RepositoryChangeEvent(fixture.Path, RepositoryChangeKind.Deleted));
        dispatcher.Drain();
        await viewModel.ApplyLiveEventAsync(new RepositoryChangeEvent(fixture.Path, RepositoryChangeKind.Created));
        // Hold the final UI application, the same boundary reached by a slow refresh.
        var applyCreated = dispatcher.TakeNext();
        await viewModel.ApplyLiveEventAsync(new RepositoryChangeEvent(fixture.Path, RepositoryChangeKind.Deleted));
        dispatcher.Drain();
        await WaitUntilAsync(() =>
        {
            dispatcher.Drain();
            return viewModel.RepositoryList.Repositories.Count == 0;
        });

        applyCreated();
        Assert.That(viewModel.RepositoryList.Repositories, Is.Empty);

        await viewModel.ApplyLiveEventAsync(new RepositoryChangeEvent(fixture.Path, RepositoryChangeKind.Created));
        dispatcher.Drain();
        Assert.That(viewModel.RepositoryList.Repositories.Single().WorkingDirectory, Is.EqualTo(fixture.Path),
            "A later genuine recreation must still be accepted.");
    }

    [Test]
    public async Task ApplyLiveEvent_OlderCreatedApplication_DoesNotReplaceNewerCreation()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var dispatcher = new QueuedUiDispatcher();
        var viewModel = TestViewModels.Create(dispatcher: dispatcher);

        await viewModel.ApplyLiveEventAsync(new RepositoryChangeEvent(fixture.Path, RepositoryChangeKind.Created));
        var applyOlderCreation = dispatcher.TakeNext();
        await viewModel.ApplyLiveEventAsync(new RepositoryChangeEvent(fixture.Path, RepositoryChangeKind.Created));

        applyOlderCreation();
        Assert.That(viewModel.RepositoryList.Repositories, Is.Empty);
        dispatcher.Drain();
        Assert.That(viewModel.RepositoryList.Repositories.Single().WorkingDirectory, Is.EqualTo(fixture.Path));
    }

    [Test]
    public async Task ApplyLiveEvent_Changed_ConcurrentEvents_HandledCleanly()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repository = new GitWizardRepository(fixture.Path);
        repository.Refresh();
        var viewModel = NewViewModel();
        viewModel.RepositoryList.AddRepository(repository, streaming: false);

        fixture.AddUntrackedFile("untracked.txt");
        var event1 = viewModel.ApplyLiveEventAsync(new RepositoryChangeEvent(fixture.Path, RepositoryChangeKind.Changed));
        var event2 = viewModel.ApplyLiveEventAsync(new RepositoryChangeEvent(fixture.Path, RepositoryChangeKind.Changed));
        await Task.WhenAll(event1, event2);

        var node = viewModel.RepositoryList.Repositories.Single();
        Assert.That(node.Repository.HasPendingChanges, Is.True);
    }

    [Test]
    public async Task ApplyLiveEvent_Renamed_IsNoOp()
    {
        var viewModel = NewViewModel();

        await viewModel.ApplyLiveEventAsync(new RepositoryChangeEvent("/a", RepositoryChangeKind.Renamed, "/b"));
        Assert.That(viewModel.RepositoryList.Repositories, Is.Empty);
    }
}

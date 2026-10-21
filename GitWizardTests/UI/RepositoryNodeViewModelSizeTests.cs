using System.Runtime.CompilerServices;
using System.Text.Json;
using GitWizard;
using GitWizardTests.TestSupport;
using GitWizardUI.ViewModels;

namespace GitWizardTests;

public class RepositoryNodeViewModelSizeTests
{
    [Test]
    public void SizeText_BeforeFirstRefresh_IsEmpty()
    {
        var node = new RepositoryNodeViewModel(new GitWizardRepository(string.Empty));

        Assert.That(node.SizeText, Is.EqualTo(string.Empty));
    }

    [TestCase(0L, "0.0 KB")]
    [TestCase(512L, "0.5 KB")]
    [TestCase(1536L, "1.5 KB")]
    [TestCase(1572864L, "1.5 MB")]
    [TestCase(1610612736L, "1.5 GB")]
    [SetCulture("en-US")]
    public void SizeText_MeasuredRepository_UsesOneDecimal(long size, string expected)
    {
        var node = CreateMeasuredNode(size);

        Assert.That(node.SizeText, Is.EqualTo(expected));
    }

    [Test]
    [SetCulture("en-US")]
    public void SizeText_GroupHeader_SumsMeasuredChildren()
    {
        var header = RepositoryNodeViewModel.CreateGroupHeader("Repositories");
        header.Children.Add(CreateMeasuredNode(524288));
        header.Children.Add(CreateMeasuredNode(1048576));
        header.Children.Add(new RepositoryNodeViewModel(new GitWizardRepository(string.Empty)));

        Assert.That(header.SizeText, Is.EqualTo("1.5 MB"));
    }

    [Test]
    public void SizeText_GroupHeaderWithoutMeasurements_IsEmpty()
    {
        var header = RepositoryNodeViewModel.CreateGroupHeader("Repositories");
        header.Children.Add(new RepositoryNodeViewModel(new GitWizardRepository(string.Empty)));

        Assert.That(header.SizeText, Is.EqualTo(string.Empty));
    }

    [Test]
    public void UpdateDisplayText_NotifiesSizeTextForRepositoryAndGroup()
    {
        var node = CreateMeasuredNode(1024);
        var header = RepositoryNodeViewModel.CreateGroupHeader("Repositories");
        header.Children.Add(node);
        var changedProperties = new List<string?>();
        node.PropertyChanged += (_, eventArguments) => changedProperties.Add(eventArguments.PropertyName);
        header.PropertyChanged += (_, eventArguments) => changedProperties.Add(eventArguments.PropertyName);

        node.Update();
        header.UpdateDisplayText();

        Assert.That(changedProperties.Count(property => property == "SizeText"), Is.EqualTo(2));
    }

    [Test]
    [SetCulture("en-US")]
    public void RefreshCompleted_RepositoryInMultipleRemoteGroups_NotifiesEverySizeTotal()
    {
        var viewModel = TestViewModels.Create();
        var repository = new GitWizardRepository("/repositories/shared");
        repository.RemoteUrls.AddRange(["https://example.com/first", "https://example.com/second"]);
        viewModel.AddRepository(repository);
        foreach (var remote in repository.RemoteUrls)
        {
            var sibling = new GitWizardRepository("/repositories/" + remote.Split('/').Last());
            sibling.RemoteUrls.Add(remote);
            viewModel.AddRepository(sibling);
        }
        viewModel.ToggleGroupMode(GroupMode.RemoteUrl);
        var headers = viewModel.Repositories.Where(node => node.IsGroupHeader).ToArray();
        Assert.That(headers, Has.Length.EqualTo(2));
        var notifiedHeaders = new HashSet<RepositoryNodeViewModel>();
        foreach (var header in headers)
            header.PropertyChanged += (_, eventArguments) =>
            {
                if (eventArguments.PropertyName == nameof(RepositoryNodeViewModel.SizeText))
                    notifiedHeaders.Add(header);
            };

        repository.SizeOnDisk = 1572864L;
        viewModel.UpdateCompletedRepository(repository);

        Assert.That(notifiedHeaders, Is.EquivalentTo(headers));
        Assert.That(headers.Select(header => header.SizeText), Is.All.EqualTo("1.5 MB"));
    }

    [Test]
    public void UncomputedSize_PreservesNumericReportContract()
    {
        var repository = new GitWizardRepository(string.Empty);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(repository));

        Assert.That(document.RootElement.GetProperty(nameof(GitWizardRepository.SizeOnDisk)).ValueKind,
            Is.EqualTo(JsonValueKind.Number));
        Assert.That(repository.SizeOnDisk, Is.Zero);
        Assert.That(document.RootElement.TryGetProperty("HasComputedSizeOnDisk", out _), Is.False);
    }

    [Test]
    [SetCulture("en-US")]
    public void SizeText_GroupHeader_RepeatedReads_DoNotReallocate()
    {
        var header = RepositoryNodeViewModel.CreateGroupHeader("Repositories");
        for (var index = 0; index < 1000; index++)
            header.Children.Add(CreateMeasuredNode(1048576));

        header.UpdateDisplayText();

        var initialText = header.SizeText;
        Assert.That(initialText, Is.EqualTo("1000.0 MB"));

        string? lastText = null;
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1000; index++)
        {
            lastText = header.SizeText;
        }
        var allocatedAfter = GC.GetAllocatedBytesForCurrentThread();

        Assert.That(lastText, Is.SameAs(initialText));
        Assert.That(allocatedAfter - allocatedBefore, Is.Zero);
    }

    [Test]
    [SetCulture("en-US")]
    public void SizeText_GroupHeader_FirstEvaluation_DoesNotAllocateChildArrays()
    {
        var header = RepositoryNodeViewModel.CreateGroupHeader("Repositories");
        for (var index = 0; index < 4000; index++)
            header.Children.Add(CreateMeasuredNode(1048576));

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var text = header.SizeText;
        var allocatedAfter = GC.GetAllocatedBytesForCurrentThread();

        Assert.That(text, Is.EqualTo("3.9 GB"));
        Assert.That(allocatedAfter - allocatedBefore, Is.LessThan(1024));
    }

    [Test]
    [SetCulture("en-US")]
    public void SizeText_GroupHeader_InvalidatesCacheOnCollectionChange()
    {
        var header = RepositoryNodeViewModel.CreateGroupHeader("Repositories");
        header.Children.Add(CreateMeasuredNode(1048576));
        Assert.That(header.SizeText, Is.EqualTo("1.0 MB"));

        header.Children.Add(CreateMeasuredNode(1048576));
        Assert.That(header.SizeText, Is.EqualTo("2.0 MB"));

        header.Children.RemoveAt(0);
        Assert.That(header.SizeText, Is.EqualTo("1.0 MB"));
    }

    [Test]
    public void SizeText_GroupHeader_UnmeasuredChildren_StaysEmptyWithoutAllocations()
    {
        var header = RepositoryNodeViewModel.CreateGroupHeader("Repositories");
        for (var index = 0; index < 500; index++)
            header.Children.Add(new RepositoryNodeViewModel(new GitWizardRepository(string.Empty)));

        header.UpdateDisplayText();
        Assert.That(header.SizeText, Is.EqualTo(string.Empty));

        string? lastText = null;
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1000; index++)
        {
            lastText = header.SizeText;
        }
        var allocatedAfter = GC.GetAllocatedBytesForCurrentThread();

        Assert.That(lastText, Is.Empty);
        Assert.That(allocatedAfter - allocatedBefore, Is.Zero);
    }

    [Test]
    [SetCulture("en-US")]
    public void UpdateNotifications_ChildCompletions_ScaleIncrementallyWithoutAllocatingChildArrays()
    {
        var header = RepositoryNodeViewModel.CreateGroupHeader("Repositories");
        const int childCount = 2000;
        var children = new RepositoryNodeViewModel[childCount];
        for (var index = 0; index < childCount; index++)
        {
            var child = new RepositoryNodeViewModel(new GitWizardRepository($"/repositories/{index}"));
            children[index] = child;
            header.Children.Add(child);
        }

        Assert.That(header.SizeText, Is.EqualTo(string.Empty));

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < childCount; index++)
        {
            children[index].Repository.SizeOnDisk = 1048576L;
            children[index].Update();
            header.UpdateDisplayText();
        }
        var allocatedAfter = GC.GetAllocatedBytesForCurrentThread();

        Assert.That(header.SizeText, Is.EqualTo("2.0 GB"));
        Assert.That(allocatedAfter - allocatedBefore, Is.LessThan(childCount * 1024));
    }

    [Test]
    [SetCulture("en-US")]
    public void UpdateCompletedRepository_GroupHeader_UpdatesTotalsIncrementallyAcrossCompletions()
    {
        var viewModel = TestViewModels.Create();
        const int repositoryCount = 500;
        var repositories = new GitWizardRepository[repositoryCount];
        for (var index = 0; index < repositoryCount; index++)
        {
            var repository = new GitWizardRepository($"C:/repositories/repository_{index:D4}");
            repositories[index] = repository;
            viewModel.AddRepository(repository);
        }

        viewModel.ToggleGroupMode(GroupMode.Drive);
        var header = viewModel.Repositories.Single(node => node.IsGroupHeader);
        Assert.That(header.SizeText, Is.EqualTo(string.Empty));

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < repositoryCount; index++)
        {
            repositories[index].SizeOnDisk = 1048576L;
            viewModel.UpdateCompletedRepository(repositories[index]);
        }
        var allocatedAfter = GC.GetAllocatedBytesForCurrentThread();

        Assert.That(header.SizeText, Is.EqualTo("500.0 MB"));
        Assert.That(allocatedAfter - allocatedBefore, Is.LessThan(repositoryCount * 1024));
    }

    [Test]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Regrouping_DoesNotLeakAbandonedGroupHeaders()
    {
        var viewModel = TestViewModels.Create();
        var repository = new GitWizardRepository("C:/repositories/repo1");
        viewModel.AddRepository(repository);

        var weakGroupHeader = CreateAndDiscardGroupHeader(viewModel);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.That(weakGroupHeader.IsAlive, Is.False, "Abandoned group header should be garbage collected after regrouping.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference CreateAndDiscardGroupHeader(MainViewModel viewModel)
    {
        viewModel.ToggleGroupMode(GroupMode.Drive);
        var header = viewModel.Repositories.First(node => node.IsGroupHeader);
        var weakReference = new WeakReference(header);

        viewModel.ToggleGroupMode(GroupMode.None);

        return weakReference;
    }

    static RepositoryNodeViewModel CreateMeasuredNode(long size)
    {
        var repository = new GitWizardRepository(string.Empty);
        repository.SizeOnDisk = size;
        return new RepositoryNodeViewModel(repository);
    }
}

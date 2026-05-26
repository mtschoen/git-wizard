using System.Reflection;
using GitWizard;
using GitWizardUI.ViewModels;

namespace GitWizardTests;

public class RepositoryNodeViewModelAdditionalTests
{
    [SetUp]
    public void SetUp()
    {
        GitWizardLog.SilentMode = true;
    }

    // Helper to set read-only properties via reflection
    static void SetPropertyValue(object obj, string propertyName, object? value)
    {
        var prop = obj.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (prop == null)
            throw new InvalidOperationException($"Property '{propertyName}' not found on type '{obj.GetType().FullName}'.");

        prop.SetValue(obj, value);
    }

    // Helper to set fields via reflection
    static void SetField(object obj, string fieldName, object? value)
    {
        var field = obj.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (field == null)
            throw new InvalidOperationException($"Field '{fieldName}' not found on type '{obj.GetType().FullName}'.");

        field.SetValue(obj, value);
    }

    [Test]
    public void CreateGroupHeader_CreatesGroupNode()
    {
        var node = RepositoryNodeViewModel.CreateGroupHeader("/home/repos");

        Assert.That(node.IsGroupHeader, Is.True);
        Assert.That(node.GroupKey, Is.EqualTo("/home/repos"));
        Assert.That(node.IsNotGroupHeader, Is.False);
        Assert.That(node.GroupHeaderFontWeight, Is.EqualTo("Bold"));
    }

    [Test]
    public void IsGroupHeader_Node_DoesNotShowStatus()
    {
        var node = RepositoryNodeViewModel.CreateGroupHeader("/drive");
        Assert.That(node.IsStatusVisible, Is.False);
    }

    [Test]
    public void Status_Success_ReturnsCorrectIcon()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();
        var node = new RepositoryNodeViewModel(repo);

        node.Status = RefreshStatus.Success;
        Assert.That(node.StatusIcon, Is.EqualTo("\u2713"));
    }

    [Test]
    public void Status_Refreshing_ReturnsCorrectIcon()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        var node = new RepositoryNodeViewModel(repo);

        node.Status = RefreshStatus.Refreshing;
        Assert.That(node.StatusIcon, Is.EqualTo("\u27f3"));
    }

    [Test]
    public void Status_Timeout_ReturnsCorrectIcon()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        var node = new RepositoryNodeViewModel(repo);

        node.Status = RefreshStatus.Timeout;
        Assert.That(node.StatusIcon, Is.EqualTo("\u26a0"));
    }

    [Test]
    public void Status_Error_ReturnsCorrectIcon()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        var node = new RepositoryNodeViewModel(repo);

        node.Status = RefreshStatus.Error;
        Assert.That(node.StatusIcon, Is.EqualTo("\u2717"));
    }

    [Test]
    public void Status_SubmoduleIssue_ReturnsCorrectIcon()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        var node = new RepositoryNodeViewModel(repo);

        node.Status = RefreshStatus.SubmoduleIssue;
        Assert.That(node.StatusIcon, Is.EqualTo("\u2691"));
    }

    [Test]
    public void Status_Changed_FiresPropertyChanged()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        var node = new RepositoryNodeViewModel(repo);

        var firedProperties = new List<string>();
        node.PropertyChanged += (_, e) => firedProperties.Add(e.PropertyName!);

        node.Status = RefreshStatus.Success;

        Assert.That(firedProperties, Does.Contain("StatusIcon"));
        Assert.That(firedProperties, Does.Contain("StatusColorHex"));
        Assert.That(firedProperties, Does.Contain("StatusTooltip"));
        Assert.That(firedProperties, Does.Contain("IsStatusVisible"));
    }

    [Test]
    public void IsExpanded_True_ReturnsCorrectExpandIndicator()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        var node = new RepositoryNodeViewModel(repo);

        node.IsExpanded = true;
        Assert.That(node.ExpandIndicator, Is.EqualTo("\u25bc"));
    }

    [Test]
    public void IsExpanded_False_ReturnsCorrectExpandIndicator()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        var node = new RepositoryNodeViewModel(repo);

        node.IsExpanded = false;
        Assert.That(node.ExpandIndicator, Is.EqualTo("\u25b6"));
    }

    [Test]
    public void IsExpanded_Changed_FiresPropertyChanged()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        var node = new RepositoryNodeViewModel(repo);

        var fired = false;
        node.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == "IsExpanded") fired = true;
        };

        node.IsExpanded = true;
        Assert.That(fired, Is.True);
    }

    [Test]
    public void DisplayText_Changed_FiresPropertyChanged()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        var node = new RepositoryNodeViewModel(repo);

        var fired = false;
        node.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == "DisplayText") fired = true;
        };

        node.DisplayText = "Custom text";
        Assert.That(fired, Is.True);
        Assert.That(node.DisplayText, Is.EqualTo("Custom text"));
    }

    [Test]
    public void WorkingDirectory_ReturnsRepoWorkingDirectory()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        var node = new RepositoryNodeViewModel(repo);

        Assert.That(node.WorkingDirectory, Is.EqualTo(fixture.Path));
    }

    [Test]
    public void ItemPaddingString_GroupHeader_ReturnsZeroPadding()
    {
        var node = RepositoryNodeViewModel.CreateGroupHeader("/test");
        Assert.That(node.ItemPaddingString, Is.EqualTo("0,5,0,0"));
    }

    [Test]
    public void ItemPaddingString_RepositoryNode_ReturnsIndentedPadding()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        var node = new RepositoryNodeViewModel(repo);
        Assert.That(node.ItemPaddingString, Is.EqualTo("15,0,0,0"));
    }

    [Test]
    public void GroupHeaderFontWeight_GroupHeader_ReturnsBold()
    {
        var node = RepositoryNodeViewModel.CreateGroupHeader("/test");
        Assert.That(node.GroupHeaderFontWeight, Is.EqualTo("Bold"));
    }

    [Test]
    public void GroupHeaderFontWeight_RepositoryNode_ReturnsNormal()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        var node = new RepositoryNodeViewModel(repo);
        Assert.That(node.GroupHeaderFontWeight, Is.EqualTo("Normal"));
    }

    [Test]
    public void UpdateDisplayText_GroupHeader_ShowsErrorCount()
    {
        var node = RepositoryNodeViewModel.CreateGroupHeader("/group");

        var child1 = new RepositoryNodeViewModel(new GitWizardRepository("c:\\repo1"));
        child1.Status = RefreshStatus.Success;

        var child2 = new RepositoryNodeViewModel(new GitWizardRepository("c:\\repo2"));
        child2.Status = RefreshStatus.Error;

        var child3 = new RepositoryNodeViewModel(new GitWizardRepository("c:\\repo3"));
        child3.Status = RefreshStatus.Error;

        node.Children.Add(child1);
        node.Children.Add(child2);
        node.Children.Add(child3);

        node.UpdateDisplayText();

        Assert.That(node.DisplayText, Does.Contain("2 errors"));
    }

    [Test]
    public void UpdateDisplayText_GroupHeader_ShowsWarningCount()
    {
        var node = RepositoryNodeViewModel.CreateGroupHeader("/group");

        var child1 = new RepositoryNodeViewModel(new GitWizardRepository("c:\\repo1"));
        child1.Status = RefreshStatus.Timeout;

        var child2 = new RepositoryNodeViewModel(new GitWizardRepository("c:\\repo2"));
        child2.Status = RefreshStatus.Success;

        node.Children.Add(child1);
        node.Children.Add(child2);

        node.UpdateDisplayText();

        Assert.That(node.DisplayText, Does.Contain("1 warning"));
    }

    [Test]
    public void UpdateDisplayText_GroupHeader_ShowsChildCount()
    {
        var node = RepositoryNodeViewModel.CreateGroupHeader("/group");

        var child1 = new RepositoryNodeViewModel(new GitWizardRepository("c:\\repo1"));
        var child2 = new RepositoryNodeViewModel(new GitWizardRepository("c:\\repo2"));
        node.Children.Add(child1);
        node.Children.Add(child2);

        node.UpdateDisplayText();

        Assert.That(node.DisplayText, Does.Contain("(2)"));
    }

    [Test]
    public void MatchesFilter_None_ReturnsTrue()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        var node = new RepositoryNodeViewModel(repo);

        Assert.That(node.MatchesFilter(FilterType.None), Is.True);
    }

    [Test]
    public void MatchesFilter_Stale_WhenRecentRepo()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();
        var node = new RepositoryNodeViewModel(repo);

        Assert.That(node.MatchesFilter(FilterType.Stale), Is.False);
    }

    [Test]
    public void MatchesFilter_PendingChanges_WhenNoChanges()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();
        var node = new RepositoryNodeViewModel(repo);

        Assert.That(node.MatchesFilter(FilterType.PendingChanges), Is.False);
    }

    [Test]
    public void MatchesFilter_DetachedHead_WhenNotDetached()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();
        var node = new RepositoryNodeViewModel(repo);

        Assert.That(node.MatchesFilter(FilterType.DetachedHead), Is.False);
    }

    [Test]
    public void MatchesFilter_MyRepositories_WhenNullEmail()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        var node = new RepositoryNodeViewModel(repo);

        Assert.That(node.MatchesFilter(FilterType.MyRepositories, null), Is.False);
    }

    [Test]
    public void MatchesFilter_DownstreamBranches_WhenNoMergedBranches()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        repo.Branches = new List<BranchInfo> { new() { Name = "feature", IsMerged = false } };
        var node = new RepositoryNodeViewModel(repo);

        Assert.That(node.MatchesFilter(FilterType.DownstreamBranches), Is.False);
    }

    [Test]
    public void MatchesFilter_DownstreamBranches_WhenNullBranches()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        repo.Branches = null;
        var node = new RepositoryNodeViewModel(repo);

        Assert.That(node.MatchesFilter(FilterType.DownstreamBranches), Is.False);
    }

    [Test]
    public void MatchesFilter_SubmoduleConfigIssue_WhenNullSubmodules()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        SetPropertyValue(repo, "Submodules", null);
        var node = new RepositoryNodeViewModel(repo);

        Assert.That(node.MatchesFilter(FilterType.SubmoduleConfigIssue), Is.False);
    }

    [Test]
    public void StatusTooltip_Success_ShowsRefreshTime()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh();
        repo.RefreshTimeSeconds = 2.5;
        var node = new RepositoryNodeViewModel(repo);

        node.Status = RefreshStatus.Success;
        Assert.That(node.StatusTooltip, Does.Contain("2.5"));
    }

    [Test]
    public void StatusTooltip_Timeout_WhenNoErrorUsesDefault()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        repo.RefreshError = null;
        var node = new RepositoryNodeViewModel(repo);

        node.Status = RefreshStatus.Timeout;
        Assert.That(node.StatusTooltip, Is.EqualTo("Timed out"));
    }

    [Test]
    public void StatusTooltip_Timeout_WhenErrorExistsUsesError()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        repo.RefreshError = "Custom timeout error";
        var node = new RepositoryNodeViewModel(repo);

        node.Status = RefreshStatus.Timeout;
        Assert.That(node.StatusTooltip, Is.EqualTo("Custom timeout error"));
    }

    [Test]
    public void StatusTooltip_Error_WhenErrorExistsUsesError()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        repo.RefreshError = "Something broke";
        var node = new RepositoryNodeViewModel(repo);

        node.Status = RefreshStatus.Error;
        Assert.That(node.StatusTooltip, Is.EqualTo("Something broke"));
    }

    [Test]
    public void Children_CanAddAndRemove()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        var node = new RepositoryNodeViewModel(repo);

        var child1 = new RepositoryNodeViewModel(new GitWizardRepository("c:\\child1"));
        var child2 = new RepositoryNodeViewModel(new GitWizardRepository("c:\\child2"));

        node.Children.Add(child1);
        node.Children.Add(child2);

        Assert.That(node.Children, Has.Count.EqualTo(2));

        node.Children.RemoveAt(0);
        Assert.That(node.Children, Has.Count.EqualTo(1));
    }

    [Test]
    public void Repository_Property_ReturnsInjectedRepo()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        var node = new RepositoryNodeViewModel(repo);

        Assert.That(node.Repository, Is.SameAs(repo));
    }

    [Test]
    public void Update_DisplayText_WhenMergedBranches()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        repo.Branches = new List<BranchInfo>
        {
            new() { Name = "merged1", IsMerged = true },
            new() { Name = "merged2", IsMerged = true }
        };
        var node = new RepositoryNodeViewModel(repo);

        node.UpdateDisplayText();

        Assert.That(node.DisplayText, Does.Contain("[2 branches]"));
    }

    [Test]
    public void Update_DisplayText_WhenSingleMergedBranch()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var repo = new GitWizardRepository(fixture.Path);
        repo.Branches = new List<BranchInfo>
        {
            new() { Name = "merged1", IsMerged = true }
        };
        var node = new RepositoryNodeViewModel(repo);

        node.UpdateDisplayText();

        Assert.That(node.DisplayText, Does.Contain("[1 branch]"));
    }
}

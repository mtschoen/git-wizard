using GitWizard;
using GitWizardUI.ViewModels;

namespace GitWizardTests;

public class RepositoryNodeViewModelTests
{
    [Test]
    public void Update_SubmoduleIssue_MapsToDedicatedStatusNotTimeout()
    {
        GitWizardLog.SilentMode = true;
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddUninitializedSubmodule("external/libfoo");

        var repository = new GitWizardRepository(fixture.Path);
        repository.Refresh();
        Assert.That(repository.HasSubmoduleIssues, Is.True, "precondition: detection flagged the submodule");

        var node = new RepositoryNodeViewModel(repository);
        node.Update();

        Assert.That(node.Status, Is.EqualTo(RefreshStatus.SubmoduleIssue));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(node.Status, Is.Not.EqualTo(RefreshStatus.Timeout));
            Assert.That(node.StatusIcon, Is.Not.Empty);
            Assert.That(node.StatusColorHex, Is.Not.EqualTo("#808080"), "should not fall through to the default color");
            Assert.That(node.StatusTooltip, Does.Contain("libfoo"));
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(node.StatusTooltip, Does.Not.Contain("Timed out"));
            Assert.That(node.DisplayText, Does.Contain("submodule"));
        }
    }

    [Test]
    public void Update_HealthyRepo_MapsToSuccess()
    {
        GitWizardLog.SilentMode = true;
        using var fixture = TempRepoFixture.CreateWithInitialCommit();

        var repository = new GitWizardRepository(fixture.Path);
        repository.Refresh();

        var node = new RepositoryNodeViewModel(repository);
        node.Update();

        Assert.That(node.Status, Is.EqualTo(RefreshStatus.Success));
    }

    [TestCase(RefreshStatus.Success, "\u2713")]
    [TestCase(RefreshStatus.Refreshing, "\u27f3")]
    [TestCase(RefreshStatus.Timeout, "\u26a0")]
    [TestCase(RefreshStatus.Error, "\u2717")]
    [TestCase(RefreshStatus.SubmoduleIssue, "\u2691")]
    public void Status_SelectsIcon(RefreshStatus status, string expected)
    {
        var node = new RepositoryNodeViewModel(new GitWizardRepository("/repo")) { Status = status };
        Assert.That(node.StatusIcon, Is.EqualTo(expected));
    }

    [Test]
    public void Status_Changed_FiresPropertyChanged()
    {
        var node = new RepositoryNodeViewModel(new GitWizardRepository("/repo"));
        var firedProperties = new List<string>();
        node.PropertyChanged += (_, e) => firedProperties.Add(e.PropertyName!);

        node.Status = RefreshStatus.Success;

        Assert.That(firedProperties, Does.Contain("StatusIcon"));
        Assert.That(firedProperties, Does.Contain("StatusColorHex"));
        Assert.That(firedProperties, Does.Contain("StatusTooltip"));
        Assert.That(firedProperties, Does.Contain("IsStatusVisible"));
    }

    [TestCase(true, "\u25bc")]
    [TestCase(false, "\u25b6")]
    public void IsExpanded_ReturnsCorrectExpandIndicator(bool isExpanded, string expected)
    {
        var node = new RepositoryNodeViewModel(new GitWizardRepository("/repo")) { IsExpanded = isExpanded };
        Assert.That(node.ExpandIndicator, Is.EqualTo(expected));
    }

    [Test]
    public void IsExpanded_Changed_FiresPropertyChanged()
    {
        var node = new RepositoryNodeViewModel(new GitWizardRepository("/repo"));
        var fired = false;
        node.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == "IsExpanded")
            {
                fired = true;
            }
        };

        node.IsExpanded = true;
        Assert.That(fired, Is.True);
    }

    [Test]
    public void DisplayText_Changed_FiresPropertyChanged()
    {
        var node = new RepositoryNodeViewModel(new GitWizardRepository("/repo"));
        var fired = false;
        node.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == "DisplayText")
            {
                fired = true;
            }
        };

        node.DisplayText = "Custom text";
        using (Assert.EnterMultipleScope())
        {
            Assert.That(fired, Is.True);
            Assert.That(node.DisplayText, Is.EqualTo("Custom text"));
        }
    }

    [Test]
    public void WorkingDirectory_ReturnsRepoWorkingDirectory()
    {
        var repo = new GitWizardRepository("/my/workdir");
        var node = new RepositoryNodeViewModel(repo);
        Assert.That(node.WorkingDirectory, Is.EqualTo("/my/workdir"));
    }

    [Test]
    public void CreateGroupHeader_CreatesGroupNode()
    {
        var node = RepositoryNodeViewModel.CreateGroupHeader("/srv/repos");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(node.IsGroupHeader, Is.True);
            Assert.That(node.GroupKey, Is.EqualTo("/srv/repos"));
            Assert.That(node.IsNotGroupHeader, Is.False);
            Assert.That(node.GroupHeaderFontWeight, Is.EqualTo("Bold"));
        }
    }

    [Test]
    public void IsGroupHeader_Node_DoesNotShowStatus()
    {
        var node = RepositoryNodeViewModel.CreateGroupHeader("/drive");
        Assert.That(node.IsStatusVisible, Is.False);
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
        var node = new RepositoryNodeViewModel(new GitWizardRepository("/test"));
        Assert.That(node.ItemPaddingString, Is.EqualTo("15,0,0,0"));
    }

    [Test]
    public void GroupHeaderFontWeight_RepositoryNode_ReturnsNormal()
    {
        var node = new RepositoryNodeViewModel(new GitWizardRepository("/test"));
        Assert.That(node.GroupHeaderFontWeight, Is.EqualTo("Normal"));
    }

    [Test]
    public void UpdateDisplayText_GroupHeader_ShowsErrorCount()
    {
        var node = RepositoryNodeViewModel.CreateGroupHeader("/group");

        var child1 = new RepositoryNodeViewModel(new GitWizardRepository("c:\\repo1")) { Status = RefreshStatus.Success };
        var child2 = new RepositoryNodeViewModel(new GitWizardRepository("c:\\repo2")) { Status = RefreshStatus.Error };
        var child3 = new RepositoryNodeViewModel(new GitWizardRepository("c:\\repo3")) { Status = RefreshStatus.Error };

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

        var child1 = new RepositoryNodeViewModel(new GitWizardRepository("c:\\repo1")) { Status = RefreshStatus.Timeout };
        var child2 = new RepositoryNodeViewModel(new GitWizardRepository("c:\\repo2")) { Status = RefreshStatus.Success };

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

    [TestCase(1, "[1 branch]")]
    [TestCase(2, "[2 branches]")]
    public void Update_DisplayText_WhenMergedBranches(int count, string expected)
    {
        var repo = new GitWizardRepository("/repo")
        {
            Branches = Enumerable.Range(1, count)
                .Select(i => new BranchInfo { Name = $"merged{i}", IsMerged = true })
                .ToList()
        };
        var node = new RepositoryNodeViewModel(repo);
        node.UpdateDisplayText();
        Assert.That(node.DisplayText, Does.Contain(expected));
    }

    [Test]
    public void StatusTooltip_Success_ShowsRefreshTime()
    {
        var repo = new GitWizardRepository("/repo")
        {
            RefreshTimeSeconds = 2.5
        };
        var node = new RepositoryNodeViewModel(repo) { Status = RefreshStatus.Success };
        Assert.That(node.StatusTooltip, Does.Contain("2.5"));
    }

    [Test]
    public void StatusTooltip_Timeout_WhenNoErrorUsesDefault()
    {
        var repo = new GitWizardRepository("/repo") { RefreshError = null };
        var node = new RepositoryNodeViewModel(repo) { Status = RefreshStatus.Timeout };
        Assert.That(node.StatusTooltip, Is.EqualTo("Timed out"));
    }

    [Test]
    public void StatusTooltip_Timeout_WhenErrorExistsUsesError()
    {
        var repo = new GitWizardRepository("/repo") { RefreshError = "Custom timeout error" };
        var node = new RepositoryNodeViewModel(repo) { Status = RefreshStatus.Timeout };
        Assert.That(node.StatusTooltip, Is.EqualTo("Custom timeout error"));
    }

    [Test]
    public void StatusTooltip_Error_WhenErrorExistsUsesError()
    {
        var repo = new GitWizardRepository("/repo") { RefreshError = "Something broke" };
        var node = new RepositoryNodeViewModel(repo) { Status = RefreshStatus.Error };
        Assert.That(node.StatusTooltip, Is.EqualTo("Something broke"));
    }

    [Test]
    public void Repository_Property_ReturnsInjectedRepo()
    {
        var repo = new GitWizardRepository("/repo");
        var node = new RepositoryNodeViewModel(repo);
        Assert.That(node.Repository, Is.SameAs(repo));
    }

    [Test]
    public void MatchesFilter_None_ReturnsTrue()
    {
        var node = new RepositoryNodeViewModel(new GitWizardRepository("/repo"));
        Assert.That(node.MatchesFilter(FilterType.None), Is.True);
    }

    [TestCase(FilterType.Stale)]
    [TestCase(FilterType.PendingChanges)]
    [TestCase(FilterType.DetachedHead)]
    [TestCase(FilterType.MyRepositories)]
    [TestCase(FilterType.DownstreamBranches)]
    [TestCase(FilterType.SubmoduleConfigIssue)]
    public void MatchesFilter_NegativeDefaults_ReturnFalse(FilterType filter)
    {
        var node = new RepositoryNodeViewModel(new GitWizardRepository("/repo"));
        Assert.That(node.MatchesFilter(filter), Is.False);
    }

    [Test]
    public void MatchesFilter_DownstreamBranches_WhenOnlyUnmergedBranches_ReturnsFalse()
    {
        var repo = new GitWizardRepository("/repo")
        {
            Branches = new List<BranchInfo> { new() { Name = "feature", IsMerged = false } }
        };
        var node = new RepositoryNodeViewModel(repo);
        Assert.That(node.MatchesFilter(FilterType.DownstreamBranches), Is.False);
    }
}

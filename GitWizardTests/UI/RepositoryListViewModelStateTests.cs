using GitWizardTests.TestSupport;
using GitWizardUI.ViewModels;

namespace GitWizardTests;

/// <summary>
/// Assigning a repository-list setting its current value changes nothing and notifies nobody.
/// </summary>
[TestFixture]
public class RepositoryListViewModelStateTests
{
    [Test]
    public void SearchText_AssigningTheCurrentValue_RaisesNoPropertyChange()
    {
        var list = TestViewModels.Create().RepositoryList;
        var changes = new List<string?>();
        list.PropertyChanged += (_, arguments) => changes.Add(arguments.PropertyName);

        list.SearchText = string.Empty;

        Assert.That(changes, Is.Empty);
    }

    [Test]
    public void ToggleGroupMode_ToTheActiveMode_RaisesNoPropertyChangeForTheMode()
    {
        var list = TestViewModels.Create().RepositoryList;
        var changes = new List<string?>();
        list.PropertyChanged += (_, arguments) => changes.Add(arguments.PropertyName);

        list.ToggleGroupMode(GroupMode.None);

        Assert.That(changes, Does.Not.Contain(nameof(RepositoryListViewModel.ActiveGroupMode)));
    }
}

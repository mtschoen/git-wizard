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
        // The whole point of #34: the status must NOT masquerade as a timeout.
        Assert.That(node.Status, Is.Not.EqualTo(RefreshStatus.Timeout));
        Assert.That(node.StatusIcon, Is.Not.Empty);
        Assert.That(node.StatusColorHex, Is.Not.EqualTo("#808080"), "should not fall through to the default color");
        Assert.That(node.StatusTooltip, Does.Contain("libfoo"));
        Assert.That(node.StatusTooltip, Does.Not.Contain("Timed out"));
        Assert.That(node.DisplayText, Does.Contain("submodule"));
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
}

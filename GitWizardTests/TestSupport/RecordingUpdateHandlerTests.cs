using GitWizard;

namespace GitWizardTests.TestSupport;

[TestFixture]
public class RecordingUpdateHandlerTests
{
    [Test]
    public void RecordsConcurrentCompletionsBeforePropagatingHookFailure()
    {
        var repository = new GitWizardRepository("/recording/repository");
        var handler = new RecordingUpdateHandler
        {
            CompletingRefresh = _ => throw new InvalidOperationException("callback failure")
        };
        Parallel.For(0, 32, _ =>
            Assert.Throws<InvalidOperationException>(() => handler.OnRepositoryRefreshCompleted(repository)));
        var snapshot = handler.RefreshCompleted;
        Assert.That(snapshot, Has.Length.EqualTo(32));
        Assert.That(snapshot, Has.All.SameAs(repository));
        Assert.Throws<InvalidOperationException>(() => handler.OnRepositoryRefreshCompleted(repository));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshot, Has.Length.EqualTo(32));
            Assert.That(handler.RefreshCompleted, Has.Length.EqualTo(33));
        }
    }

    [Test]
    public void RecordsOriginalArgumentsForEveryCallback()
    {
        var parent = new GitWizardRepository("/parent");
        var child = new GitWizardRepository("/parent/child");
        var handler = new RecordingUpdateHandler();
        handler.StartProgress("scan", 7);
        handler.UpdateProgress(3);
        handler.SendUpdateMessage(null);
        handler.SendUpdateMessage("message");
        handler.OnRepositoryCreated(parent);
        handler.OnSubmoduleCreated(parent, child);
        handler.OnWorktreeCreated(child);
        handler.OnUninitializedSubmoduleCreated(parent, "uninitialized");
        handler.OnRepositoryRefreshCompleted(parent);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(handler.ProgressStarts, Is.EqualTo([("scan", 7)]));
            Assert.That(handler.ProgressCounts, Is.EqualTo([3]));
            Assert.That(handler.Messages, Is.EqualTo([null, "message"]));
            Assert.That(handler.RepositoriesCreated.Single(), Is.SameAs(parent));
            Assert.That(handler.SubmodulesCreated.Single(), Is.EqualTo((parent, child)));
            Assert.That(handler.WorktreesCreated.Single(), Is.SameAs(child));
            Assert.That(handler.UninitializedSubmodulesCreated.Single(), Is.EqualTo((parent, "uninitialized")));
            Assert.That(handler.RefreshCompleted.Single(), Is.SameAs(parent));
        }
    }
}

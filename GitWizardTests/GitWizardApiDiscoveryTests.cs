using GitWizard;

namespace GitWizardTests;

/// <summary>
/// Covers edge paths of the recursive (non-MFT) repository scan in
/// <see cref="GitWizardApi.GetRepositoryPaths(string, System.Collections.Generic.ICollection{string}, System.Collections.Generic.ICollection{string}, IUpdateHandler?)"/>:
/// a throwing update handler is swallowed, and a hidden-attribute directory is skipped.
/// </summary>
public class GitWizardApiDiscoveryTests
{
    [SetUp]
    public void SetUp() => GitWizardLog.SilentMode = true;

    [Test]
    public void GetRepositoryPaths_SwallowsThrowingUpdateHandler()
    {
        var root = Path.Combine(Path.GetTempPath(), "gw-scan-throw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "repo", ".git"));
        try
        {
            var paths = new List<string>();
            var handler = new ThrowingMessageHandler();

            Assert.DoesNotThrow(() => GitWizardApi.GetRepositoryPaths(root, paths, Array.Empty<string>(), handler));

            Assert.Multiple(() =>
            {
                Assert.That(handler.MessageCalls, Is.GreaterThan(0),
                    "The scan must call SendUpdateMessage (which throws and must be caught).");
                Assert.That(paths.Any(p => p.Contains("repo", StringComparison.OrdinalIgnoreCase)), Is.True,
                    "The repo must still be discovered despite the throwing handler.");
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void GetRepositoryPaths_SkipsHiddenAttributeDirectory()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Ignore("FileAttributes.Hidden is a Windows concept; the Hidden-attribute skip only applies there.");

        var root = Path.Combine(Path.GetTempPath(), "gw-scan-hidden-" + Guid.NewGuid().ToString("N"));
        var hidden = Path.Combine(root, "hiddendir");
        Directory.CreateDirectory(Path.Combine(hidden, "repo", ".git"));
        new DirectoryInfo(hidden).Attributes |= FileAttributes.Hidden;
        try
        {
            var paths = new List<string>();
            GitWizardApi.GetRepositoryPaths(root, paths, Array.Empty<string>());

            Assert.That(paths.Any(p => p.Contains("repo", StringComparison.OrdinalIgnoreCase)), Is.False,
                "A repo under a hidden-attribute directory must be skipped by the scan.");
        }
        finally
        {
            new DirectoryInfo(hidden).Attributes &= ~FileAttributes.Hidden;
            Directory.Delete(root, recursive: true);
        }
    }

    sealed class ThrowingMessageHandler : IUpdateHandler
    {
        public int MessageCalls { get; private set; }

        public void StartProgress(string description, int total) { }
        public void UpdateProgress(int count) { }

        public void SendUpdateMessage(string? message)
        {
            MessageCalls++;
            throw new InvalidOperationException("message failure");
        }

        public void OnRepositoryCreated(GitWizardRepository gitWizardRepository) { }
        public void OnSubmoduleCreated(GitWizardRepository parent, GitWizardRepository submodule) { }
        public void OnWorktreeCreated(GitWizardRepository worktree) { }
        public void OnUninitializedSubmoduleCreated(GitWizardRepository parent, string submodulePath) { }
        public void OnRepositoryRefreshCompleted(GitWizardRepository gitWizardRepository) { }
    }
}

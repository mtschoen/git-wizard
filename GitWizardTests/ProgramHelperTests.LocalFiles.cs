using GitWizard;

namespace GitWizardTests;

public partial class ProgramEntryPointTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task Main_DeleteAllRefusalIsNonzeroAndDoesNotContinue(bool alsoClear)
    {
        // Module initializers do not reach the child process; it inherits this owned home explicitly.
        var home = TestUtilities.RedirectLocalFilesToTemp();
        try
        {
            Assert.That(Environment.GetEnvironmentVariable("GITWIZARD_HOME"), Is.Not.Null.And.Not.Empty);
            var cache = MftIndexCachePolicy.GetCacheRoot(GitWizardApi.GetLocalFilesPath());
            Directory.CreateDirectory(cache);
            var sentinel = Path.Combine(cache, "sentinel.mlix");
            var list = GitWizardApi.GetCachedRepositoryListPath();
            await File.WriteAllTextAsync(sentinel, "retained");
            await File.WriteAllTextAsync(list, "repository sentinel");
            var arguments = new List<string> { "--delete-all-local-files", "--no-refresh", "--silent" };
            if (alsoClear)
                arguments.Add("--clear-cache");
            var result = await RunCliAsync(arguments.ToArray());
            Assert.That(result.ExitCode, Is.EqualTo(1));
            Assert.That(result.StandardError, Does.Contain("mft-index"));
            Assert.That(result.StandardOutput, Is.Empty);
            Assert.That(await File.ReadAllTextAsync(sentinel), Is.EqualTo("retained"));
            Assert.That(await File.ReadAllTextAsync(list), Is.EqualTo("repository sentinel"));
        }
        finally
        {
            TestUtilities.ClearLocalFilesRedirect(home);
        }
    }
}

using GitWizard;

namespace GitWizardTests;

/// <summary>
/// Assembly-wide safety net: redirects GitWizard's data dir (config/cache/report) to an isolated
/// temp home for the ENTIRE test run via GITWIZARD_HOME, so no test ever reads or writes the real
/// ~/.GitWizard — even one that forgets to call <see cref="TestUtilities.RedirectLocalFilesToTemp"/>.
///
/// This exists because the per-class opt-in redirect pattern leaked twice: GitWizardConfigurationTests
/// wrote to the real config, and GitWizardApiAdditionalTests called GitWizardApi.DeleteAllLocalFiles()
/// without a redirect and recursively DELETED the user's real ~/.GitWizard (their search paths,
/// including custom include folders, were lost). A safety net makes isolation the default, not an
/// opt-in every new test class has to remember.
///
/// The temp home is placed under the user profile so path-shape assertions still hold cross-platform
/// (e.g. GitWizardApiAdditionalTests.GetLocalFilesPath_ContainsUserProfile). Per-class redirects via
/// <see cref="TestUtilities.RedirectLocalFilesToTemp"/> still override this for their own isolation;
/// their teardown restores this fallback (see <see cref="TestUtilities.ClearLocalFilesRedirect"/>).
/// </summary>
[SetUpFixture]
public class GlobalTestSetup
{
    string? _home;

    [OneTimeSetUp]
    public void RedirectDataDirForWholeAssembly()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _home = Path.Combine(profile, ".GitWizardTestHome", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
        Environment.SetEnvironmentVariable("GITWIZARD_HOME", _home);
        TestUtilities.DefaultHome = _home;
    }

    [OneTimeTearDown]
    public void RestoreDataDir()
    {
        TestUtilities.DefaultHome = null;
        Environment.SetEnvironmentVariable("GITWIZARD_HOME", null);

        // Release the open log-file handle GitWizardLog keeps in the data dir, then remove the temp
        // home. Cleanup is best-effort: a still-locked file must not fail the whole run (a thrown
        // OneTimeTearDown makes `dotnet test` exit non-zero and reds CI even with every test passing).
        GitWizardLog.CloseCurrentLogFile();
        try
        {
            if (!string.IsNullOrEmpty(_home) && Directory.Exists(_home))
                Directory.Delete(_home, recursive: true);
        }
        catch
        {
            // Throwaway temp dir under the user profile; the OS cleans %TEMP%-style cruft eventually.
        }
    }
}

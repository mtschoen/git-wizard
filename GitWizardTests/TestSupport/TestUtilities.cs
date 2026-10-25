using GitWizard;
using GitWizardTests.TestSupport;

namespace GitWizardTests;

/// <summary>
/// Shared test utilities for resetting static caches.
/// Reduces duplication across test files that need static isolation.
/// </summary>
public static class TestUtilities
{
    /// <summary>
    /// Resets static caches in GitWizardReport and GitWizardConfiguration.
    /// Call in SetUp and TearDown to ensure test isolation.
    /// Does NOT delete cached files.
    /// </summary>
    public static void ResetStaticCaches()
    {
        GitWizardReport.ResetCachedReport();
        GitWizardConfiguration.ResetGlobalConfiguration();
    }

    /// <summary>
    /// The assembly-wide fallback GITWIZARD_HOME set by <see cref="GlobalTestSetup"/>. Clearing a
    /// per-class redirect restores this instead of null, so a later test that forgets to redirect
    /// still lands on an isolated temp dir - never the real ~/.GitWizard.
    /// </summary>
    public static string? DefaultHome { get; set; }

    /// <summary>
    /// Clears the GITWIZARD_HOME redirect set by <see cref="LocalFilesSandbox"/> and
    /// deletes the temp dir. Safe to call with a null/missing path. NUnit runs TearDown even
    /// after a failing test, so the env var never leaks into the next test.
    /// </summary>
    public static void ClearLocalFilesRedirect(string? temp)
    {
        // Restore the assembly-wide isolated home, NOT null. A null here would re-expose the real
        // ~/.GitWizard to any later test that forgets to redirect - and a TryDeleteAllLocalFiles there
        // wipes the user's real config (search paths and all). GlobalTestSetup sets DefaultHome.
        Environment.SetEnvironmentVariable("GITWIZARD_HOME", DefaultHome);
        if (string.IsNullOrEmpty(temp))
        {
            return;
        }

        // A fire-and-forget config write can still be in flight when the test ends: e.g.
        // SettingsViewModel mutations (and its constructor) call SaveImmediate(), which runs
        // GitWizardConfiguration.SaveGlobalConfigurationAsync -> File.WriteAllTextAsync(config.json)
        // without being awaited. Deleting the temp home while that write holds config.json throws
        // IOException "the process cannot access the file ... because it is being used by another
        // process" - flaky under CI timing, never reproduced locally. Retry briefly so the async
        // write finishes, then give up (a throwaway temp dir must not red CI; %TEMP% is reclaimed).
        for (var attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                if (Directory.Exists(temp))
                {
                    TempDirectory.DeleteTree(temp);
                }

                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(25);
            }
        }
    }
}

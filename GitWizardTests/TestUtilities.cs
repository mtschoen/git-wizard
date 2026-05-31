using System.Reflection;
using GitWizard;

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
    /// Does NOT delete cached files - use AsyncFileIOTests.ResetStaticCaches for full cleanup.
    /// </summary>
    public static void ResetStaticCaches()
    {
        var reportType = typeof(GitWizardReport);
        var reportField = reportType.GetField("_cachedReport", BindingFlags.NonPublic | BindingFlags.Static);
        reportField?.SetValue(null, null);

        var configType = typeof(GitWizardConfiguration);
        var configField = configType.GetField("_globalConfiguration", BindingFlags.NonPublic | BindingFlags.Static);
        configField?.SetValue(null, null);
    }

    /// <summary>
    /// Redirects GitWizard's data dir (config/cache/report) to a fresh temp dir via the
    /// GITWIZARD_HOME env var, so a test never reads or writes the real ~/.GitWizard.
    /// Call in SetUp; pass the returned path to <see cref="ClearLocalFilesRedirect"/> in TearDown.
    /// </summary>
    public static string RedirectLocalFilesToTemp()
    {
        var temp = Path.Combine(Path.GetTempPath(), "GitWizardHome", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        Environment.SetEnvironmentVariable("GITWIZARD_HOME", temp);
        return temp;
    }

    /// <summary>
    /// The assembly-wide fallback GITWIZARD_HOME set by <see cref="GlobalTestSetup"/>. Clearing a
    /// per-class redirect restores this instead of null, so a later test that forgets to redirect
    /// still lands on an isolated temp dir - never the real ~/.GitWizard.
    /// </summary>
    public static string? DefaultHome { get; set; }

    /// <summary>
    /// Clears the GITWIZARD_HOME redirect set by <see cref="RedirectLocalFilesToTemp"/> and
    /// deletes the temp dir. Safe to call with a null/missing path. NUnit runs TearDown even
    /// after a failing test, so the env var never leaks into the next test.
    /// </summary>
    public static void ClearLocalFilesRedirect(string? temp)
    {
        // Restore the assembly-wide isolated home, NOT null. A null here would re-expose the real
        // ~/.GitWizard to any later test that forgets to redirect - and a DeleteAllLocalFiles there
        // wipes the user's real config (search paths and all). GlobalTestSetup sets DefaultHome.
        Environment.SetEnvironmentVariable("GITWIZARD_HOME", DefaultHome);
        if (string.IsNullOrEmpty(temp))
            return;

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
                    Directory.Delete(temp, recursive: true);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(25);
            }
        }
    }
}

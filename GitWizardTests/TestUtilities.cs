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
    /// Does NOT delete cached files — use AsyncFileIOTests.ResetStaticCaches for full cleanup.
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
    /// Clears the GITWIZARD_HOME redirect set by <see cref="RedirectLocalFilesToTemp"/> and
    /// deletes the temp dir. Safe to call with a null/missing path. NUnit runs TearDown even
    /// after a failing test, so the env var never leaks into the next test.
    /// </summary>
    public static void ClearLocalFilesRedirect(string? temp)
    {
        Environment.SetEnvironmentVariable("GITWIZARD_HOME", null);
        if (!string.IsNullOrEmpty(temp) && Directory.Exists(temp))
            Directory.Delete(temp, recursive: true);
    }
}

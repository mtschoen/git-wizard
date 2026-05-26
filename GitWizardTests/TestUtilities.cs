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
}

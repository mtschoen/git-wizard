using GitWizard;

namespace GitWizardTests.TestSupport;

/// <summary>Runs the enumeration-backed discovery of one recursive root the way production does.</summary>
static class EnumeratedDiscovery
{
    static RecursiveDiscovery Run(
        string rootPath, ICollection<string> ignoredPaths, bool? skipHiddenDirectories = null,
        bool? includeTempDirectories = null)
    {
        var configuration = new GitWizardConfiguration();
        if (skipHiddenDirectories.HasValue)
        {
            configuration.SkipHiddenDirectories = skipHiddenDirectories.Value;
        }

        if (includeTempDirectories.HasValue)
        {
            configuration.IncludeTempDirectories = includeTempDirectories.Value;
        }

        foreach (var ignored in ignoredPaths)
        {
            configuration.IgnoredPaths.Add(ignored);
        }

        return Run(new DiscoveryRequest(configuration, DiscoveryPurpose.Ordinary, false, true, []), rootPath);
    }

    public static RecursiveDiscovery Run(DiscoveryRequest request, string rootPath, CancellationToken cancellationToken = default)
    {
        var expanded = GitWizardApi.ExpandSearchPath(rootPath);
        return expanded == null
            ? new RecursiveDiscovery([], [], new Dictionary<string, string>())
            : EnumerationDiscovery.DiscoverAsync(request, expanded, cancellationToken).GetAwaiter().GetResult();
    }

    /// <summary>Adds the repositories found under <paramref name="rootPath" /> to <paramref name="paths" />.</summary>
    public static void GetRepositoryPaths(
        string rootPath, ICollection<string> paths, ICollection<string> ignoredPaths,
        bool? skipHiddenDirectories = null, bool? includeTempDirectories = null)
    {
        foreach (var path in Run(rootPath, ignoredPaths, skipHiddenDirectories, includeTempDirectories).Paths)
        {
            paths.Add(path);
        }
    }
}

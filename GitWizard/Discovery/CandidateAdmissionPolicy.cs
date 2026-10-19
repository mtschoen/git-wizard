using GitWizard.Watch;

namespace GitWizard;

/// <summary>
/// Decides whether a repository candidate may enter an inventory. The recursive walk, indexed
/// candidate filter, watch replay and Linux retained roots call it. It takes plain values (paths and flags) so the
/// enumeration-backed discovery reuses it unchanged.
/// </summary>
/// <remarks>
/// Ignored roots reject the root itself and every descendant. With <see cref="SkipHiddenDirectories"/>
/// a candidate is rejected when any directory between its search root and itself, the candidate
/// directory included, is dot-prefixed or carries the Hidden attribute. Temporary directories are
/// rejected unless <see cref="IncludeTempDirectories"/> is set or the search root is itself a
/// temporary directory.
/// </remarks>
internal readonly record struct CandidateAdmissionPolicy(
    IReadOnlyList<WatchPath> IgnoredRoots,
    bool IncludeTempDirectories,
    bool SkipHiddenDirectories)
{
    public static CandidateAdmissionPolicy ForScope(DiscoveryScope scope) =>
        Create(scope.IgnoredRoots, scope.Request.Configuration);

    public static CandidateAdmissionPolicy Create(IEnumerable<string> ignoredRoots, GitWizardConfiguration configuration) =>
        new(ignoredRoots.Select(WatchPath.Parse).ToArray(),
            configuration.IncludeTempDirectories == true,
            configuration.SkipHiddenDirectories == true);

    public static bool AdmitsInventoryCandidate(DiscoveryScope scope, string candidateRoot) =>
        ForScope(scope).AdmitsInventoryCandidate(scope.SearchRoots, scope.RetainedRoots, candidateRoot);

    public static bool IsDotPrefixed(string directoryName) => directoryName.StartsWith('.');

    public static bool HasHiddenAttribute(FileAttributes attributes) => attributes.HasFlag(FileAttributes.Hidden);

    public bool IsIgnored(WatchPath path) => IgnoredRoots.Any(path.IsUnderOrEqual);

    /// <summary>
    /// Applies the ignored, temporary and hidden rules to <paramref name="candidate"/>, which must
    /// already lie under or equal <paramref name="searchRoot"/>. <paramref name="hasHiddenAttributeWithin"/>
    /// answers whether any of the nearest N directories of the candidate, counting the candidate
    /// itself, carries the Hidden attribute; a route without attributes passes null. A route that
    /// translated the search root into index coordinates passes the root as configured in
    /// <paramref name="configuredSearchRoot"/>, which decides whether the search root is temporary.
    /// </summary>
    public bool AdmitsWithin(
        WatchPath searchRoot,
        WatchPath candidate,
        Func<int, bool>? hasHiddenAttributeWithin = null,
        WatchPath? configuredSearchRoot = null)
    {
        if (IsIgnored(candidate))
            return false;

        if (!IncludeTempDirectories &&
            !TempDirectories.IsTempPath((configuredSearchRoot ?? searchRoot).Original) &&
            TempDirectories.IsTempPath(candidate.Original))
            return false;

        if (!SkipHiddenDirectories)
            return true;

        var relativeSegments = RelativeSegments(searchRoot, candidate);
        if (relativeSegments.Any(IsDotPrefixed))
            return false;

        return relativeSegments.Length == 0 || hasHiddenAttributeWithin?.Invoke(relativeSegments.Length) != true;
    }

    /// <summary>
    /// Admits an inventory candidate: it must satisfy <see cref="AdmitsWithin"/> for a search root
    /// that claims it, or equal a retained root that no search root claims.
    /// </summary>
    public bool AdmitsInventoryCandidate(
        IReadOnlyList<string> searchRoots, IReadOnlyList<string> retainedRoots, string candidateRoot)
    {
        var candidate = WatchPath.Parse(candidateRoot);
        if (IsIgnored(candidate))
            return false;

        var hasHiddenAttributeWithin = DiskHiddenAncestors.For(candidate);
        var underSearchRoot = false;
        foreach (var searchRoot in searchRoots.Select(WatchPath.Parse))
        {
            if (!candidate.IsUnderOrEqual(searchRoot))
                continue;

            underSearchRoot = true;
            if (AdmitsWithin(searchRoot, candidate, hasHiddenAttributeWithin))
                return true;
        }

        return !underSearchRoot && retainedRoots.Any(root => candidate.IsEqualTo(WatchPath.Parse(root)));
    }

    static string[] RelativeSegments(WatchPath searchRoot, WatchPath candidate) =>
        candidate.Comparison.Length <= searchRoot.Comparison.Length
            ? []
            : candidate.Comparison[searchRoot.Comparison.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
}

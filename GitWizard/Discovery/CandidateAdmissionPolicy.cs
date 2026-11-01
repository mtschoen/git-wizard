using GitWizard.Watch;

namespace GitWizard.Discovery;

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
readonly partial record struct CandidateAdmissionPolicy(
    IReadOnlyList<WatchPath> IgnoredRoots,
    bool IncludeTempDirectories,
    bool SkipHiddenDirectories)
{
    public static CandidateAdmissionPolicy ForScope(DiscoveryScope scope) =>
        Create(scope.IgnoredRoots, scope.Request.Configuration);

    public static CandidateAdmissionPolicy Create(IEnumerable<string> ignoredRoots, GitWizardConfiguration configuration) =>
        new((WatchPath[])[.. ignoredRoots.Select(WatchPath.Parse)],
            configuration.IncludeTempDirectories == true,
            configuration.SkipHiddenDirectories == true);

    static bool IsDotPrefixed(string directoryName) => directoryName.StartsWith('.');

    public static bool HasHiddenAttribute(FileAttributes attributes) => attributes.HasFlag(FileAttributes.Hidden);

    bool IsIgnored(WatchPath path) => IgnoredRoots.Any(path.IsUnderOrEqual);

    /// <summary>
    /// Applies the ignored, temporary and hidden rules to <paramref name="candidate"/>, which must
    /// already lie under or equal <paramref name="searchRoot"/>. <paramref name="hasHiddenAttributeWithin"/>
    /// answers whether any of the nearest N directories of the candidate, counting the candidate
    /// itself, carries the Hidden attribute; a route without attributes passes null.
    /// </summary>
    public bool AdmitsWithin(
        WatchPath searchRoot,
        WatchPath candidate,
        Func<int, bool>? hasHiddenAttributeWithin = null)
    {
        if (IsIgnored(candidate))
        {
            return false;
        }

        if (!IncludeTempDirectories &&
            !TempDirectories.IsTempPath(searchRoot.Original) &&
            TempDirectories.IsTempPath(candidate.Original))
        {
            return false;
        }

        if (!SkipHiddenDirectories)
        {
            return true;
        }

        var relativeSegments = RelativeSegments(searchRoot, candidate);
        if (relativeSegments.Any(IsDotPrefixed))
        {
            return false;
        }

        return relativeSegments.Length == 0 || hasHiddenAttributeWithin?.Invoke(relativeSegments.Length) != true;
    }

}

using GitWizard.Watch;

namespace GitWizard;

readonly partial record struct CandidateAdmissionPolicy
{
    public static bool AdmitsInventoryCandidate(DiscoveryScope scope, string candidateRoot) =>
        ForScope(scope).AdmitsInventoryCandidate(scope.SearchRoots, scope.RetainedRoots, candidateRoot);

    /// <summary>
    /// Admits an inventory candidate: it must satisfy <see cref="AdmitsWithin"/> for a search root
    /// that claims it, or equal a retained root that no search root claims.
    /// </summary>
    public bool AdmitsInventoryCandidate(
        IReadOnlyList<string> searchRoots, IReadOnlyList<string> retainedRoots, string candidateRoot)
    {
        var candidate = WatchPath.Parse(candidateRoot);
        if (IsIgnored(candidate))
        {
            return false;
        }

        var hasHiddenAttributeWithin = DiskHiddenAncestors.For(candidate);
        var underSearchRoot = false;
        foreach (var searchRoot in searchRoots.Select(WatchPath.Parse))
        {
            if (!candidate.IsUnderOrEqual(searchRoot))
            {
                continue;
            }

            underSearchRoot = true;
            if (AdmitsWithin(searchRoot, candidate, hasHiddenAttributeWithin))
            {
                return true;
            }
        }

        return !underSearchRoot && retainedRoots.Any(root => candidate.IsEqualTo(WatchPath.Parse(root)));
    }

    static string[] RelativeSegments(WatchPath searchRoot, WatchPath candidate) =>
        candidate.Comparison.Length <= searchRoot.Comparison.Length
            ? []
            : candidate.Comparison[searchRoot.Comparison.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
}

using System.Collections.ObjectModel;
using GitWizard.Watch;

namespace GitWizard.Discovery;

public sealed class DiscoveryScope
{
    public DiscoveryRequest Request { get; }
    public IReadOnlyList<string> SearchRoots { get; }
    public IReadOnlyList<string> RetainedRoots { get; }
    public IReadOnlyList<string> IgnoredRoots { get; }
    public IReadOnlyList<string> IndexRoots { get; }
    public IReadOnlyDictionary<string, string> ExpansionErrors { get; }

    DiscoveryScope(DiscoveryRequest request, IReadOnlyList<string> searchRoots,
        IReadOnlyList<string> retainedRoots, IReadOnlyList<string> ignoredRoots,
        IReadOnlyDictionary<string, string> errors)
    {
        Request = request;
        SearchRoots = Array.AsReadOnly<string>([.. searchRoots]);
        RetainedRoots = Array.AsReadOnly<string>([.. retainedRoots]);
        IgnoredRoots = Array.AsReadOnly<string>([.. ignoredRoots]);
        IndexRoots = Array.AsReadOnly<string>([.. searchRoots.Concat(retainedRoots).Distinct(WatchPath.StringComparer)]);
        ExpansionErrors = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(errors, WatchPath.StringComparer));
    }

    public static DiscoveryScope Create(DiscoveryRequest request)
    {
        var searchRoots = new List<string>();
        var errors = new Dictionary<string, string>(WatchPath.StringComparer);
        foreach (var rawPath in request.Configuration.SearchPaths)
        {
            if (GitWizardApi.ExpandSearchPath(rawPath) is { } expanded)
            {
                searchRoots.Add(expanded);
            }
            else
            {
                errors[rawPath] = $"Search path does not exist or could not be expanded: {rawPath}";
            }
        }

        var retained = request.RetainedRoots.Select(GitWizardApi.ExpandIgnoredPath).OfType<string>().ToArray();
        return CreateExpanded(request, searchRoots, retained,
            GitWizardApi.ExpandIgnoredPaths(request.Configuration.IgnoredPaths), errors);
    }

    public static DiscoveryScope CreateExpanded(DiscoveryRequest request, IReadOnlyList<string> searchRoots,
        IReadOnlyList<string> retainedRoots, IReadOnlyList<string> ignoredRoots,
        IReadOnlyDictionary<string, string>? initialErrors = null) =>
        new(request, searchRoots, retainedRoots, ignoredRoots,
            initialErrors ?? new Dictionary<string, string>(WatchPath.StringComparer));
}

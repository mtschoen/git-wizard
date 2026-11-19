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
        IndexRoots = Array.AsReadOnly<string>([.. searchRoots.Concat(retainedRoots.Where(retained =>
            !searchRoots.Any(search => WatchPath.Parse(retained).IsUnderOrEqual(WatchPath.Parse(search)))))
            .Distinct(WatchPath.StringComparer)]);
        ExpansionErrors = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(errors, WatchPath.StringComparer));
    }

    // Inventory error key and text for a scope that names no root at all (see HasNoRoots).
    public const string NoRootsErrorKey = "*";
    public const string NoRootsErrorMessage = "No search roots configured.";

    /// <summary>
    /// True when neither a usable search root nor a retained root remains, so discovery is
    /// <see cref="DiscoveryOutcome.Unusable"/> without scanning anything.
    /// </summary>
    public bool HasNoRoots => SearchRoots.Count == 0 && RetainedRoots.Count == 0;

    /// <summary>
    /// The errors discovery reports for a scope with <see cref="HasNoRoots"/>: every search path that
    /// could not be expanded, then <see cref="NoRootsErrorMessage"/>. Empty when any root remains.
    /// </summary>
    public IReadOnlyDictionary<string, string> GetNoRootsErrors()
    {
        if (!HasNoRoots)
        {
            return new Dictionary<string, string>();
        }

        return new Dictionary<string, string>(ExpansionErrors, StringComparer.OrdinalIgnoreCase)
        {
            [NoRootsErrorKey] = NoRootsErrorMessage
        };
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

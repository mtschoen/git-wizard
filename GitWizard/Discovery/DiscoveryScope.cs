using System.Collections.ObjectModel;
using GitWizard.Watch;

namespace GitWizard;

public sealed class DiscoveryScope
{
    public DiscoveryRequest Request { get; }
    public IReadOnlyList<string> SearchRoots { get; }
    public IReadOnlyList<string> RetainedRoots { get; }
    public IReadOnlyList<string> IgnoredRoots { get; }
    public IReadOnlyList<RootClassification> Classifications { get; }
    public IReadOnlyList<string> RequestedNtfsDrives { get; }
    public IReadOnlyList<string> RecursiveRoots { get; }
    public IReadOnlyList<string> WatchExcludedRoots { get; }
    public IReadOnlyDictionary<string, string> ClassificationErrors { get; }

    DiscoveryScope(
        DiscoveryRequest request,
        IReadOnlyList<string> searchRoots,
        IReadOnlyList<string> retainedRoots,
        IReadOnlyList<string> ignoredRoots,
        IReadOnlyList<RootClassification> classifications,
        IReadOnlyList<string> requestedNtfsDrives,
        IReadOnlyList<string> recursiveRoots,
        IReadOnlyList<string> watchExcludedRoots,
        IReadOnlyDictionary<string, string> classificationErrors)
    {
        Request = request;
        SearchRoots = Array.AsReadOnly(searchRoots.ToArray());
        RetainedRoots = Array.AsReadOnly(retainedRoots.ToArray());
        IgnoredRoots = Array.AsReadOnly(ignoredRoots.ToArray());
        Classifications = Array.AsReadOnly(classifications.ToArray());
        RequestedNtfsDrives = Array.AsReadOnly(requestedNtfsDrives.ToArray());
        RecursiveRoots = Array.AsReadOnly(recursiveRoots.ToArray());
        WatchExcludedRoots = Array.AsReadOnly(watchExcludedRoots.ToArray());
        ClassificationErrors = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(classificationErrors));
    }

    public static DiscoveryScope Create(DiscoveryRequest request, Func<string, RootClassification> classify)
    {
        var searchRoots = new List<string>();
        var expansionErrors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawPath in request.Configuration.SearchPaths)
        {
            var expanded = GitWizardApi.ExpandSearchPath(rawPath);
            if (expanded is not null)
            {
                searchRoots.Add(expanded);
            }
            else
            {
                expansionErrors[rawPath] = $"Search path does not exist or could not be expanded: {rawPath}";
            }
        }

        var retainedRoots = request.RetainedRoots
            .Select(GitWizardApi.ExpandIgnoredPath)
            .OfType<string>()
            .ToArray();

        var ignoredRoots = GitWizardApi.ExpandIgnoredPaths(request.Configuration.IgnoredPaths);

        return CreateExpanded(request, searchRoots, retainedRoots, ignoredRoots, classify, expansionErrors);
    }

    public static DiscoveryScope CreateExpanded(
        DiscoveryRequest request,
        IReadOnlyList<string> searchRoots,
        IReadOnlyList<string> retainedRoots,
        IReadOnlyList<string> ignoredRoots,
        Func<string, RootClassification> classify,
        IReadOnlyDictionary<string, string>? initialErrors = null)
    {
        var classifications = new List<RootClassification>();
        var errors = new Dictionary<string, string>(initialErrors ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        var requestedNtfsDrives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var recursiveRoots = new List<string>();
        var watchExcludedRoots = new List<string>();

        // Classify search roots
        foreach (var root in searchRoots)
        {
            var classification = classify(root);
            classifications.Add(classification);

            if (classification.FileSystem == RootFileSystem.Ntfs)
            {
                var drive = NormalizeDriveLetter(classification.NtfsDrive ?? root);
                if (drive is not null)
                    requestedNtfsDrives.Add(drive);
            }
            else if (classification.FileSystem == RootFileSystem.Recursive)
            {
                watchExcludedRoots.Add(root);
            }
            else // Unknown
            {
                watchExcludedRoots.Add(root);
                if (!request.NoMft)
                {
                    errors[root] = classification.Error ?? $"Unknown filesystem for search root: {root}";
                }
            }

            if (request.NoMft)
            {
                recursiveRoots.Add(root);
            }
            else if (classification.FileSystem == RootFileSystem.Recursive)
            {
                recursiveRoots.Add(root);
            }
        }

        // Retained roots are exact roots; classify them for NTFS journal watch inclusion
        foreach (var root in retainedRoots)
        {
            var classification = classify(root);
            classifications.Add(classification);

            if (classification.FileSystem == RootFileSystem.Ntfs)
            {
                var drive = NormalizeDriveLetter(classification.NtfsDrive ?? root);
                if (drive is not null)
                    requestedNtfsDrives.Add(drive);
            }
            else
            {
                watchExcludedRoots.Add(root);
                if (classification.FileSystem == RootFileSystem.Unknown && !request.NoMft)
                {
                    errors[root] = classification.Error ?? $"Unknown filesystem for retained root: {root}";
                }
            }
        }

        var sortedDrives = requestedNtfsDrives.OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToArray();

        return new DiscoveryScope(
            request,
            searchRoots,
            retainedRoots,
            ignoredRoots,
            classifications,
            sortedDrives,
            recursiveRoots,
            watchExcludedRoots,
            errors);
    }

    static string? NormalizeDriveLetter(string pathOrDrive)
    {
        var wp = WatchPath.Parse(pathOrDrive);
        if (wp.IsWindows && wp.Volume is { Length: 1 } vol && char.IsAsciiLetter(vol[0]))
            return vol.ToUpperInvariant();

        if (pathOrDrive.Length >= 1 && char.IsAsciiLetter(pathOrDrive[0]))
        {
            if (pathOrDrive.Length == 1 || (pathOrDrive.Length >= 2 && pathOrDrive[1] == ':'))
                return char.ToUpperInvariant(pathOrDrive[0]).ToString();
        }

        return null;
    }
}

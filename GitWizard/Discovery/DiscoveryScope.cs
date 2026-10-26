using System.Collections.ObjectModel;
using GitWizard.Watch;
using MFTLib.Index;

namespace GitWizard.Discovery;

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
        RootPaths rootPaths,
        IReadOnlyList<RootClassification> classifications,
        IReadOnlyList<string> requestedNtfsDrives,
        IReadOnlyDictionary<string, string> classificationErrors)
    {
        Request = request;
        SearchRoots = Array.AsReadOnly<string>([.. rootPaths.SearchRoots]);
        RetainedRoots = Array.AsReadOnly<string>([.. rootPaths.RetainedRoots]);
        IgnoredRoots = Array.AsReadOnly<string>([.. rootPaths.IgnoredRoots]);
        Classifications = Array.AsReadOnly<RootClassification>([.. classifications]);
        RequestedNtfsDrives = Array.AsReadOnly<string>([.. requestedNtfsDrives]);
        RecursiveRoots = Array.AsReadOnly<string>([.. rootPaths.RecursiveRoots]);
        WatchExcludedRoots = Array.AsReadOnly<string>([.. rootPaths.WatchExcludedRoots]);
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
        var requestedNtfsDrives = new HashSet<DriveKey>();
        var recursiveRoots = new List<string>();
        var watchExcludedRoots = new List<string>();

        // Classify search roots
        foreach (var root in searchRoots)
        {
            var classification = classify(root);
            classifications.Add(classification);

            switch (classification.FileSystem)
            {
                case RootFileSystem.Ntfs:
                    AddNtfsDrive(classification, root, requestedNtfsDrives);
                    break;
                case RootFileSystem.Recursive:
                    watchExcludedRoots.Add(root);
                    break;
                case RootFileSystem.Unknown:
                    watchExcludedRoots.Add(root);
                    if (!request.NoMft)
                    {
                        errors[root] = classification.Error ?? $"Unknown filesystem for search root: {root}";
                    }
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(classify), classification.FileSystem, "Unknown filesystem classification.");
            }

            if (request.NoMft || classification.FileSystem == RootFileSystem.Recursive)
            {
                recursiveRoots.Add(root);
            }
        }

        // Retained roots are exact roots; classify them for NTFS journal watch inclusion
        classifications.AddRange(ClassifyRetainedRoots(request, retainedRoots, classify, requestedNtfsDrives, watchExcludedRoots, errors));

        var sortedDrives = requestedNtfsDrives.OrderBy(drive => drive.Letter)
            .Select(drive => drive.ToString()).ToArray();

        return new DiscoveryScope(
            request,
            new RootPaths(searchRoots, retainedRoots, ignoredRoots, recursiveRoots, watchExcludedRoots),
            classifications,
            sortedDrives,
            errors);
    }

    sealed record RootPaths(
        IReadOnlyList<string> SearchRoots,
        IReadOnlyList<string> RetainedRoots,
        IReadOnlyList<string> IgnoredRoots,
        IReadOnlyList<string> RecursiveRoots,
        IReadOnlyList<string> WatchExcludedRoots);

    static List<RootClassification> ClassifyRetainedRoots(
        DiscoveryRequest request,
        IReadOnlyList<string> retainedRoots,
        Func<string, RootClassification> classify,
        HashSet<DriveKey> requestedNtfsDrives,
        List<string> watchExcludedRoots,
        Dictionary<string, string> errors)
    {
        var classifications = new List<RootClassification>();
        foreach (var root in retainedRoots)
        {
            var classification = classify(root);
            classifications.Add(classification);

            if (classification.FileSystem == RootFileSystem.Ntfs)
            {
                AddNtfsDrive(classification, root, requestedNtfsDrives);
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

        return classifications;
    }

    static void AddNtfsDrive(RootClassification classification, string root, HashSet<DriveKey> requestedNtfsDrives)
    {
        var pathOrDrive = (classification.NtfsDrive ?? root).Trim();
        var volume = WatchPath.Parse(pathOrDrive).Volume ?? pathOrDrive;
        if (DriveKey.TryParse(volume, out var drive))
        {
            requestedNtfsDrives.Add(drive);
        }
    }
}

using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace GitWizard.Discovery;

public enum DiscoveryOutcome
{
    CompleteEmpty = 0,
    CompleteNonempty = 1,
    Partial = 2,
    Unusable = 3
}

public enum DiscoveryRoute
{
    Indexed = 0,
    Mixed = 1,
    RecursiveOnly = 2
}

public enum DiscoverySource
{
    RepositoryList = 0,
    Snapshot = 1,
    Scan = 2,
    Replay = 3,
    Recursive = 4
}

public enum DiscoveryPurpose
{
    Ordinary = 0,
    Rebuild = 1,
    Watch = 2
}

public enum RepositoryCandidateKind
{
    Ordinary = 0,
    GitFile = 1,
    Bare = 2
}

public sealed record RepositoryCandidate(string Path, RepositoryCandidateKind Kind);

public sealed record DiscoveryRequest(
    GitWizardConfiguration Configuration,
    DiscoveryPurpose Purpose,
    bool AllowRepositoryList,
    bool NoMft,
    IReadOnlyList<string> RetainedRoots)
{
    public IReadOnlyList<string> RetainedRoots { get; } = Array.AsReadOnly<string>([.. RetainedRoots]);
}

public sealed record RootObservation(
    string Root,
    bool ExactRoot,
    bool Complete,
    DiscoverySource Source,
    DateTime? ScanTimestamp,
    string? Error);

public sealed record DiscoveryInventory
{
    public required DiscoveryOutcome Outcome { get; init; }

    public required DiscoveryRoute Route { get; init; }

    public required IReadOnlyList<string> Paths
    {
        get;
        init => field = Array.AsReadOnly<string>([.. value]);
    } = [];

    public required IReadOnlyList<string> RequestedNtfsDrives
    {
        get;
        init => field = Array.AsReadOnly<string>([.. value]);
    } = [];

    public required IReadOnlyList<RootObservation> Observations
    {
        get;
        init => field = Array.AsReadOnly<RootObservation>([.. value]);
    } = [];

    public required IReadOnlyList<MftRootStatus> RootStatuses
    {
        get;
        init => field = Array.AsReadOnly<MftRootStatus>([.. value]);
    } = [];

    public required IReadOnlyDictionary<string, string> Errors
    {
        get;
        init => field = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(value));
    } = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());

    public required IReadOnlyList<string> WatchExcludedRoots
    {
        get;
        init => field = Array.AsReadOnly<string>([.. value]);
    } = [];

    [JsonIgnore]
    public bool CanPublishFullInventory => Outcome is
        DiscoveryOutcome.CompleteEmpty or DiscoveryOutcome.CompleteNonempty;
}

public sealed record RecursiveDiscovery(
    IReadOnlyList<string> Paths,
    IReadOnlyList<string> FailedSubtrees,
    IReadOnlyDictionary<string, string> Errors)
{
    public IReadOnlyList<string> Paths { get; } = Array.AsReadOnly<string>([.. Paths]);

    public IReadOnlyList<string> FailedSubtrees { get; } = Array.AsReadOnly<string>([.. FailedSubtrees]);

    public IReadOnlyDictionary<string, string> Errors { get; } = new ReadOnlyDictionary<string, string>(
        new Dictionary<string, string>(Errors));
}

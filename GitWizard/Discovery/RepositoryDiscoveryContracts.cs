using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace GitWizard;

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

public enum RootFileSystem
{
    Ntfs = 0,
    Recursive = 1,
    Unknown = 2
}

public enum RepositoryCandidateKind
{
    Ordinary = 0,
    GitFile = 1,
    Bare = 2
}

public sealed record RepositoryCandidate(string Path, RepositoryCandidateKind Kind);

public sealed record RootClassification(
    string Root,
    RootFileSystem FileSystem,
    string? NtfsDrive,
    string? Error);

public sealed record DiscoveryRequest(
    GitWizardConfiguration Configuration,
    DiscoveryPurpose Purpose,
    bool AllowRepositoryList,
    bool NoMft,
    IReadOnlyList<string> RetainedRoots)
{
    readonly IReadOnlyList<string> _retainedRoots = Array.AsReadOnly(RetainedRoots.ToArray());
    public IReadOnlyList<string> RetainedRoots
    {
        get => _retainedRoots;
        init => _retainedRoots = Array.AsReadOnly(value.ToArray());
    }
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
    readonly IReadOnlyList<string> _paths = [];
    readonly IReadOnlyList<string> _requestedNtfsDrives = [];
    readonly IReadOnlyList<RootObservation> _observations = [];
    readonly IReadOnlyList<MftDriveStatus> _driveStatuses = [];
    readonly IReadOnlyDictionary<string, string> _errors = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());
    readonly IReadOnlyList<string> _watchExcludedRoots = [];

    public required DiscoveryOutcome Outcome { get; init; }

    public required DiscoveryRoute Route { get; init; }

    public required IReadOnlyList<string> Paths
    {
        get => _paths;
        init => _paths = Array.AsReadOnly(value.ToArray());
    }

    public required IReadOnlyList<string> RequestedNtfsDrives
    {
        get => _requestedNtfsDrives;
        init => _requestedNtfsDrives = Array.AsReadOnly(value.ToArray());
    }

    public required IReadOnlyList<RootObservation> Observations
    {
        get => _observations;
        init => _observations = Array.AsReadOnly(value.ToArray());
    }

    public required IReadOnlyList<MftDriveStatus> DriveStatuses
    {
        get => _driveStatuses;
        init => _driveStatuses = Array.AsReadOnly(value.ToArray());
    }

    public required IReadOnlyDictionary<string, string> Errors
    {
        get => _errors;
        init => _errors = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(value));
    }

    public required IReadOnlyList<string> WatchExcludedRoots
    {
        get => _watchExcludedRoots;
        init => _watchExcludedRoots = Array.AsReadOnly(value.ToArray());
    }

    [JsonIgnore]
    public bool CanPublishFullInventory => Outcome is
        DiscoveryOutcome.CompleteEmpty or DiscoveryOutcome.CompleteNonempty;
}

public sealed record RecursiveDiscovery(
    IReadOnlyList<string> Paths,
    IReadOnlyList<string> FailedSubtrees,
    IReadOnlyDictionary<string, string> Errors)
{
    readonly IReadOnlyList<string> _paths = Array.AsReadOnly(Paths.ToArray());
    public IReadOnlyList<string> Paths
    {
        get => _paths;
        init => _paths = Array.AsReadOnly(value.ToArray());
    }

    readonly IReadOnlyList<string> _failedSubtrees = Array.AsReadOnly(FailedSubtrees.ToArray());
    public IReadOnlyList<string> FailedSubtrees
    {
        get => _failedSubtrees;
        init => _failedSubtrees = Array.AsReadOnly(value.ToArray());
    }

    readonly IReadOnlyDictionary<string, string> _errors = new ReadOnlyDictionary<string, string>(
        new Dictionary<string, string>(Errors));
    public IReadOnlyDictionary<string, string> Errors
    {
        get => _errors;
        init => _errors = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(value));
    }
}

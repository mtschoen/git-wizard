using JetBrains.Annotations;

namespace GitWizard.Watch;

public enum RepositoryChangeKind { Changed, Created, Deleted, Renamed }

public sealed record RepositoryChangeEvent(
    [property: UsedImplicitly] string RepoRoot,
    [property: UsedImplicitly] RepositoryChangeKind Kind,
    [property: UsedImplicitly] string? NewPath = null);

using JetBrains.Annotations;

namespace GitWizard.Watch;

public enum RepositoryChangeKind { Changed = 0, Created = 1, Deleted = 2, Renamed = 3 }

public sealed record RepositoryChangeEvent(
    [property: UsedImplicitly] string RepoRoot,
    [property: UsedImplicitly] RepositoryChangeKind Kind,
    [property: UsedImplicitly] string? NewPath = null);

namespace GitWizard.Watch;

public enum RepositoryChangeKind { Changed, Created, Deleted, Renamed }

public sealed record RepositoryChangeEvent(
    string RepoRoot, RepositoryChangeKind Kind, string? NewPath = null);

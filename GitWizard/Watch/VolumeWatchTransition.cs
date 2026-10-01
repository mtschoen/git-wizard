namespace GitWizard.Watch;

public sealed record VolumeWatchTransition(
    Guid SessionId, string Volume, long Version, string State,
    string? Reason, DateTimeOffset ObservedAt);

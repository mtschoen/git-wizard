namespace GitWizard.Watch;

/// <summary>
/// Startup phases a watch preparation passes through. Broker process existence is never a phase:
/// <see cref="RequestingElevation"/> is raised when the lazy broker connect begins (the UAC
/// prompt), <see cref="BrokerConnected"/> when the pipe is up. Neither is readiness.
/// </summary>
public enum WatchStartupPhase
{
    Opening = 0,
    RequestingElevation = 1,
    BrokerConnected = 2,
    Scanning = 3,
    CatchingUp = 4,
    Discovering = 5,
    Reconciling = 6
}

public sealed record WatchStartupProgress(
    WatchStartupPhase Phase,
    string? Volume = null,
    VolumeReadinessSnapshot? Readiness = null);

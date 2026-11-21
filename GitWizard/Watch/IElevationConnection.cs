namespace GitWizard.Watch;

/// <summary>
/// Optional elevation boundary for a source that starts its privileged half while arming (the
/// Linux pkexec helper). The Linux counterpart of the Windows broker session's Connecting and
/// Connected events: <see cref="Connecting"/> is raised when the elevation prompt is requested,
/// <see cref="Connected"/> once the privileged half answers. Neither is readiness.
/// </summary>
public interface IElevationConnection
{
    event Action? Connecting;
    event Action? Connected;
}

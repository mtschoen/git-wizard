namespace GitWizard.Watch;

/// <summary>
/// Optional arm-time scan progress for a source that cold-scans while arming
/// (Windows index-backed sources). Sources with no scan phase (fanotify on Linux) do not
/// implement it. Samples raise only while ArmAndCatchUpAsync opens a fresh index session; an
/// arm that adopts a prepared discovery session scans nothing and reports nothing.
/// </summary>
public interface IVolumeScanProgress
{
    event Action<VolumeScanProgress>? ScanProgress;
}

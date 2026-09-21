namespace GitWizard.Watch;

/// <summary>Optional per-drive diagnostics for a source that can keep other drives running.</summary>
public interface IVolumeChangeDiagnostics
{
    event Action<string, string>? DriveFailed;

    /// <summary>
    /// Raised when a drive's journal checkpoint loss changes mid-run: a live watch that faulted
    /// because the journal moved past the position it had reached. Carries the loss the drive
    /// reported at that moment, so subscribers can refresh the drive's hint without a restart.
    /// </summary>
    event Action<JournalWarning>? JournalWarningDetected;
}

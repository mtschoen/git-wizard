namespace GitWizard.Watch;

/// <summary>Optional per-drive diagnostics for a source that can keep other drives running.</summary>
public interface IVolumeChangeDiagnostics
{
    event Action<string, string>? DriveFailed;
}

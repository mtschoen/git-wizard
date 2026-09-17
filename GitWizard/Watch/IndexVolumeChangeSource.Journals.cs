using MFTLib.Index;

namespace GitWizard.Watch;

public sealed partial class IndexVolumeChangeSource
{
    void AddJournalWarning(char letter, GitWizardConfiguration configuration, List<JournalWarning> warnings)
    {
        var owner = _owner ?? throw new InvalidOperationException("No index session available.");
        var drive = owner.IndexedDrives.FirstOrDefault(candidate =>
            char.ToUpperInvariant(candidate.DriveLetter) == letter);
        if (drive is null || configuration.IgnoredJournalWarnings.Contains(drive.VolumeSerial))
            return;

        try
        {
            var settings = QueryUsnJournalSettings(letter);
            if (settings.IsBelowRecommended)
                warnings.Add(new JournalWarning(letter.ToString(), drive.VolumeSerial,
                    settings.MaximumSize, UsnJournalRecommendations.RecommendedMaximumSize, settings.AllocationDelta));
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, $"Querying USN journal settings failed for drive {letter}.");
        }
    }
}

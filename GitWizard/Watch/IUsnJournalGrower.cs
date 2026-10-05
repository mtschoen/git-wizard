using MFTLib;

namespace GitWizard.Watch;

public interface IUsnJournalGrower
{
    UsnJournalSettings QueryUsnJournalSettings(char driveLetter);
    Task<UsnJournalSettings> GrowUsnJournalAsync(char driveLetter, long maximumSize, long allocationDelta, CancellationToken cancellationToken);
}

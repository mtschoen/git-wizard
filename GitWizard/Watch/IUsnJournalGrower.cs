using MFTLib;
using MFTLib.Index;

namespace GitWizard.Watch;

public interface IUsnJournalGrower
{
    UsnJournalSettings QueryUsnJournalSettings(DriveKey drive);
    Task<UsnJournalSettings> GrowUsnJournalAsync(DriveKey drive, UsnJournalSettings target, CancellationToken cancellationToken);
}

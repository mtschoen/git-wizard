using MFTLib;
using MFTLib.Index;

namespace GitWizard.Watch;

public interface IUsnJournalGrower
{
    UsnJournalSettings QueryUsnJournalSettings(VolumeKey volume);
    Task<UsnJournalSettings> GrowUsnJournalAsync(VolumeKey volume, UsnJournalSettings target, CancellationToken cancellationToken);
}

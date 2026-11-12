using MFTLib;
using MFTLib.Index;

namespace GitWizard.Watch;

public interface IUsnJournalGrower
{
    UsnJournalSettings QueryUsnJournalSettings(VolumeKey volume);
    long QueryMaximumRecommendedSize(VolumeKey volume);
    Task<UsnJournalGrowthResult> GrowUsnJournalAsync(
        VolumeKey volume,
        UsnJournalSettings target,
        CancellationToken cancellationToken,
        bool allowAboveMaximumRecommendedSize = false);
}

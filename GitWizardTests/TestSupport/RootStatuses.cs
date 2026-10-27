using MFTLib.Index;
using MFTLibTestExtensions;

namespace GitWizardTests.TestSupport;

static class RootStatuses
{
    public static RootStatus CreateRootStatus(char drive, RootState state,
        RootBlockStatus? block = null, RootFailure? failure = null, bool watchSupported = false)
    {
        var status = SyntheticNotifications.CreateRootStatus(new VolumeKey(drive), state, block, failure);
        return status with { Watch = status.Watch with { IsSupported = watchSupported } };
    }
}

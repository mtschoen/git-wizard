using MFTLib.Index;
using MFTLibTestExtensions;

namespace GitWizardTests.TestSupport;

internal static class DriveStatuses
{
    public static DriveStatus CreateDriveStatus(char drive, DriveState state,
        DriveBlockStatus? block = null, DriveFailure? failure = null, bool watchSupported = false)
    {
        var status = SyntheticNotifications.CreateDriveStatus(drive, state, block, failure);
        return status with { Watch = status.Watch with { IsSupported = watchSupported } };
    }
}

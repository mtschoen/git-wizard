using System.Runtime.CompilerServices;
using MFTLibTestExtensions;

namespace GitWizardTests;

static class JournalIsolationInitializer
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        JournalIsolation.ForbidLiveJournalReads();
        // A pkexec launch would raise a real polkit dialog on the desktop running the suite.
        GitWizard.Watch.Linux.LinuxWatchHelperLaunch.ForbidPkexec();
    }
}

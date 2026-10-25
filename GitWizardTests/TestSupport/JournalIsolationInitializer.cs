using System.Runtime.CompilerServices;
using MFTLibTestExtensions;

namespace GitWizardTests;

static class JournalIsolationInitializer
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        JournalIsolation.ForbidLiveJournalReads();
    }
}

using System.Runtime.CompilerServices;
using MFTLibTestExtensions;

namespace GitWizardTests;

internal static class JournalIsolationInitializer
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        JournalIsolation.ForbidLiveJournalReads();
    }
}

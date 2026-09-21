using System.Runtime.CompilerServices;
using MFTLibTestExtensions;

namespace GitWizardTests;

internal static class CacheDirectoryIsolationInitializer
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        CacheDirectoryIsolation.ForbidDefaultCacheDirectory();
        JournalIsolation.ForbidLiveJournalReads();
    }
}

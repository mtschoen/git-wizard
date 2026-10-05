using MFTLibTestExtensions;

namespace GitWizardTests.TestSupport;

internal static class ScriptedWatchSourceWaits
{
    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Waits until the index has started at least <paramref name="count" /> watches. It polls
    /// the source's recorded watches, so it cannot miss a start that happened before the call.
    /// </summary>
    public static Task WaitForWatchesAsync(this ScriptedWatchSource source, int count = 1) =>
        AsyncCondition.WaitUntilAsync(() => source.Watches.Count >= count, HangGuard);
}

using GitWizard;
using MFTLib.Index;
using MFTLibTestExtensions;

namespace GitWizardTests.TestSupport;

/// <summary>What a requested drive does while a synthetic session opens it.</summary>
enum SyntheticDriveBehavior
{
    /// <summary>The synthetic producer completes a scan of the fixture tree.</summary>
    Ready = 0,

    /// <summary>The synthetic producer throws, leaving the drive Failed.</summary>
    ProducerFails = 1,

    /// <summary>The drive root does not exist, leaving the drive Offline.</summary>
    Offline = 2
}

/// <summary>
/// Opens real <see cref="MftIndexSession" /> values whose drive statuses come from the synthetic producer,
/// so tests observe the statuses the library reports instead of replacing them.
/// </summary>
static class StatusSessions
{
    static string ProducerFailureMessage(char drive) => $"Drive {drive} failure";

    /// <summary>
    /// A discovery open callback over <paramref name="files" />; the fixture is a parameter so a test's
    /// callback does not capture its own disposable local.
    /// </summary>
    public static Func<IReadOnlyList<string>, CacheMode, IProgress<IndexScanProgress>?, CancellationToken,
        Task<MftIndexSession>> Opener(PackedIndexFixture files,
        params (char Letter, SyntheticDriveBehavior Behavior)[] drives) =>
        (_, _, _, _) => OpenAsync(files, drives);

    /// <summary>Like <see cref="Opener" />, with every later production failing as a failed rescan does.</summary>
    public static Func<IReadOnlyList<string>, CacheMode, IProgress<IndexScanProgress>?, CancellationToken,
        Task<MftIndexSession>> OpenerFailingRescan(PackedIndexFixture files, string message,
        params (char Letter, SyntheticDriveBehavior Behavior)[] drives) =>
        async (_, _, _, _) =>
        {
            var session = await OpenAsync(files, drives).ConfigureAwait(false);
            files.BeforeProduceAsync = (_, _) => throw new IOException(message);
            return session;
        };

    public static Task<MftIndexSession> OpenAsync(PackedIndexFixture files,
        params (char Letter, SyntheticDriveBehavior Behavior)[] drives) =>
        OpenAsync(files, new ScriptedWatchSource(), drives);

    public static async Task<MftIndexSession> OpenAsync(PackedIndexFixture files, ScriptedWatchSource watchSource,
        params (char Letter, SyntheticDriveBehavior Behavior)[] drives)
    {
        var failing = drives.Where(item => item.Behavior == SyntheticDriveBehavior.ProducerFails)
            .Select(item => item.Letter).ToHashSet();
        files.BeforeProduceAsync = (drive, _) => failing.Contains(drive.ToString()[0])
            ? throw new IOException(ProducerFailureMessage(drive.ToString()[0]))
            : Task.CompletedTask;
        var absentRoot = Path.Combine(files.DirectoryPath, "absent");
        var indexedDrives = drives.Select(item => new IndexedDrive(item.Letter,
            item.Behavior == SyntheticDriveBehavior.Offline ? absentRoot : files.TreePath, 1)).ToArray();
        var index = await FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = indexedDrives,
            CacheMode = CacheMode.Disabled,
            MftSource = SyntheticIndexSource.Create(files.Producer, watchSource)
        }, CancellationToken.None).ConfigureAwait(false);
        var scanErrors = index.Drives
            .Where(status => status.State is DriveState.Failed or DriveState.Offline)
            .ToDictionary(status => status.Drive.ToString(),
                status => status.Failure?.Message ?? $"Drive is {status.State}.");
        return new MftIndexSession(index, new CountingOwner(),
            (string[])[.. drives.Select(item => item.Letter.ToString())], indexedDrives, scanErrors);
    }
}

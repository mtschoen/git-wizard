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
        (roots, _, _, _) => OpenRequestedAsync(files, roots, drives);

    /// <summary>Like <see cref="Opener" />, with every later production failing as a failed rescan does.</summary>
    public static Func<IReadOnlyList<string>, CacheMode, IProgress<IndexScanProgress>?, CancellationToken,
        Task<MftIndexSession>> OpenerFailingRescan(PackedIndexFixture files, string message,
        params (char Letter, SyntheticDriveBehavior Behavior)[] drives) =>
        async (roots, _, _, _) =>
        {
            var session = await OpenRequestedAsync(files, roots, drives).ConfigureAwait(false);
            files.BeforeProduceAsync = (_, _) => throw new IOException(message);
            return session;
        };

    static async Task<MftIndexSession> OpenRequestedAsync(PackedIndexFixture files, IReadOnlyList<string> roots,
        (char Letter, SyntheticDriveBehavior Behavior)[] drives)
    {
        var failing = drives.Where(item => item.Behavior == SyntheticDriveBehavior.ProducerFails)
            .Select(item => item.Letter).ToHashSet();
        files.BeforeProduceAsync = (root, _) => failing.Contains(root.ToString()[0])
            ? throw new IOException(ProducerFailureMessage(root.ToString()[0])) : Task.CompletedTask;
        var source = SyntheticIndexSource.Create(files.Producer, new ScriptedWatchSource());
        var indices = await Task.WhenAll(roots.Select((root, position) => FileIndex.OpenAsync(
            SyntheticVolumeFacts.Apply(new FileIndexOptions
            {
                Root = RootKey.FromDirectory(root),
                CacheMode = CacheMode.Disabled,
                MftSource = source
            }, 1, volume: new VolumeKey(drives.Length == 1 ? drives[0].Letter : drives[position].Letter), volumeRootDirectory: root),
            CancellationToken.None))).ConfigureAwait(false);
        var errors = indices.Select(index => index.Status)
            .Where(status => status.State is RootState.Failed or RootState.Offline)
            .ToDictionary(status => status.Root.ToString(), status => status.Failure?.Message ?? $"Drive is {status.State}.");
        return new MftIndexSession(indices, new CountingOwner(), roots, errors);
    }

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
        var indexedDrives = drives.Select(item => (RootKey)new VolumeKey(item.Letter)).ToArray();
        var indices = await Task.WhenAll(indexedDrives.Select(root => FileIndex.OpenAsync(Configure(new FileIndexOptions
        {
            Root = root,
            CacheMode = CacheMode.Disabled,
            MftSource = SyntheticIndexSource.Create(files.Producer, watchSource)
        }), CancellationToken.None))).ConfigureAwait(false);
        var scanErrors = indices.Select(index => index.Status)
            .Where(status => status.State is RootState.Failed or RootState.Offline)
            .ToDictionary(status => status.Root.ToString(),
                status => status.Failure?.Message ?? $"Drive is {status.State}.");
        return new MftIndexSession(indices, new CountingOwner(),
            (string[])[.. drives.Select(item => item.Letter.ToString())], scanErrors);

        FileIndexOptions Configure(FileIndexOptions options)
        {
            var volume = options.Root!.Value.TryGetVolume(out var value) ? value : throw new InvalidOperationException();
            return drives.Single(item => item.Letter == volume.Letter).Behavior == SyntheticDriveBehavior.Offline
                ? SyntheticVolumeFacts.Apply(options, 1, volume: volume, volumeRootDirectory: absentRoot)
                : files.ConfigureOptions(options);
        }
    }
}

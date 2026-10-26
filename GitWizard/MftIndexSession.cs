using MFTLib;
using MFTLib.Index;

namespace GitWizard;

/// <summary>Owns an index and its broker session through discovery and live watch.</summary>
public sealed partial class MftIndexSession : IAsyncDisposable
{
    readonly IAsyncDisposable? _connectionOwner;
    MftIndexDiagnostics? _diagnostics;
    readonly Lock _disposalLock = new();
    readonly string? _cacheDirectory;
    Task? _disposal;
    int _disposed;
    IReadOnlyDictionary<string, string> _identityFailures =
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    public FileIndex Index { get; }
    public IReadOnlyList<string> Drives { get; private set; }
    public IReadOnlyList<IndexedDrive> IndexedDrives { get; }
    public IReadOnlyDictionary<string, string> ScanErrors { get; private set; }
    public bool IsFaulted => BrokerSession?.Ended.IsCompleted == true;
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    internal MftIndexSession(FileIndex index, IAsyncDisposable? connectionOwner,
        IReadOnlyList<string> drives, IReadOnlyList<IndexedDrive> indexedDrives,
        IReadOnlyDictionary<string, string> scanErrors, string? cacheDirectory = null)
    {
        Index = index;
        _connectionOwner = connectionOwner;
        Drives = Array.AsReadOnly<string>([.. drives]);
        IndexedDrives = Array.AsReadOnly<IndexedDrive>([.. indexedDrives]);
        ScanErrors = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(scanErrors, StringComparer.OrdinalIgnoreCase));
        _cacheDirectory = cacheDirectory;
    }

    internal static Task<MftIndexSession> OpenAsync(IReadOnlyList<IndexedDrive> drives,
        BrokerSession brokerSession,
        string cacheDirectory, IProgress<IndexScanProgress>? progress, CancellationToken cancellationToken) =>
        OpenAsync(drives, brokerSession, cacheDirectory, CacheMode.Persistent, progress, cancellationToken);

    /// <summary>Opens the drives over <paramref name="cacheDirectory" /> without connecting the broker eagerly.</summary>
    /// <param name="drives">Volume identities to open.</param>
    /// <param name="brokerSession">Owns the broker process, launched lazily on first use; ownership transfers here.</param>
    /// <param name="cacheDirectory">A fully resolved policy directory, normally from <see cref="MftIndexCachePolicy" />.</param>
    /// <param name="cacheMode">Persistent scans a drive without a usable cached block; ExistingOnly refuses it.</param>
    /// <param name="progress">Receives scan progress.</param>
    /// <param name="cancellationToken">Cancels opening.</param>
    internal static async Task<MftIndexSession> OpenAsync(IReadOnlyList<IndexedDrive> drives,
        BrokerSession brokerSession,
        string cacheDirectory, CacheMode cacheMode, IProgress<IndexScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (cacheMode is not (CacheMode.Persistent or CacheMode.ExistingOnly))
        {
            throw new ArgumentOutOfRangeException(nameof(cacheMode));
        }

        MftIndexDiagnostics? diagnostics = null;
        var transferred = false;
        FileIndex? index = null;
        try
        {
            diagnostics = MftIndexDiagnostics.CreateForGitWizardLog();
            cancellationToken.ThrowIfCancellationRequested();
            index = await FileIndex.OpenAsync(CreateIndexOptions(drives, brokerSession, cacheDirectory, cacheMode,
                progress, diagnostics), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var session = new MftIndexSession(index, brokerSession,
                (string[])[.. drives.Select(drive => drive.Drive.ToString())], drives,
                CollectScanErrors(index), cacheDirectory)
            {
                BrokerSession = brokerSession,
                _diagnostics = diagnostics
            };
            transferred = true;
            index = null;
            diagnostics = null;
            return session;
        }
        finally
        {
            try
            {
                if (!transferred)
                {
                    await DisposeResourcesAsync(index, brokerSession, null).ConfigureAwait(false);
                }
            }
            finally
            {
                await DisposeResourcesAsync(null, null, diagnostics).ConfigureAwait(false);
            }
        }
    }

    static FileIndexOptions CreateIndexOptions(IReadOnlyList<IndexedDrive> drives, BrokerSession brokerSession,
        string cacheDirectory, CacheMode cacheMode, IProgress<IndexScanProgress>? progress,
        MftIndexDiagnostics diagnostics)
    {
        // The session stays lazy in both cache modes: a warm or snapshot open never launches the broker,
        // while a later watch or explicit rescan connects through the same owned session.
        return new FileIndexOptions
        {
            Drives = drives,
            CacheDirectory = cacheDirectory,
            CacheTag = MftIndexCachePolicy.CacheTag,
            CacheMode = cacheMode,
            RetainedFileNames = MftIndexCachePolicy.DirectoryScanFileNames,
            MftSource = brokerSession.CreateIndexSource(),
            ScanProgress = progress,
            Diagnostics = diagnostics.Report
        };
    }

    static Dictionary<string, string> CollectScanErrors(FileIndex index)
    {
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var drive in index.Drives)
        {
            if (drive.State is DriveState.Failed or DriveState.Offline)
            {
                errors[drive.Drive.ToString()] = drive.Failure?.Message ?? $"Drive is {drive.State}.";
            }
        }
        return errors;
    }

    /// <summary>
    /// Disposes in dependency order: the index stops first, so deletions it performs while closing are
    /// still relayed and no watch uses the broker; then the broker; then the relay drains. A failure in
    /// one step never skips the later ones.
    /// </summary>
    static async Task DisposeResourcesAsync(FileIndex? index, IAsyncDisposable? connection,
        MftIndexDiagnostics? diagnostics)
    {
        try
        {
            if (index != null)
            {
                await index.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                if (connection != null)
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                if (diagnostics != null)
                {
                    await diagnostics.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>
    /// Captures each requested drive's current index status, in requested order. Every call returns
    /// a new read-only list; a drive whose identity failed carries that reason instead of a status.
    /// </summary>
    public IReadOnlyList<MftDriveStatus> CaptureDriveStatuses()
    {
        var indexed = Index.Drives.ToDictionary(status =>
            status.Drive.ToString(), StringComparer.OrdinalIgnoreCase);
        return Array.AsReadOnly<MftDriveStatus>([.. Drives.Select(drive => new MftDriveStatus(drive,
            indexed.TryGetValue(drive, out var status) ? status with { } : null,
            _identityFailures.GetValueOrDefault(drive)))]);
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposalLock)
        {
            return new ValueTask(_disposal ??= DisposeCoreAsync());
        }
    }

    internal BrokerSession? BrokerSession { get; init; }

    Task DisposeCoreAsync()
    {
        Volatile.Write(ref _disposed, 1);
        return DisposeResourcesAsync(Index, _connectionOwner, _diagnostics);
    }
}

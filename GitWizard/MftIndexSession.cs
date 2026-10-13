using MFTLib;
using MFTLib.Index;

namespace GitWizard;

/// <summary>Owns an index and its broker session through discovery and live watch.</summary>
public sealed partial class MftIndexSession : IAsyncDisposable
{
    readonly IAsyncDisposable? _connectionOwner;
    MftIndexDiagnostics? _diagnostics;
    BrokerSession? _brokerSession;
    readonly Lock _disposalLock = new();
    readonly string? _cacheDirectory;
    Task? _disposal;
    int _disposed;
    IReadOnlyDictionary<string, string> _identityFailures =
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    public FileIndex Index { get; }
    public IReadOnlyList<string> Drives { get; private set; }
    public IReadOnlyList<IndexedDrive> IndexedDrives { get; private set; }
    public IReadOnlyDictionary<string, string> ScanErrors { get; private set; }
    public bool IsFaulted => _brokerSession?.Ended.IsCompleted == true;
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    internal MftIndexSession(FileIndex index, IAsyncDisposable? connectionOwner,
        IReadOnlyList<string> drives, IReadOnlyList<IndexedDrive> indexedDrives,
        IReadOnlyDictionary<string, string> scanErrors, string? cacheDirectory = null)
    {
        Index = index;
        _connectionOwner = connectionOwner;
        Drives = Array.AsReadOnly(drives.ToArray());
        IndexedDrives = Array.AsReadOnly(indexedDrives.ToArray());
        ScanErrors = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(scanErrors, StringComparer.OrdinalIgnoreCase));
        _cacheDirectory = cacheDirectory;
    }

    internal static Task<MftIndexSession> OpenAsync(IReadOnlyList<IndexedDrive> drives,
        BrokerSession brokerSession,
        string cacheDirectory, IProgress<IndexScanProgress>? progress, CancellationToken cancellationToken) =>
        OpenAsync(drives, brokerSession, cacheDirectory, MftIndexOpenIntent.Persistent, progress, cancellationToken);

    /// <summary>Opens the drives over <paramref name="cacheDirectory" /> without connecting the broker eagerly.</summary>
    /// <param name="drives">Volume identities to open.</param>
    /// <param name="brokerSession">Owns the broker process, launched lazily on first use; ownership transfers here.</param>
    /// <param name="cacheDirectory">A fully resolved policy directory, normally from <see cref="MftIndexCachePolicy" />.</param>
    /// <param name="intent">Whether a drive without a usable cached block is scanned or refused.</param>
    /// <param name="progress">Receives scan progress.</param>
    /// <param name="cancellationToken">Cancels opening.</param>
    internal static async Task<MftIndexSession> OpenAsync(IReadOnlyList<IndexedDrive> drives,
        BrokerSession brokerSession,
        string cacheDirectory, MftIndexOpenIntent intent, IProgress<IndexScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (intent is not (MftIndexOpenIntent.Persistent or MftIndexOpenIntent.Snapshot))
            throw new ArgumentOutOfRangeException(nameof(intent));
        MftIndexDiagnostics? diagnostics = null;
        var transferred = false;
        FileIndex? index = null;
        try
        {
            diagnostics = MftIndexDiagnostics.CreateForGitWizardLog();
            cancellationToken.ThrowIfCancellationRequested();
            index = await FileIndex.OpenAsync(CreateIndexOptions(drives, brokerSession, cacheDirectory, intent,
                progress, diagnostics), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var session = new MftIndexSession(index, brokerSession,
                drives.Select(drive => char.ToUpperInvariant(drive.DriveLetter).ToString()).ToArray(), drives,
                CollectScanErrors(index), cacheDirectory)
            {
                _brokerSession = brokerSession,
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
                    await DisposeResourcesAsync(index, brokerSession, null).ConfigureAwait(false);
            }
            finally
            {
                await DisposeResourcesAsync(null, null, diagnostics).ConfigureAwait(false);
            }
        }
    }

    static FileIndexOptions CreateIndexOptions(IReadOnlyList<IndexedDrive> drives, BrokerSession brokerSession,
        string cacheDirectory, MftIndexOpenIntent intent, IProgress<IndexScanProgress>? progress,
        MftIndexDiagnostics diagnostics)
    {
        // The session stays lazy in both intents: a warm or snapshot open never launches the broker,
        // while a later watch or explicit rescan connects through the same owned session.
        return new FileIndexOptions
        {
            Drives = drives,
            CacheDirectory = cacheDirectory,
            CacheTag = MftIndexCachePolicy.CacheTag,
            NoCache = false,
            InitialOpenCacheOnly = intent == MftIndexOpenIntent.Snapshot,
            ProducerPolicy = ProducerPolicy.Mft,
            MftSource = brokerSession.CreateIndexSource(new MftScanOptions
            {
                RetainedFileNames = MftIndexCachePolicy.DirectoryScanFileNames
            }),
            Progress = progress,
            Diagnostics = diagnostics.Report
        };
    }

    static Dictionary<string, string> CollectScanErrors(FileIndex index)
    {
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var drive in index.Drives)
        {
            if (drive.State is DriveState.Failed or DriveState.Offline)
                errors[drive.DriveLetter.ToString()] = drive.Failure?.Message ?? $"Drive is {drive.State}.";
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
                await index.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (connection != null)
                    await connection.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                if (diagnostics != null)
                    await diagnostics.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    internal Func<IReadOnlyList<MftDriveStatus>>? DriveStatusesOverride { get; set; }

    /// <summary>
    /// Captures each requested drive's current index status, in requested order. Every call returns
    /// a new read-only list; a drive whose identity failed carries that reason instead of a status.
    /// </summary>
    public IReadOnlyList<MftDriveStatus> CaptureDriveStatuses()
    {
        if (DriveStatusesOverride is not null)
            return DriveStatusesOverride();

        var indexed = Index.Drives.ToDictionary(status =>
            char.ToUpperInvariant(status.DriveLetter).ToString(), StringComparer.OrdinalIgnoreCase);
        return Array.AsReadOnly(Drives.Select(drive => new MftDriveStatus(drive,
            indexed.TryGetValue(drive, out var status) ? status with { } : null,
            _identityFailures.GetValueOrDefault(drive))).ToArray());
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposalLock)
            return new ValueTask(_disposal ??= DisposeCoreAsync());
    }

    internal BrokerSession? BrokerSession
    {
        get => _brokerSession;
        set => _brokerSession = value;
    }

    Task DisposeCoreAsync()
    {
        Volatile.Write(ref _disposed, 1);
        return DisposeResourcesAsync(Index, _connectionOwner, _diagnostics);
    }
}

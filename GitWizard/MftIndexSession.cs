using System.Runtime.ExceptionServices;
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

    /// <summary>One index per opened root, in requested order.</summary>
    public IReadOnlyList<FileIndex> Indices { get; }

    /// <summary>Answers cross-root queries over <see cref="Indices" />.</summary>
    public FileIndexSet IndexSet { get; }

    public IReadOnlyList<string> Drives { get; private set; }
    public IReadOnlyList<IndexedRoot> IndexedRoots { get; }
    public IReadOnlyDictionary<string, string> ScanErrors { get; private set; }
    public bool IsFaulted => BrokerSession?.Ended.IsCompleted == true;
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    internal MftIndexSession(IReadOnlyList<FileIndex> indices, IAsyncDisposable? connectionOwner,
        IReadOnlyList<string> drives, IReadOnlyList<IndexedRoot> indexedRoots,
        IReadOnlyDictionary<string, string> scanErrors, string? cacheDirectory = null)
    {
        Indices = Array.AsReadOnly<FileIndex>([.. indices]);
        IndexSet = new FileIndexSet(Indices);
        _connectionOwner = connectionOwner;
        Drives = Array.AsReadOnly<string>([.. drives]);
        IndexedRoots = Array.AsReadOnly<IndexedRoot>([.. indexedRoots]);
        ScanErrors = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(scanErrors, StringComparer.OrdinalIgnoreCase));
        _cacheDirectory = cacheDirectory;
    }

    internal static Task<MftIndexSession> OpenAsync(IReadOnlyList<IndexedRoot> drives,
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
    internal static async Task<MftIndexSession> OpenAsync(IReadOnlyList<IndexedRoot> drives,
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
        FileIndex[] indices = [];
        try
        {
            diagnostics = MftIndexDiagnostics.CreateForGitWizardLog();
            cancellationToken.ThrowIfCancellationRequested();
            var openDiagnostics = diagnostics;
            indices = await OpenIndicesAsync(drives, root => FileIndex.OpenAsync(CreateIndexOptions(root,
                brokerSession, cacheDirectory, cacheMode, progress, openDiagnostics), cancellationToken)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var session = new MftIndexSession(indices, brokerSession,
                (string[])[.. drives.Select(root => root.Root.ToString())], drives,
                CollectScanErrors(indices), cacheDirectory)
            {
                BrokerSession = brokerSession,
                _diagnostics = diagnostics
            };
            transferred = true;
            indices = [];
            diagnostics = null;
            return session;
        }
        finally
        {
            try
            {
                if (!transferred)
                {
                    await DisposeResourcesAsync(indices, brokerSession, null).ConfigureAwait(false);
                }
            }
            finally
            {
                await DisposeResourcesAsync([], null, diagnostics).ConfigureAwait(false);
            }
        }
    }

    // Every root opens concurrently; if any open throws, the roots that did open are disposed before it propagates.
    internal static async Task<FileIndex[]> OpenIndicesAsync(IReadOnlyList<IndexedRoot> roots,
        Func<IndexedRoot, Task<FileIndex>> openIndex)
    {
        var opens = new List<Task<FileIndex>>();
        try
        {
            // A root whose open throws before returning a task must not strand the opens already started.
            foreach (var root in roots)
            {
                opens.Add(openIndex(root));
            }

            return await Task.WhenAll(opens).ConfigureAwait(false);
        }
        catch
        {
            await DisposeResourcesAsync(await CompletedIndicesAsync(opens).ConfigureAwait(false), null, null)
                .ConfigureAwait(false);
            throw;
        }
    }

    static async Task<FileIndex[]> CompletedIndicesAsync(IReadOnlyList<Task<FileIndex>> opens)
    {
        var completed = new List<FileIndex>();
        foreach (var open in opens)
        {
            try
            {
                completed.Add(await open.ConfigureAwait(false));
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // The failed open is already propagating from the caller; only the ones that succeeded are collected.
                GitWizardLog.Log($"Index open failed: {exception.Message}", GitWizardLog.LogType.Verbose);
            }
        }

        return [.. completed];
    }

    static FileIndexOptions CreateIndexOptions(IndexedRoot root, BrokerSession brokerSession,
        string cacheDirectory, CacheMode cacheMode, IProgress<IndexScanProgress>? progress,
        MftIndexDiagnostics diagnostics)
    {
        // The session stays lazy in both cache modes: a warm or snapshot open never launches the broker,
        // while a later watch or explicit rescan connects through the same owned session.
        return new FileIndexOptions
        {
            Root = root,
            CacheDirectory = cacheDirectory,
            CacheTag = MftIndexCachePolicy.CacheTag,
            CacheMode = cacheMode,
            RetainedFileNames = MftIndexCachePolicy.DirectoryScanFileNames,
            MftSource = brokerSession.CreateIndexSource(),
            ScanProgress = progress,
            Diagnostics = diagnostics.Report
        };
    }

    static Dictionary<string, string> CollectScanErrors(IEnumerable<FileIndex> indices)
    {
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var status in indices.Select(index => index.Status))
        {
            if (status.State is RootState.Failed or RootState.Offline)
            {
                errors[status.Root.ToString()] = status.Failure?.Message ?? $"Drive is {status.State}.";
            }
        }
        return errors;
    }

    /// <summary>
    /// Disposes in dependency order: the index stops first, so deletions it performs while closing are
    /// still relayed and no watch uses the broker; then the broker; then the relay drains. A failure in
    /// one step never skips the later ones.
    /// </summary>
    static async Task DisposeResourcesAsync(IReadOnlyList<FileIndex> indices, IAsyncDisposable? connection,
        MftIndexDiagnostics? diagnostics)
    {
        try
        {
            await DisposeIndicesAsync(indices).ConfigureAwait(false);
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

    static async Task DisposeIndicesAsync(IReadOnlyList<FileIndex> indices)
    {
        List<Exception>? failures = null;
        foreach (var index in indices)
        {
            try
            {
                await index.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                (failures ??= []).Add(exception);
            }
        }

        if (failures is { Count: 1 })
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        if (failures is not null)
        {
            throw new AggregateException(failures);
        }
    }

    /// <summary>
    /// Captures each requested drive's current index status, in requested order. Every call returns
    /// a new read-only list; a drive whose identity failed carries that reason instead of a status.
    /// </summary>
    public IReadOnlyList<MftRootStatus> CaptureRootStatuses()
    {
        var indexed = Indices.Select(index => index.Status).ToDictionary(status =>
            status.Root.ToString(), StringComparer.OrdinalIgnoreCase);
        return Array.AsReadOnly<MftRootStatus>([.. Drives.Select(drive => new MftRootStatus(drive,
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

    /// <summary>Finds the index that holds <paramref name="root" />, or null when that root was never opened.</summary>
    internal FileIndex? IndexFor(RootKey root) => Indices.FirstOrDefault(index => index.Status.Root == root);

    Task DisposeCoreAsync()
    {
        Volatile.Write(ref _disposed, 1);
        return DisposeResourcesAsync(Indices, _connectionOwner, _diagnostics);
    }
}

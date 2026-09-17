using MFTLib;
using MFTLib.Index;

namespace GitWizard;

/// <summary>Owns an index and its borrowed broker connection through discovery and live watch.</summary>
public sealed partial class MftIndexSession : IAsyncDisposable
{
    readonly IAsyncDisposable? _connectionOwner;
    BrokerConnection? _brokerConnection;
    readonly Lock _disposalLock = new();
    Task? _disposal;
    int _disposed;

    public FileIndex Index { get; }
    public IReadOnlyList<string> Drives { get; private set; }
    public IReadOnlyList<IndexedDrive> IndexedDrives { get; private set; }
    public IReadOnlyDictionary<string, string> ScanErrors { get; private set; }
    public bool IsFaulted => _brokerConnection?.IsFaulted == true;
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    internal MftIndexSession(FileIndex index, IAsyncDisposable? connectionOwner,
        IReadOnlyList<string> drives, IReadOnlyList<IndexedDrive> indexedDrives, IReadOnlyDictionary<string, string> scanErrors)
    {
        Index = index;
        _connectionOwner = connectionOwner;
        Drives = Array.AsReadOnly(drives.ToArray());
        IndexedDrives = Array.AsReadOnly(indexedDrives.ToArray());
        ScanErrors = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(scanErrors, StringComparer.OrdinalIgnoreCase));
    }

    internal static async Task<MftIndexSession> OpenAsync(IReadOnlyList<IndexedDrive> drives,
        Func<CancellationToken, Task<JournalBrokerClient>> connectAsync,
        string cacheDirectory, IProgress<IndexScanProgress>? progress, CancellationToken cancellationToken)
    {
        var connection = new BrokerConnection(connectAsync);
        var transferred = false;
        FileIndex? index = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var producer = new BrokerMftBlockProducer(connection.ConnectAsync, new BrokerScanOptions
            {
                Profile = BrokerScanProfile.DirectoryIndex,
                KeepFileNames = [GitWizardApi.GitEntryName]
            });
            index = await FileIndex.OpenAsync(new FileIndexOptions
            {
                Drives = drives,
                CacheDirectory = cacheDirectory,
                NoCache = true,
                ProducerPolicy = ProducerPolicy.Mft,
                MftProducer = producer.CreateProducer(),
                WatchSource = producer.CreateWatchSource(),
                Progress = progress
            }, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var drive in index.Drives)
            {
                if (drive.State is DriveState.Failed or DriveState.Offline)
                    errors[drive.DriveLetter.ToString()] = drive.MftProducerFailureMessage ?? $"Drive is {drive.State}.";
            }
            var session = new MftIndexSession(index, connection.Client,
                drives.Select(drive => char.ToUpperInvariant(drive.DriveLetter).ToString()).ToArray(), drives, errors)
            {
                _brokerConnection = connection
            };
            transferred = true;
            index = null;
            return session;
        }
        finally
        {
            try
            {
                if (index != null)
                    await index.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                if (!transferred)
                {
                    connection.Detach();
                    if (connection.Client != null)
                        await connection.Client.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposalLock)
            return new ValueTask(_disposal ??= DisposeCoreAsync());
    }

    internal JournalBrokerClient? GetBrokerClient() => _brokerConnection?.Client;

    async Task DisposeCoreAsync()
    {
        Volatile.Write(ref _disposed, 1);
        try
        {
            await Index.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _brokerConnection?.Detach();
            if (_connectionOwner != null)
                await _connectionOwner.DisposeAsync().ConfigureAwait(false);
        }
    }

    sealed class BrokerConnection(Func<CancellationToken, Task<JournalBrokerClient>> connectAsync)
    {
        readonly Lock _connectionLock = new();
        Task<JournalBrokerClient>? _connection;
        public JournalBrokerClient? Client { get; private set; }
        int _faulted;
        public bool IsFaulted => Volatile.Read(ref _faulted) != 0;

        public Task<JournalBrokerClient> ConnectAsync(CancellationToken cancellationToken)
        {
            lock (_connectionLock)
                return _connection ??= ConnectCoreAsync(cancellationToken);
        }

        async Task<JournalBrokerClient> ConnectCoreAsync(CancellationToken cancellationToken)
        {
            Client = await connectAsync(cancellationToken).ConfigureAwait(false);
            Client.BrokerDied += OnBrokerDied;
            return Client;
        }

        void OnBrokerDied(string reason) => Volatile.Write(ref _faulted, 1);

        public void Detach()
        {
            if (Client != null)
                Client.BrokerDied -= OnBrokerDied;
        }
    }
}

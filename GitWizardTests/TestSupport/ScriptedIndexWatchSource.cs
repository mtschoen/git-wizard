using System.Runtime.CompilerServices;
using System.Threading.Channels;
using MFTLib.Index;

namespace GitWizardTests.TestSupport;

internal sealed class ScriptedIndexWatchSource : IIndexWatchSource
{
    readonly Lock _gate = new();
    readonly Dictionary<char, ScriptedDriveWatch> _watches = [];
    readonly List<IndexWatchTarget> _targets = [];
    readonly TaskCompletionSource<IReadOnlyList<IndexWatchTarget>> _started =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool AutoCatchUp { get; set; } = true;
    public Task<IReadOnlyList<IndexWatchTarget>> Started => _started.Task;
    public Task Completed => _completed.Task;
    public bool WasCancelled { get; private set; }

    /// <summary>Every start in order, including the restarts of a recovered drive.</summary>
    public IReadOnlyList<IndexWatchTarget> Targets
    {
        get { lock (_gate) return _targets.ToArray(); }
    }

    /// <summary>By drive: an exception its next starts throw, or null to start normally.</summary>
    public Func<char, Exception?>? StartFailure { get; set; }

    public Task<IIndexDriveWatch> StartAsync(IndexWatchTarget target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var driveLetter = char.ToUpperInvariant(target.DriveLetter);
        if (StartFailure?.Invoke(driveLetter) is { } failure)
            return Task.FromException<IIndexDriveWatch>(failure);
        ScriptedDriveWatch watch;
        lock (_gate)
        {
            // MFTLib retires a drive's previous watch before it restarts the drive.
            if (_watches.TryGetValue(driveLetter, out var previous) && !previous.IsFinished)
                throw new InvalidOperationException($"Drive {driveLetter} already has a running scripted watch.");
            watch = new ScriptedDriveWatch(driveLetter, this);
            _watches[driveLetter] = watch;
            _targets.Add(target);
        }
        _started.TrySetResult([target]);
        if (AutoCatchUp)
            Publish(driveLetter, new DriveCaughtUp());
        return Task.FromResult<IIndexDriveWatch>(watch);
    }

    public void Publish(char driveLetter, WatchStreamItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        GetWatch(driveLetter).Publish(item);
    }

    public void FailDrive(char driveLetter, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        GetWatch(driveLetter).Fail(new DriveWatchFaultException(char.ToUpperInvariant(driveLetter),
            exception.Message, exception));
    }

    public void LoseChannel(char driveLetter, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        GetWatch(driveLetter).Fail(exception);
    }

    public void Complete(char driveLetter) => GetWatch(driveLetter).Complete();

    ScriptedDriveWatch GetWatch(char driveLetter)
    {
        lock (_gate)
        {
            var normalized = char.ToUpperInvariant(driveLetter);
            return _watches.TryGetValue(normalized, out var watch)
                ? watch
                : throw new InvalidOperationException($"Drive {normalized} has no scripted watch.");
        }
    }

    void OnWatchDisposed(bool cancelled)
    {
        WasCancelled |= cancelled;
        _completed.TrySetResult();
    }

    sealed class ScriptedDriveWatch(char driveLetter, ScriptedIndexWatchSource owner) : IIndexDriveWatch
    {
        readonly Channel<WatchStreamItem> _items = Channel.CreateUnbounded<WatchStreamItem>();
        int _disposed;
        int _finished;

        public char DriveLetter { get; } = driveLetter;

        public bool IsFinished => Volatile.Read(ref _finished) != 0 || Volatile.Read(ref _disposed) != 0;

        public void Publish(WatchStreamItem item)
        {
            Assert.That(_items.Writer.TryWrite(item), Is.True);
        }

        public void Fail(Exception exception) => _items.Writer.TryComplete(exception);

        public void Complete() => _items.Writer.TryComplete();

        public async IAsyncEnumerable<WatchStreamItem> ReadAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            try
            {
                await foreach (var item in _items.Reader.ReadAllAsync(cancellationToken))
                    yield return item;
            }
            finally
            {
                Volatile.Write(ref _finished, 1);
                owner.OnWatchDisposed(cancellationToken.IsCancellationRequested);
            }
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                _items.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}

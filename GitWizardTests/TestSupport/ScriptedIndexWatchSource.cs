using System.Runtime.CompilerServices;
using System.Threading.Channels;
using MFTLib.Index;

namespace GitWizardTests.TestSupport;

internal sealed class ScriptedIndexWatchSource : IIndexWatchSource
{
    readonly Channel<PendingItem> _items = Channel.CreateUnbounded<PendingItem>();
    readonly Lock _gate = new();
    readonly Dictionary<char, int> _generations = [];
    int _nextGeneration;
    readonly TaskCompletionSource<IReadOnlyList<IndexWatchTarget>> _started =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    bool _running;

    public Task<IReadOnlyList<IndexWatchTarget>> Started => _started.Task;
    public Task Completed => _completed.Task;
    public bool WasCancelled { get; private set; }

    public void Publish(WatchStreamItem item)
    {
        lock (_gate)
        {
            var letter = GetDriveLetter(item);
            Assert.That(_items.Writer.TryWrite(new PendingItem(item, letter,
                _generations.GetValueOrDefault(letter))), Is.True);
        }
    }
    public void Fail(Exception exception) => _items.Writer.TryComplete(exception);
    public void Complete() => _items.Writer.TryComplete();

    public IAsyncEnumerable<WatchStreamItem> StartWatching(IReadOnlyList<IndexWatchTarget> targets,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_running)
                throw new InvalidOperationException("A scripted stream is already live.");
            if (targets.Select(target => char.ToUpperInvariant(target.DriveLetter)).Distinct().Count() != targets.Count)
                throw new ArgumentException("Duplicate drive targets.", nameof(targets));
            _generations.Clear();
            foreach (var target in targets)
                _generations.Add(char.ToUpperInvariant(target.DriveLetter), ++_nextGeneration);
            _running = true;
        }
        return ReadAsync(targets, cancellationToken);
    }

    async IAsyncEnumerable<WatchStreamItem> ReadAsync(IReadOnlyList<IndexWatchTarget> targets,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _started.TrySetResult(targets.ToArray());
        try
        {
            await foreach (var pending in _items.Reader.ReadAllAsync(cancellationToken))
            {
                bool lastDriveFailed;
                lock (_gate)
                {
                    // A re-arm retires queued items because they belong to the previous cursor.
                    if (!_generations.TryGetValue(pending.DriveLetter, out var generation) ||
                        generation != pending.Generation)
                        continue;
                    if (pending.Item is DriveWatchFailure)
                        _generations.Remove(pending.DriveLetter);
                    lastDriveFailed = _generations.Count == 0;
                }
                yield return pending.Item;
                if (lastDriveFailed)
                    yield break;
            }
        }
        finally
        {
            WasCancelled = cancellationToken.IsCancellationRequested;
            lock (_gate)
                _running = false;
            _completed.TrySetResult();
        }
    }

    public Task ArmDriveAsync(IndexWatchTarget target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            EnsureRunning();
            _generations[char.ToUpperInvariant(target.DriveLetter)] = ++_nextGeneration;
        }
        return Task.CompletedTask;
    }

    public Task DisarmDriveAsync(char driveLetter, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            EnsureRunning();
            _generations.Remove(char.ToUpperInvariant(driveLetter));
        }
        return Task.CompletedTask;
    }

    static char GetDriveLetter(WatchStreamItem item) => item switch
    {
        JournalBatch batch => char.ToUpperInvariant(batch.DriveLetter),
        DriveWatchFailure failure => char.ToUpperInvariant(failure.DriveLetter),
        _ => throw new ArgumentOutOfRangeException(nameof(item), item, "Unknown watch item.")
    };

    sealed record PendingItem(WatchStreamItem Item, char DriveLetter, int Generation);

    void EnsureRunning()
    {
        if (!_running)
            throw new InvalidOperationException("No scripted stream is live.");
    }
}

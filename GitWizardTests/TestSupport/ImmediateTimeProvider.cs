namespace GitWizardTests.TestSupport;

/// <summary>
/// A clock whose timers fire at once on the thread pool, whatever their due time, so a delay
/// between polls costs no real time; <see cref="TimersCreated" /> counts the delays requested.
/// </summary>
internal sealed class ImmediateTimeProvider : TimeProvider
{
    int _timersCreated;

    public int TimersCreated => Volatile.Read(ref _timersCreated);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        Interlocked.Increment(ref _timersCreated);
        var timer = new ImmediateTimer();
        if (dueTime != Timeout.InfiniteTimeSpan)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                if (!timer.IsDisposed)
                    callback(state);
            });
        }
        return timer;
    }

    sealed class ImmediateTimer : ITimer
    {
        int _disposed;

        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public bool Change(TimeSpan dueTime, TimeSpan period) => !IsDisposed;

        public void Dispose() => Volatile.Write(ref _disposed, 1);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

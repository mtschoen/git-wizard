namespace GitWizardTests.TestSupport;

internal sealed class ControlledDelayClock : TimeProvider
{
    public ScheduledTimer? Timer { get; private set; }

    public int CreatedTimerCount { get; private set; }

    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;

    public override ITimer CreateTimer(TimerCallback callback, object? state,
        TimeSpan dueTime, TimeSpan period)
    {
        CreatedTimerCount++;
        return Timer = new ScheduledTimer(callback, state, dueTime, period);
    }

    internal sealed class ScheduledTimer(TimerCallback callback, object? state,
        TimeSpan dueTime, TimeSpan period) : ITimer
    {
        public TimeSpan DueTime { get; private set; } = dueTime;
        public TimeSpan Period { get; private set; } = period;
        public bool IsDisposed { get; private set; }

        public void Fire()
        {
            if (!IsDisposed)
            {
                callback(state);
            }
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (IsDisposed)
            {
                return false;
            }

            DueTime = dueTime;
            Period = period;
            return true;
        }

        public void Dispose() => IsDisposed = true;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

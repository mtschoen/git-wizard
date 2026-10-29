namespace GitWizardTests.TestSupport;

sealed class RefreshOverlapProbe : IDisposable
{
    readonly ManualResetEventSlim _secondEntered = new(false);
    readonly object _sync = new();
    int _entries;
    int _active;

    public int MaximumActive { get; private set; }

    public void OnPhase(int phaseIndex)
    {
        if (phaseIndex != 0)
        {
            return;
        }

        var active = Interlocked.Increment(ref _active);
        lock (_sync)
        {
            MaximumActive = Math.Max(MaximumActive, active);
        }

        var entry = Interlocked.Increment(ref _entries);
        try
        {
            if (entry == 1)
            {
                _ = _secondEntered.Wait(TestTimeouts.ShortHangGuard);
            }
            else
            {
                _secondEntered.Set();
            }
        }
        finally
        {
            _ = Interlocked.Decrement(ref _active);
        }

        throw new OperationCanceledException();
    }

    public void Dispose() => _secondEntered.Dispose();
}

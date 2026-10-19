namespace GitWizard.Watch;

/// <summary>
/// Remembers the last scan error reported per drive so a caller surfaces only new or changed
/// errors. Thread-safe: scan-error notifications arrive on source threads.
/// </summary>
public sealed class ScanErrorChangeFilter
{
    readonly Lock _gate = new();
    readonly Dictionary<string, string> _reported = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the error is new or differs from the drive's last reported one.</summary>
    public bool TryReport(string drive, string message)
    {
        lock (_gate)
        {
            if (_reported.TryGetValue(drive, out var previous) && previous == message)
                return false;
            _reported[drive] = message;
            return true;
        }
    }

    public void Clear()
    {
        lock (_gate)
            _reported.Clear();
    }
}

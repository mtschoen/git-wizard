using System.Diagnostics;

namespace GitWizardUI.Services;

public readonly record struct DispatcherWatchdogObservation(
    string CallbackName,
    TimeSpan Elapsed,
    TimeSpan Threshold);

public sealed class DispatcherWatchdog
{
    static readonly TimeSpan DefaultWarningThreshold = TimeSpan.FromMilliseconds(500);

    public static DispatcherWatchdog Default { get; } = new(
        TimeProvider.System,
        DefaultWarningThreshold,
        WriteDefaultWarning);

    readonly TimeProvider _timeProvider;
    readonly TimeSpan _warningThreshold;
    readonly Action<DispatcherWatchdogObservation> _onSlowCallback;

    public DispatcherWatchdog(
        TimeProvider timeProvider,
        TimeSpan warningThreshold,
        Action<DispatcherWatchdogObservation> onSlowCallback)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(onSlowCallback);
        _timeProvider = timeProvider;
        _warningThreshold = warningThreshold;
        _onSlowCallback = onSlowCallback;
    }

    public Action Inspect(string callbackName, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callbackName);
        ArgumentNullException.ThrowIfNull(callback);
        return () =>
        {
            var startTimestamp = _timeProvider.GetTimestamp();
            try
            {
                callback();
            }
            finally
            {
                ReportIfSlow(callbackName, startTimestamp);
            }
        };
    }

    public Func<T> Inspect<T>(string callbackName, Func<T> callback)
    {
        ArgumentNullException.ThrowIfNull(callbackName);
        ArgumentNullException.ThrowIfNull(callback);
        return () =>
        {
            var startTimestamp = _timeProvider.GetTimestamp();
            try
            {
                return callback();
            }
            finally
            {
                ReportIfSlow(callbackName, startTimestamp);
            }
        };
    }

    public static Action Wrap(string callbackName, Action callback) =>
        Default.Inspect(callbackName, callback);

    public static Func<T> Wrap<T>(string callbackName, Func<T> callback) =>
        Default.Inspect(callbackName, callback);

    void ReportIfSlow(string callbackName, long startTimestamp)
    {
        var elapsed = _timeProvider.GetElapsedTime(startTimestamp);
        if (elapsed > _warningThreshold)
        {
            _onSlowCallback(new DispatcherWatchdogObservation(callbackName, elapsed, _warningThreshold));
        }
    }

    static void WriteDefaultWarning(DispatcherWatchdogObservation observation) =>
        // aislop-ignore-next-line ai-slop/csharp-console-leftover -- intentional always-on diagnostic for attended Release Trace listeners
        Trace.WriteLine($"[DispatcherWatchdog] Callback '{observation.CallbackName}' took {observation.Elapsed.TotalMilliseconds:N0} ms (budget {observation.Threshold.TotalMilliseconds:N0} ms).");
}

namespace GitWizard.Watch.Linux;

/// <summary>
/// Memory budgets of one root helper session. The helper runs as root for an unprivileged peer, so
/// it bounds what that peer can make it retain in both directions.
/// </summary>
/// <param name="MaximumRequestBytes">
/// Largest request line accepted from stdin, in UTF-8 bytes excluding the newline. A longer line is
/// rejected without being buffered. The default is 16 MiB (16,777,216 bytes) of UTF-8 encoded JSON per
/// request line, excluding the newline: roughly 4,000 maximum-length (4,095-byte) unescaped paths, fewer
/// when paths need JSON escaping, far more at typical path lengths. This is an estimate, not a
/// supported root count.
/// </param>
/// <param name="MaximumQueuedOutputBytes">
/// Largest total of serialized frames queued for stdout and not yet written. When the next frame
/// would exceed it the desktop is no longer reading, and the session stops with the output-failed
/// exit code instead of dropping the frame or waiting without end.
/// </param>
readonly record struct LinuxWatchHelperLimits(int MaximumRequestBytes, long MaximumQueuedOutputBytes)
{
    public const int DefaultMaximumRequestBytes = 16 * 1024 * 1024;
    public const long DefaultMaximumQueuedOutputBytes = 64L * 1024 * 1024;

    public static LinuxWatchHelperLimits Default { get; } =
        new(DefaultMaximumRequestBytes, DefaultMaximumQueuedOutputBytes);

    /// <summary>Renders a byte count as whole MiB when it is one, otherwise as bytes.</summary>
    public static string Describe(long bytes) =>
        bytes > 0 && bytes % (1024 * 1024) == 0 ? $"{bytes / (1024 * 1024)} MiB" : $"{bytes} bytes";
}

/// <summary>Test seams of one helper run: how the shutdown deadline starts, and the session's memory limits.</summary>
readonly record struct LinuxWatchHelperOptions(Func<Task>? StartShutdownDeadline, LinuxWatchHelperLimits Limits);

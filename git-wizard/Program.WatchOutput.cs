using System.Globalization;
using System.Text.Json;
using GitWizard.Watch;

// ReSharper disable once CheckNamespace
namespace GitWizard.CLI;

public static partial class Program
{
    // One writer per --watch run: serializes every line (events arrive on the enumeration
    // thread, readiness and errors on source threads) and applies the -v timestamp prefix.
    internal sealed class WatchLineWriter(Action<string> sink, bool verbose, TimeProvider clock)
    {
        readonly Lock _gate = new();
        readonly Dictionary<string, VolumeWatchStatus> _printedVolumes = new(StringComparer.OrdinalIgnoreCase);

        public void Write(string line)
        {
            var text = verbose ? $"{FormatTimestamp(clock.GetUtcNow())} {line}" : line;
            lock (_gate)
            {
                sink(text);
            }
        }

        void WriteVerbose(string line)
        {
            if (verbose)
            {
                Write(line);
            }
        }

        public void WriteStartup(WatchStartupProgress progress)
        {
            if (progress.Readiness is { } snapshot)
            {
                WriteVolumes(snapshot);
            }
            else
            {
                WriteVerbose(FormatStartupLine(progress));
            }
        }

        // Prints only transitions, so a snapshot repeating an already-printed state is silent.
        public void WriteVolumes(VolumeReadinessSnapshot snapshot)
        {
            foreach (var status in snapshot.Volumes)
            {
                bool changed;
                lock (_gate)
                {
                    changed = !_printedVolumes.TryGetValue(status.Volume, out var previous) || previous != status;
                    _printedVolumes[status.Volume] = status;
                }
                if (changed)
                {
                    WriteVerbose(FormatVolumeLine(status));
                }
            }
        }
    }

    internal static string FormatTimestamp(DateTimeOffset utc) =>
        utc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    internal static string FormatReadyLine(WatchReadiness readiness)
    {
        var caughtUp = readiness.CaughtUpVolumes.Count == 0 ? "-" : string.Join(",", readiness.CaughtUpVolumes);
        var excluded = readiness.ExcludedVolumes.Count == 0
            ? "-"
            : string.Join(",", readiness.ExcludedVolumes.Select(status =>
                $"{status.Volume}={JsonSerializer.Serialize(status.ExclusionReason ?? string.Empty)}"));
        return $"ready: mode={readiness.Mode.Token()} caught-up={caughtUp} excluded={excluded}";
    }

    internal static string FormatVolumeLine(VolumeWatchStatus status)
    {
        var line = $"drive: {status.Volume} state={status.State.Token()} origin={status.Origin.Token()}";
        return status.ExclusionReason is null ? line : $"{line} reason={JsonSerializer.Serialize(status.ExclusionReason)}";
    }

    internal static string FormatStartupLine(WatchStartupProgress progress)
    {
        var phase = progress.Phase.Token();
        return progress.Volume is null ? $"startup: {phase}" : $"startup: {phase} {progress.Volume}";
    }

}

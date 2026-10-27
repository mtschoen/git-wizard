using System.Globalization;
using MFTLib.Index;

namespace GitWizard;

public static class MftIndexCacheStatusFormatter
{
    public static string Format(MftIndexCacheSnapshot snapshot)
    {
        if (!snapshot.IsApplicable)
        {
            return "MFT index cache is not applicable on this platform (Windows only).";
        }

        var lines = new List<string>
        {
            "Index cache status (point-in-time observation, not a reservation)",
            $"Observed UTC: {snapshot.ObservedAt:O}",
            $"Cache directory: {snapshot.CacheDirectory}",
            $"Retention: {DescribeRetention(snapshot.DirectoryScanFileNames)}",
            "Namespace: current user's GitWizard private cache.",
            "Inspection may create persistent .lock siblings; holder identity is unavailable.",
            "Directory-entry last-write is not a scan timestamp or last successful catch-up."
        };
        foreach (var row in snapshot.Inventory)
        {
            AddInventory(lines, snapshot, row);
        }

        if (snapshot.Inventory.Count == 0 && snapshot.Errors.Count == 0)
        {
            lines.Add("No cached blocks in the current policy directory.");
        }

        foreach (var session in snapshot.Sessions)
        {
            foreach (var row in session.Drives)
            {
                AddSession(lines, row);
            }
        }

        if (snapshot.Sessions.Count == 0)
        {
            lines.Add("No active session status in this process.");
        }

        lines.Add("Normal concurrent open can cold-scan into a private block; cache-only concurrent open refuses with InUse.");
        lines.Add("Backing describes the current block; a private fallback does not prove the slot is still held now.");
        lines.AddRange(snapshot.Errors.Select(error => $"Error: {error}"));
        return string.Join(Environment.NewLine, lines);
    }

    static string DescribeRetention(IReadOnlyList<string>? names)
    {
        if (names is null)
        {
            return "all eligible records";
        }

        return names.Count == 0 ? "directories only" : $"directories plus {string.Join(", ", names)}";
    }

    static void AddInventory(List<string> lines, MftIndexCacheSnapshot snapshot, CachedBlockStatus row)
    {
        var owned = snapshot.Sessions.SelectMany(session => session.Drives).Any(active =>
            active.VolumeSerial == row.File.VolumeSerial &&
            active.Drive.IndexStatus?.Root == row.File.Root &&
            active.Drive.IndexStatus.Block?.CacheLocation == CacheLocation.OwnedCanonical);
        var availability = row.Availability.ToString();
        if (row.Availability == CachedBlockAvailability.InUse)
        {
            availability = owned ? "InUse; this session owns the cache" : "in use; holder identity unavailable";
        }

        lines.Add($"Drive {row.File.Root}: serial {row.File.VolumeSerial:X8}; {availability}");
        lines.Add($"  Size: {row.File.Size.ToString(CultureInfo.InvariantCulture)} bytes");
        lines.Add($"  Directory-entry last-write UTC: {row.File.LastWriteTime:O}; age: {Age(row.File.LastWriteTime, snapshot.ObservedAt)}");
        lines.Add($"  Validation: {row.Validation?.ToString() ?? "unavailable"}; producer: {row.ProducerKind?.ToString() ?? "unavailable"}");
        lines.Add($"  Cache tag: stored {row.CacheTag?.ToString() ?? "unavailable"}; requested {snapshot.RequestedCacheTag}");
    }

    static void AddSession(List<string> lines, MftActiveCacheRoot row)
    {
        lines.Add($"Active session drive {row.Drive.Drive}: serial {row.VolumeSerial?.ToString("X8", CultureInfo.InvariantCulture) ?? "unavailable"}");
        if (row.Drive.IndexStatus is not { } status)
        {
            lines.Add($"  Identity failure: {row.Drive.IdentityFailure ?? "unavailable"}");
            return;
        }
        lines.Add($"  State: {status.State}; source: {status.Block?.Origin.ToString() ?? "unavailable"}");
        var ownership = status.Block is { } block ? Backing(block.CacheLocation) : string.Empty;
        if (ownership.Length > 0)
        {
            lines.Add($"  Backing: {ownership}");
        }

        lines.Add($"  Scan timestamp UTC: {status.Block?.ScanTimestamp.ToString("O", CultureInfo.InvariantCulture) ?? "unavailable"}");
        lines.Add($"  Open/rescan failure: {status.Failure?.Kind.ToString() ?? "None"}; {status.Failure?.Message ?? "none reported"}");
        lines.Add($"  Watch failure: {status.Watch.FailureMessage ?? "none reported (not proof of catch-up)"}");
    }

    public static string Backing(CacheLocation? state) => state switch
    {
        CacheLocation.OwnedCanonical => "this session owns the cache",
        CacheLocation.PrivateFallback => "running on a private block because the canonical slot was unavailable when selected",
        _ => string.Empty
    };

    public static string Age(DateTime timestamp, DateTimeOffset observed)
    {
        if (timestamp == default)
        {
            return "unavailable";
        }

        var age = observed - new DateTimeOffset(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc));
        if (age < TimeSpan.Zero)
        {
            return "in the future (clock difference)";
        }

        if (age.TotalDays >= 1)
        {
            return $"{Math.Floor(age.TotalDays).ToString(CultureInfo.InvariantCulture)}d";
        }

        if (age.TotalHours >= 1)
        {
            return $"{Math.Floor(age.TotalHours).ToString(CultureInfo.InvariantCulture)}h";
        }

        return age.TotalMinutes >= 1
            ? $"{Math.Floor(age.TotalMinutes).ToString(CultureInfo.InvariantCulture)}m"
            : $"{Math.Floor(age.TotalSeconds).ToString(CultureInfo.InvariantCulture)}s";
    }
}

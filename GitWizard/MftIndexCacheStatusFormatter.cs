using System.Globalization;
using MFTLib.Index;

namespace GitWizard;

public static class MftIndexCacheStatusFormatter
{
    public static string Format(MftIndexCacheSnapshot snapshot)
    {
        if (!snapshot.IsApplicable)
            return "MFT index cache is not applicable on this platform (Windows only).";
        var lines = new List<string>
        {
            "Index cache status (point-in-time observation, not a reservation)",
            $"Observed UTC: {snapshot.ObservedAt:O}",
            $"Cache directory: {snapshot.CacheDirectory}",
            $"Profile: {snapshot.Profile}; retained names: {string.Join(", ", snapshot.KeepFileNames)}",
            "Namespace: current user's GitWizard private cache.",
            "Inspection may create persistent .lock siblings; holder identity is unavailable.",
            "Directory-entry last-write is not a scan timestamp or last successful catch-up."
        };
        foreach (var row in snapshot.Inventory)
            AddInventory(lines, snapshot, row);
        if (snapshot.Inventory.Count == 0 && snapshot.Errors.Count == 0)
            lines.Add("No cached blocks in the current policy directory.");
        foreach (var session in snapshot.Sessions)
            foreach (var row in session.Drives)
                AddSession(lines, row);
        if (snapshot.Sessions.Count == 0)
            lines.Add("No active session status in this process.");
        lines.Add("Normal concurrent open can cold-scan into a private block; cache-only concurrent open refuses with InUse.");
        lines.Add("Backing describes the current block; a private fallback does not prove the slot is still held now.");
        lines.AddRange(snapshot.Errors.Select(error => $"Error: {error}"));
        return string.Join(Environment.NewLine, lines);
    }

    static void AddInventory(List<string> lines, MftIndexCacheSnapshot snapshot, CachedBlockStatus row)
    {
        var owned = snapshot.Sessions.SelectMany(session => session.Drives).Any(active =>
            active.VolumeSerial == row.File.VolumeSerial &&
            active.Drive.IndexStatus?.DriveLetter == row.File.DriveLetter &&
            active.Drive.IndexStatus.CacheSlot == CacheSlotState.OwnedCanonical);
        var availability = row.Availability.ToString();
        if (row.Availability == CachedBlockAvailability.InUse)
            availability = owned ? "InUse; this session owns the cache" : "in use; holder identity unavailable";
        lines.Add($"Drive {row.File.DriveLetter}: serial {row.File.VolumeSerial:X8}; {availability}");
        lines.Add($"  Size: {row.File.SizeBytes.ToString(CultureInfo.InvariantCulture)} bytes");
        lines.Add($"  Directory-entry last-write UTC: {row.File.LastWriteTimeUtc:O}; age: {Age(row.File.LastWriteTimeUtc, snapshot.ObservedAt)}");
        lines.Add($"  Validation: {row.Validation?.ToString() ?? "unavailable"}; producer: {row.ProducerKind?.ToString() ?? "unavailable"}");
    }

    static void AddSession(List<string> lines, MftActiveCacheDrive row)
    {
        lines.Add($"Active session drive {row.Drive.Drive}: serial {row.VolumeSerial?.ToString("X8", CultureInfo.InvariantCulture) ?? "unavailable"}");
        if (row.Drive.IndexStatus is not { } status)
        {
            lines.Add($"  Identity failure: {row.Drive.IdentityFailure ?? "unavailable"}");
            return;
        }
        lines.Add($"  State: {status.State}; source: {status.BlockSource}");
        var ownership = Backing(status.CacheSlot);
        if (ownership.Length > 0)
            lines.Add($"  Backing: {ownership}");
        lines.Add($"  Scan timestamp UTC: {(status.ScanTimestamp == default ? "unavailable" : status.ScanTimestamp.ToString("O", CultureInfo.InvariantCulture))}");
        lines.Add($"  Open/rescan failure: {status.FailureKind}; {status.MftProducerFailureMessage ?? "none reported"}");
        lines.Add($"  Watch failure: {status.WatchFailureMessage ?? "none reported (not proof of catch-up)"}");
    }

    public static string Backing(CacheSlotState state) => state switch
    {
        CacheSlotState.OwnedCanonical => "this session owns the cache",
        CacheSlotState.PrivateFallback => "running on a private block; another process holds the cache",
        _ => string.Empty
    };

    public static string Age(DateTime timestamp, DateTimeOffset observed)
    {
        if (timestamp == default)
            return "unavailable";
        var age = observed - new DateTimeOffset(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc));
        if (age < TimeSpan.Zero)
            return "in the future (clock difference)";
        if (age.TotalDays >= 1)
            return $"{Math.Floor(age.TotalDays).ToString(CultureInfo.InvariantCulture)}d";
        if (age.TotalHours >= 1)
            return $"{Math.Floor(age.TotalHours).ToString(CultureInfo.InvariantCulture)}h";
        if (age.TotalMinutes >= 1)
            return $"{Math.Floor(age.TotalMinutes).ToString(CultureInfo.InvariantCulture)}m";
        return $"{Math.Floor(age.TotalSeconds).ToString(CultureInfo.InvariantCulture)}s";
    }
}

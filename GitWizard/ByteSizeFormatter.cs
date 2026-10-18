using System.Globalization;

namespace GitWizard;

/// <summary>Formats byte counts for compact diagnostics and repository size displays.</summary>
public static class ByteSizeFormatter
{
    /// <summary>Uses invariant culture, up to two decimal places, and units from B through TB.</summary>
    public static string Format(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        var unitIndex = 0;
        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }
        return string.Create(CultureInfo.InvariantCulture, $"{size:0.##} {units[unitIndex]}");
    }

    /// <summary>Uses current culture, one decimal place, and KB, MB or GB; missing sizes are empty.</summary>
    public static string FormatForDisplay(long? bytes)
    {
        if (!bytes.HasValue)
            return string.Empty;

        const double bytesPerKilobyte = 1024;
        const double bytesPerMegabyte = bytesPerKilobyte * 1024;
        const double bytesPerGigabyte = bytesPerMegabyte * 1024;
        if (bytes.Value >= bytesPerGigabyte)
            return $"{bytes.Value / bytesPerGigabyte:F1} GB";
        if (bytes.Value >= bytesPerMegabyte)
            return $"{bytes.Value / bytesPerMegabyte:F1} MB";
        return $"{bytes.Value / bytesPerKilobyte:F1} KB";
    }
}

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace GitWizard.Watch.Linux;

static partial class LinuxWatchHelperProtocol
{
    /// <summary>
    /// Parses one protocol line. A line that is not a JSON object with a known <c>type</c> and
    /// well-typed members yields false and a one-line reason instead of an exception.
    /// </summary>
    public static bool TryParse(string line, [NotNullWhen(true)] out LinuxWatchHelperFrame? frame,
        [NotNullWhen(false)] out string? error)
    {
        frame = null;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty(TypeMember, out var type) || type.ValueKind != JsonValueKind.String)
            {
                error = "The line is not a JSON object with a string type member.";
                return false;
            }

            frame = ParseBody(type.GetString(), root);
            error = frame is null ? $"Unknown or incomplete frame of type '{type.GetString()}'." : null;
            return frame is not null;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            // JsonElement decodes escaped strings on access, after the document has been parsed.
            error = "The line is not valid JSON.";
            return false;
        }
    }

    static LinuxWatchHelperFrame? ParseBody(string? type, JsonElement root) => type switch
    {
        "arm" => TryReadStrings(root, "roots") is { } roots ? new ArmRequestFrame(roots) : null,
        "watch" => new WatchRequestFrame(),
        "hello" => root.TryGetProperty("protocol", out var protocol) && protocol.ValueKind == JsonValueKind.Number &&
                   protocol.TryGetInt32(out var version)
            ? new HelloFrame(version)
            : null,
        "armResult" => TryReadErrors(root) is { } errors ? new ArmResultFrame(errors) : null,
        "readiness" => TryReadVolumes(root) is { } volumes ? new ReadinessFrame(volumes) : null,
        "pumpStarted" => new PumpStartedFrame(),
        "batch" => TryReadBatch(root) is { } batch ? new BatchFrame(batch) : null,
        "driveFailed" => TryReadString(root, "volume") is { } volume && TryReadString(root, "message") is { } message
            ? new DriveFailedFrame(volume, message)
            : null,
        "error" => TryReadString(root, "message") is { } errorMessage ? new ErrorFrame(errorMessage) : null,
        "died" => TryReadString(root, "reason") is { } reason ? new DiedFrame(reason) : null,
        _ => null
    };

    static string? TryReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    static string[]? TryReadStrings(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var values = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } value)
            {
                return null;
            }

            values.Add(value);
        }

        return [.. values];
    }

    static Dictionary<string, string>? TryReadErrors(JsonElement root)
    {
        if (!root.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var result = new Dictionary<string, string>(WatchPath.StringComparer);
        foreach (var property in errors.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String || property.Value.GetString() is not { } message)
            {
                return null;
            }

            result[property.Name] = message;
        }

        return result;
    }

    static VolumeWatchStatus[]? TryReadVolumes(JsonElement root)
    {
        if (!root.TryGetProperty("volumes", out var volumes) || volumes.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var result = new List<VolumeWatchStatus>();
        foreach (var item in volumes.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || TryReadString(item, "volume") is not { } volume ||
                !TryReadEnum<VolumeWatchState>(item, "state", out var state) ||
                !TryReadEnum<VolumeBlockOrigin>(item, "origin", out var origin))
            {
                return null;
            }

            long? version = null;
            if (item.TryGetProperty("watchStateVersion", out var versionElement))
            {
                if (versionElement.ValueKind != JsonValueKind.Number || !versionElement.TryGetInt64(out var parsed))
                {
                    return null;
                }

                version = parsed;
            }

            result.Add(new VolumeWatchStatus(volume, state, origin, TryReadString(item, "exclusionReason"))
            {
                WatchStateVersion = version
            });
        }

        return [.. result];
    }

    static VolumeChangeBatch? TryReadBatch(JsonElement root)
    {
        if (TryReadString(root, "volume") is not { } volume ||
            !root.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var result = new List<VolumeChangeEntry>();
        foreach (var item in entries.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || TryReadString(item, "path") is not { } path ||
                !TryReadEnum<VolumeEntryKind>(item, "kind", out var kind))
            {
                return null;
            }

            result.Add(new VolumeChangeEntry(path, kind));
        }

        return new VolumeChangeBatch(volume, [.. result]);
    }

    // Names only: Enum.TryParse also accepts numerals, which would admit undefined values.
    static bool TryReadEnum<TEnum>(JsonElement element, string name, out TEnum value) where TEnum : struct, Enum
    {
        value = default;
        return TryReadString(element, name) is { } text &&
               Enum.GetNames<TEnum>().Contains(text, StringComparer.Ordinal) &&
               Enum.TryParse(text, false, out value);
    }
}

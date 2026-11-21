using System.Buffers;
using System.Text;
using System.Text.Json;

namespace GitWizard.Watch.Linux;

/// <summary>One line of the elevated Linux watch helper's protocol, in either direction.</summary>
abstract record LinuxWatchHelperFrame;

/// <summary>Client to helper: mark these absolute roots and report the arm result.</summary>
sealed record ArmRequestFrame(IReadOnlyList<string> Roots) : LinuxWatchHelperFrame;

/// <summary>Client to helper: start the read pump and stream change batches.</summary>
sealed record WatchRequestFrame : LinuxWatchHelperFrame;

/// <summary>Helper to client: the helper runs as root and accepts requests.</summary>
sealed record HelloFrame(int ProtocolVersion) : LinuxWatchHelperFrame;

/// <summary>Helper to client: the per-root failures of the arm request; an absent root was marked.</summary>
sealed record ArmResultFrame(IReadOnlyDictionary<string, string> Errors) : LinuxWatchHelperFrame;

/// <summary>Helper to client: the source's full per-volume readiness after a change.</summary>
sealed record ReadinessFrame(IReadOnlyList<VolumeWatchStatus> Volumes) : LinuxWatchHelperFrame;

/// <summary>Helper to client: the source's read pump is running.</summary>
sealed record PumpStartedFrame : LinuxWatchHelperFrame;

/// <summary>Helper to client: one change batch, in source order.</summary>
sealed record BatchFrame(VolumeChangeBatch Batch) : LinuxWatchHelperFrame;

/// <summary>Helper to client: one volume failed while the others keep running.</summary>
sealed record DriveFailedFrame(string Volume, string Message) : LinuxWatchHelperFrame;

/// <summary>Helper to client: a request was rejected; the helper keeps running.</summary>
sealed record ErrorFrame(string Message) : LinuxWatchHelperFrame;

/// <summary>Helper to client: the watch ended; the helper exits after writing this line.</summary>
sealed record DiedFrame(string Reason) : LinuxWatchHelperFrame;

/// <summary>
/// JSON-lines wire format between the unprivileged desktop process and the root helper started
/// with <see cref="LinuxWatchHelperHost.Flag"/>. Every frame is one JSON object on one line with a
/// <c>type</c> member; <see cref="Serialize"/> escapes control characters, so a path never splits a
/// line.
/// </summary>
static partial class LinuxWatchHelperProtocol
{
    public const int Version = 1;

    const string TypeMember = "type";

    public static string Serialize(LinuxWatchHelperFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            WriteBody(writer, frame);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    static void WriteBody(Utf8JsonWriter writer, LinuxWatchHelperFrame frame)
    {
        switch (frame)
        {
            case ArmRequestFrame arm:
                writer.WriteString(TypeMember, "arm");
                writer.WriteStartArray("roots");
                foreach (var root in arm.Roots)
                {
                    writer.WriteStringValue(root);
                }

                writer.WriteEndArray();
                break;
            case WatchRequestFrame:
                writer.WriteString(TypeMember, "watch");
                break;
            case HelloFrame hello:
                writer.WriteString(TypeMember, "hello");
                writer.WriteNumber("protocol", hello.ProtocolVersion);
                break;
            case ArmResultFrame armResult:
                writer.WriteString(TypeMember, "armResult");
                writer.WriteStartObject("errors");
                foreach (var (root, message) in armResult.Errors)
                {
                    writer.WriteString(root, message);
                }

                writer.WriteEndObject();
                break;
            case ReadinessFrame readiness:
                writer.WriteString(TypeMember, "readiness");
                WriteVolumes(writer, readiness.Volumes);
                break;
            case PumpStartedFrame:
                writer.WriteString(TypeMember, "pumpStarted");
                break;
            case BatchFrame batch:
                writer.WriteString(TypeMember, "batch");
                WriteBatch(writer, batch.Batch);
                break;
            case DriveFailedFrame driveFailed:
                writer.WriteString(TypeMember, "driveFailed");
                writer.WriteString("volume", driveFailed.Volume);
                writer.WriteString("message", driveFailed.Message);
                break;
            case ErrorFrame error:
                writer.WriteString(TypeMember, "error");
                writer.WriteString("message", error.Message);
                break;
            case DiedFrame died:
                writer.WriteString(TypeMember, "died");
                writer.WriteString("reason", died.Reason);
                break;
            default:
                throw new ArgumentException($"Unknown helper frame {frame.GetType().Name}.", nameof(frame));
        }
    }

    static void WriteVolumes(Utf8JsonWriter writer, IReadOnlyList<VolumeWatchStatus> volumes)
    {
        writer.WriteStartArray("volumes");
        foreach (var volume in volumes)
        {
            writer.WriteStartObject();
            writer.WriteString("volume", volume.Volume);
            writer.WriteString("state", volume.State.ToString());
            writer.WriteString("origin", volume.Origin.ToString());
            if (volume.ExclusionReason is { } reason)
            {
                writer.WriteString("exclusionReason", reason);
            }

            if (volume.WatchStateVersion is { } version)
            {
                writer.WriteNumber("watchStateVersion", version);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    static void WriteBatch(Utf8JsonWriter writer, VolumeChangeBatch batch)
    {
        writer.WriteString("volume", batch.Volume);
        writer.WriteStartArray("entries");
        foreach (var entry in batch.Entries)
        {
            writer.WriteStartObject();
            writer.WriteString("path", entry.FullPath);
            writer.WriteString("kind", entry.Kind.ToString());
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }
}

using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using MFTLib.Index;

namespace GitWizard;

/// <summary>Owns the discovery index session until ownership is transferred to a consumer.</summary>
public sealed class MftDiscoveryResult : IAsyncDisposable
{
    static readonly IReadOnlyDictionary<string, string> EmptyScanErrors =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());

    MftIndexSession? _indexSession;
    public bool Success { get; }
    [JsonIgnore]
    public FileIndex? Index => _indexSession?.Index;
    public IReadOnlyDictionary<string, string> ScanErrors { get; }
    /// <summary>Per-drive outcomes captured at construction; unaffected by later owner transfer or disposal.</summary>
    [JsonIgnore]
    public IReadOnlyList<MftDriveStatus> DriveStatuses { get; }

    public MftDiscoveryResult(bool success, MftIndexSession? indexSession = null,
        IReadOnlyDictionary<string, string>? scanErrors = null,
        IReadOnlyList<MftDriveStatus>? driveStatuses = null)
    {
        Success = success;
        _indexSession = indexSession;
        ScanErrors = scanErrors ?? indexSession?.ScanErrors ?? EmptyScanErrors;
        DriveStatuses = Array.AsReadOnly((driveStatuses ?? indexSession?.CaptureDriveStatuses() ?? []).ToArray());
    }

    public MftIndexSession? DetachIndexSession() => Interlocked.Exchange(ref _indexSession, null);

    public async ValueTask DisposeAsync()
    {
        var owner = DetachIndexSession();
        if (owner is not null)
            await owner.DisposeAsync().ConfigureAwait(false);
    }
}

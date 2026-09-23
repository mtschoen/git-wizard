using MFTLib.Index;

namespace GitWizard.Watch;

public sealed partial class IndexVolumeChangeSource
{
    readonly Lock _startupBufferLock = new();
    List<VolumeChangeBatch>? _startupBuffer;
    bool _startupBuffering;
    readonly Dictionary<char, string> _startupErrors = [];

    internal MftIndexSession? Session => _owner;
    internal FileIndex? Index => _owner?.Index;

    public static IndexVolumeChangeSource CreatePrepared(MftIndexSession owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return new IndexVolumeChangeSource(
            (_, _, _) => throw new InvalidOperationException("Strict prepared source cannot open a new session."),
            owner);
    }

    public void StartStartupBuffering()
    {
        lock (_startupBufferLock)
        {
            _startupBuffer = [];
            _startupBuffering = true;
        }
    }

    public IReadOnlyList<VolumeChangeBatch> CompleteStartupBuffer()
    {
        lock (_startupBufferLock)
        {
            _startupBuffering = false;
            var batches = _startupBuffer ?? [];
            _startupBuffer = null;
            return batches;
        }
    }

    async Task WaitDriveAsync(MftIndexSession owner, char drive, CancellationToken token)
    {
        try
        {
            await owner.Index.WaitForCatchUpAsync(drive, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            RecordStartupDriveFailure(drive, exception.Message);
        }
    }

    void RecordStartupDriveFailure(char drive, string message)
    {
        var upper = char.ToUpperInvariant(drive);
        lock (_lifecycleLock)
        {
            _usableDrives.Remove(upper);
        }
        lock (_faultLock)
        {
            _startupErrors[upper] = message;
        }
        DriveFailed?.Invoke(upper.ToString(), message);
    }
}

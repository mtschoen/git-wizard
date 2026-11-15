using MFTLib.Index;

namespace GitWizard.Watch;

// Capacity warnings: a drive whose block could not store a journal change carries one until a
// rescan publishes a block that reads IsCapacityExhausted false.
public sealed partial class IndexVolumeChangeSource
{
    readonly Dictionary<char, CapacityWarning> _capacityWarnings = [];
    readonly Lock _capacityWarningsLock = new();

    public event Action? CapacityWarningsChanged;

    public IReadOnlyList<CapacityWarning> CurrentCapacityWarnings
    {
        get
        {
            lock (_capacityWarningsLock)
            {
                return (CapacityWarning[])[.. _capacityWarnings.Values.OrderBy(warning => warning.DriveLetter, StringComparer.Ordinal)];
            }
        }
    }

    // The CapacityExhausted fault: MFTLib notifies once per block lineage, so a drive that already
    // carries a warning keeps the first one.
    void RaiseCapacityWarning(char driveLetter, string detail)
    {
        var letter = char.ToUpperInvariant(driveLetter);
        lock (_capacityWarningsLock)
        {
            if (!_capacityWarnings.TryAdd(letter, new CapacityWarning { DriveLetter = letter.ToString(), Detail = detail }))
            {
                return;
            }
        }
        CapacityWarningsChanged?.Invoke();
    }

    // Built after the arm's catch-up, like the journal warnings, from the drives' blocks as they are
    // now: a watched drive whose block is flagged (a rescan failed or was never attempted) is warned
    // about from the start, and a fault the catch-up already reported keeps its own detail.
    void CollectArmedCapacityWarnings(MftIndexSession owner)
    {
        lock (_capacityWarningsLock)
        {
            var raised = new Dictionary<char, CapacityWarning>(_capacityWarnings);
            _capacityWarnings.Clear();
            foreach (var drive in owner.Indices.Select(index => index.Status))
            {
                if (drive is not { State: RootState.Ready, Watch.IsSupported: true, Block.IsCapacityExhausted: true } ||
                    !owner.TryGetVolume(drive.Root, out var volume))
                {
                    continue;
                }

                _capacityWarnings[volume.Letter] = raised.GetValueOrDefault(volume.Letter) ?? new CapacityWarning
                {
                    DriveLetter = volume.Letter.ToString(),
                    Detail = CapacityWarning.FlaggedBeforeWatchDetail
                };
            }
        }
        CapacityWarningsChanged?.Invoke();
    }

    // Called with a state change the source accepted. A rescan (MFTLib's recovery, for one) moves
    // the watch through new states; once the drive's published block no longer reads flagged the
    // warning is cleared.
    void ClearCapacityWarningIfRescanned(char driveLetter)
    {
        var letter = char.ToUpperInvariant(driveLetter);
        lock (_capacityWarningsLock)
        {
            if (!_capacityWarnings.ContainsKey(letter) ||
                Session?.IndexFor(new VolumeKey(letter))?.Status.Block is not { IsCapacityExhausted: false })
            {
                return;
            }

            _capacityWarnings.Remove(letter);
        }
        CapacityWarningsChanged?.Invoke();
    }
}

using System.Collections.Concurrent;
using MFTLib.Index;

namespace GitWizard;

public sealed partial class MftIndexSession
{
    readonly ConcurrentDictionary<RootKey, VolumeKey> _observedVolumes = new();
    readonly ConcurrentDictionary<RootKey, JournalCheckpointLoss> _rescannedOpeningLosses = new();

    internal void ObserveVolume(RootKey root, VolumeKey volume) => _observedVolumes[root] = volume;

    // A consumer rescan clears the library's report, but the drive opening's report is still why
    // that rescan ran (a cache-only open adopts the unresumable block for the watch to replace),
    // so the session keeps it for the drive's journal warning until the library reports a newer loss.
    internal void RetainOpeningLossBeforeRescan(RootStatus drive)
    {
        if (drive.Watch.CheckpointLoss is { DetectedDuring: JournalCheckpointLossDetection.RootOpening } loss)
        {
            _rescannedOpeningLosses[drive.Root] = loss;
        }
    }

    internal JournalCheckpointLoss? CheckpointLossFor(RootStatus drive) =>
        drive.Watch.CheckpointLoss ?? _rescannedOpeningLosses.GetValueOrDefault(drive.Root);

    internal bool TryGetVolume(RootKey root, out VolumeKey volume)
    {
        if (_observedVolumes.TryGetValue(root, out volume) || root.TryGetVolume(out volume))
        {
            return true;
        }

        if (IndexFor(root)?.Status is { } drive && CheckpointLossFor(drive) is { } loss)
        {
            volume = loss.Volume;
            return true;
        }

        return false;
    }

    internal VolumeKey VolumeFor(RootKey root) => TryGetVolume(root, out var volume)
        ? volume : throw new InvalidOperationException($"No volume observation is available for root {root}.");

    internal IReadOnlyList<string> ObservedMftVolumes => [.. Indices
        .Where(index => index.Status.Producer == ProducerKind.Mft)
        .Select(index => TryGetVolume(index.Status.Root, out var volume) ? volume.ToString() : null)
        .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)];
}

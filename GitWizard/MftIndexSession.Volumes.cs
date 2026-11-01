using System.Collections.Concurrent;
using MFTLib.Index;

namespace GitWizard;

public sealed partial class MftIndexSession
{
    readonly ConcurrentDictionary<RootKey, VolumeKey> _observedVolumes = new();

    internal void ObserveVolume(RootKey root, VolumeKey volume) => _observedVolumes[root] = volume;

    internal bool TryGetVolume(RootKey root, out VolumeKey volume)
    {
        if (_observedVolumes.TryGetValue(root, out volume) || root.TryGetVolume(out volume))
        {
            return true;
        }

        if (IndexFor(root)?.Status.Watch.CheckpointLoss is { } loss)
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

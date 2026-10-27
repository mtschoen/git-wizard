using MFTLib.Index;

namespace GitWizard;

/// <summary>Turns persisted drive-spec strings into index root keys in one place.</summary>
static class RootKeyProjection
{
    /// <summary>
    /// Parses each drive spec ("C", "C:" or "C:\") as a volume root, skips unparseable entries and keeps the first
    /// occurrence of a repeated root, so persisted lists with stale or duplicate entries project the same way everywhere.
    /// </summary>
    internal static RootKey[] ParseRootKeys(IEnumerable<string> driveSpecs) =>
        [.. driveSpecs
            .Select(value => VolumeKey.TryParse(value, out var volume) ? (RootKey?)volume : null)
            .OfType<RootKey>()
            .Distinct()];

    /// <summary>The drive letter of a volume root; the watch pipeline only ever holds volume roots.</summary>
    internal static char VolumeLetter(this RootKey root) =>
        root.TryGetVolume(out var volume)
            ? volume.Letter
            : throw new InvalidOperationException($"Root {root} is not a volume.");
}

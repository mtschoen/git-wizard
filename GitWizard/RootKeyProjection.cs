using GitWizard.Watch;
using MFTLib.Index;

namespace GitWizard;

/// <summary>Turns configured root strings into index root keys in one place.</summary>
static class RootKeyProjection
{
    /// <summary>
    /// Parses volume specifications and fully qualified directory paths, skips unparseable entries and keeps the first
    /// occurrence of a repeated root, so persisted lists with stale or duplicate entries project the same way everywhere.
    /// </summary>
    internal static RootKey[] ParseRootKeys(IEnumerable<string> driveSpecs) =>
        [.. driveSpecs
            .Select(value => RootKey.TryParse(value, out var root) ? (RootKey?)root : null)
            .OfType<RootKey>()
            .Distinct()];

    /// <summary>The drive letter containing a Windows volume or directory root.</summary>
    internal static char VolumeLetter(this RootKey root)
    {
        if (root.TryGetVolume(out var volume))
        {
            return volume.Letter;
        }

        if (WatchPath.Parse(root.ToString()).Volume is [var letter] && char.IsAsciiLetter(letter))
        {
            return char.ToUpperInvariant(letter);
        }

        throw new InvalidOperationException($"Root {root} does not name a lettered volume.");
    }
}

using GitWizard.Watch;

namespace GitWizardUI.ViewModels;

/// <summary>Keys a per-session, per-volume version slot: the volume component compares as a watch key (POSIX roots ordinally, drives case-folded).</summary>
sealed class VersionKeyComparer : IEqualityComparer<(Guid Session, string Volume)>
{
    public static VersionKeyComparer Instance { get; } = new();

    public bool Equals((Guid Session, string Volume) x, (Guid Session, string Volume) y) =>
        x.Session == y.Session && WatchKeyComparer.Instance.Equals(x.Volume, y.Volume);

    public int GetHashCode((Guid Session, string Volume) obj) =>
        HashCode.Combine(obj.Session, WatchKeyComparer.Instance.GetHashCode(obj.Volume));
}

using GitWizard.Watch;
using MFTLib.Index;

namespace GitWizard.Discovery;

// The index supplies final paths. This projects policy coordinates onto those observations;
// it never resolves links or classifies a filesystem itself.
static class ResolvedDiscoveryScope
{
    internal static DiscoveryScope Create(FileIndexSet index, DiscoveryScope scope)
    {
        var mappings = index.Indices.Where(member => member.Status.Block is not null)
            .Select(member => (Configured: ConfiguredPath(member.Status.Root), Resolved: member.Root().Path))
            .OrderByDescending(item => item.Configured.Comparison.Length).ToArray();
        string Translate(string path)
        {
            var parsed = WatchPath.Parse(path);
            foreach (var mapping in mappings)
            {
                if (parsed.IsUnderOrEqual(mapping.Configured))
                {
                    var relative = parsed.Original[mapping.Configured.Original.Length..].TrimStart('\\', '/');
                    return relative.Length == 0 ? mapping.Resolved : Path.Combine(mapping.Resolved, relative);
                }
            }
            return path;
        }

        var ignored = scope.IgnoredRoots.Select(Translate).ToList();
        foreach (var mapping in mappings)
        {
            if (scope.IgnoredRoots.Any(root => mapping.Configured.IsUnderOrEqual(WatchPath.Parse(root))))
            {
                ignored.Add(mapping.Resolved);
            }
        }
        return DiscoveryScope.CreateExpanded(scope.Request, [.. scope.SearchRoots.Select(Translate)],
            [.. scope.RetainedRoots.Select(Translate)], ignored, scope.ExpansionErrors);
    }

    static WatchPath ConfiguredPath(RootKey root) => WatchPath.Parse(
        root.TryGetVolume(out var volume) ? $"{volume}:\\" : root.ToString());
}
